// StreamGPT Now Playing: Streamer.bot C# action
//
// Paste this into an action named exactly "[Spotify] Now Playing" (Execute C# Code sub-action).
//
// If compiling says "The type or namespace name 'Process' could not be found", the code needs
// Windows' System.dll: click Find Refs at the bottom of the code window, or on the References tab
// right-click > Add reference from file > C:\Windows\Microsoft.NET\Framework64\v4.0.30319\System.dll,
// then Save and Compile again.
//
// The overlay calls the action by that name to get the current song when it loads. If you name it
// something else, put the name in the editor's "Action name" box (or add &action=<name> to the link).
// It starts nowplaying.ps1 in the background when Streamer.bot loads, reads the
// song info it prints, and broadcasts it to the overlay over the Streamer.bot
// WebSocket server.
//
// What running the action does:
//   no arguments              restart the watcher
//   from a command (!song)    post the current song in chat
//   npMode = "resend"         rebroadcast the look and current song (the overlay does this on connect)
//   npMode = "look"           save the look in npLook and push it to every overlay (editor.html does this)
//   npMode = "replay"         make every overlay hide and show again, to watch its animations
//   npMode = "stop"           stop the watcher
//
// The look is kept in the persisted global np_look, so OBS gets it back after a restart.
// During Spotify ads the song globals keep the last song, np_ad is true, the Song Changed trigger
// does not fire, and !song says an ad is on.
//
// It also sets these globals (not persisted): np_title, np_artist, np_album,
// np_source, np_playing, np_ad. And it fires the custom trigger
// "Now Playing: Song Changed" (Custom > StreamGPT) with the same values as
// arguments (%npTitle%, %npArtist%, %npAlbum%, %npSource%).

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;

public class CPHInline
{
    // ---------- Settings ----------
    // Where nowplaying.ps1 lives.
    private const string ScriptPath = @"C:\StreamGPT\NowPlaying\nowplaying.ps1";
    // Only these apps are used, comma separated, matched against the app id. Everything else
    // (browsers, YouTube, games...) is ignored. Set to "" to use any app.
    private const string OnlyApps = "spotify";
    // Apps to skip, comma separated, when OnlyApps is "". Example: "chrome,msedge"
    private const string IgnoreApps = "";
    // Chat reply for !song. {title} {artist} {album} {source} are filled in.
    private const string SongMessage = "Now playing: {title} by {artist} ({source})";
    private const string NothingMessage = "Nothing is playing right now.";
    private const string AdMessage = "An ad is playing. The music will be right back.";
    // ------------------------------

    private const string TriggerEvent = "nowPlayingSongChanged";
    private const string PidGlobal = "np_watcherPid";
    private const string LookGlobal = "np_look";

    // Static, so every run of the action sees the same watcher, song, cover and look, even if
    // Streamer.bot runs a request (an overlay asking for the current song) on a fresh instance.
    private static string lookParams;
    private static readonly object gate = new object();
    private static Process watcher;
    private static bool stopping;
    private static bool coverLogged;
    private static bool firstLineLogged;
    private static string latestArt;
    private static string latestArtId;
    private static int crashCount;
    private static string latestJson;
    private static JObject latest;
    private static string lastTrackKey;

    public void Init()
    {
        try
        {
            CPH.RegisterCustomTrigger("Now Playing: Song Changed", TriggerEvent, new[] { "StreamGPT" });
        }
        catch (Exception ex) { SafeLog("Could not register trigger: " + ex.Message); }
        try { lookParams = CPH.GetGlobalVar<string>(LookGlobal, true); } catch { }
        StartWatcher();
    }

    // Only the copy of this code that started the watcher stops it when Streamer.bot disposes it
    // (on recompile or exit), so a short-lived copy used for one request never stops it.
    private static CPHInline owner;
    public void Dispose()
    {
        if (owner == this || owner == null) StopWatcher();
    }

    public bool Execute()
    {
        string mode = "";
        object raw;
        if (args.TryGetValue("npMode", out raw) && raw != null) mode = raw.ToString().Trim().ToLowerInvariant();
        else if (args.ContainsKey("command")) mode = "song";

        // Whatever ran the action (!song, an overlay connecting, the editor), make sure the watcher
        // is running: recompiling the action stops it, and nothing else would start it again.
        if (mode != "stop" && mode != "")
        {
            bool started = EnsureWatcher();
            // !song right after a restart: give the watcher a moment to report the current song.
            if (started && mode == "song")
            {
                for (int i = 0; i < 30 && latestJson == null; i++) System.Threading.Thread.Sleep(100);
            }
        }

        switch (mode)
        {
            case "resend":
                BroadcastLook();
                Broadcast();
                int sentParts = BroadcastArt();
                SafeLog(latestJson == null
                    ? "An overlay asked for the current song: nothing yet (is Spotify playing?)"
                    : "An overlay asked for the current song: sent it" + (sentParts > 0 ? " with its cover (" + sentParts + " parts)" : ", no cover yet"));
                return true;
            case "look":
                object lookRaw;
                string look = args.TryGetValue("npLook", out lookRaw) && lookRaw != null ? lookRaw.ToString() : "";
                if (look.Length > 4000) return false;
                // Saved to disk only when it actually changed (the editor sends while you drag sliders).
                if (look != lookParams)
                {
                    lookParams = look;
                    CPH.SetGlobalVar(LookGlobal, look, true);
                }
                BroadcastLook();
                return true;
            case "replay":
                SendToOverlays(new JObject { ["type"] = "nowplaying-replay" });
                return true;
            case "song":
                SendSongToChat();
                return true;
            case "stop":
                StopWatcher();
                return true;
            default:
                StopWatcher();
                crashCount = 0;
                StartWatcher();
                return true;
        }
    }

    private void StartWatcher()
    {
        lock (gate)
        {
            if (watcher != null && !watcher.HasExited) return;
            stopping = false;
            KillOrphan();

            if (!File.Exists(ScriptPath))
            {
                SafeLog("Script not found at " + ScriptPath);
                return;
            }

            string arguments = "-NoProfile -NonInteractive -ExecutionPolicy Bypass -WindowStyle Hidden -File \""
                + ScriptPath + "\" -ParentPid " + Process.GetCurrentProcess().Id;
            if (!string.IsNullOrWhiteSpace(OnlyApps)) arguments += " -Only \"" + OnlyApps.Replace("\"", "") + "\"";
            if (!string.IsNullOrWhiteSpace(IgnoreApps)) arguments += " -Ignore \"" + IgnoreApps.Replace("\"", "") + "\"";

            var psi = new ProcessStartInfo
            {
                FileName = "powershell.exe",
                Arguments = arguments,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = new UTF8Encoding(false),
                StandardErrorEncoding = new UTF8Encoding(false)
            };

            // These run on background threads, outside any action run. An exception escaping one
            // would take down all of Streamer.bot, so each catches everything and logs it instead.
            var p = new Process { StartInfo = psi, EnableRaisingEvents = true };
            p.OutputDataReceived += (s, e) =>
            {
                try { if (e.Data != null) HandleLine(e.Data); }
                catch (Exception ex) { SafeLog("Skipped a line from the watcher: " + ex.Message); }
            };
            p.ErrorDataReceived += (s, e) =>
            {
                try { if (!string.IsNullOrWhiteSpace(e.Data)) SafeLog(e.Data); } catch { }
            };
            p.Exited += (s, e) =>
            {
                try { OnExited(p); } catch (Exception ex) { SafeLog("Watcher restart failed: " + ex.Message); }
            };

            try
            {
                p.Start();
                p.BeginOutputReadLine();
                p.BeginErrorReadLine();
                watcher = p;
                owner = this;
                firstLineLogged = false;
                CPH.SetGlobalVar(PidGlobal, p.Id, false);
                SafeLog("Watcher started (pid " + p.Id + ")");
            }
            catch (Exception ex)
            {
                SafeLog("Could not start PowerShell: " + ex.Message);
            }
        }
    }

    // Starts the watcher if it is not running. Returns true if it had to start it.
    private bool EnsureWatcher()
    {
        lock (gate)
        {
            if (watcher != null)
            {
                bool exited = true;
                try { exited = watcher.HasExited; } catch { }
                if (!exited) return false;
            }
        }
        SafeLog("Watcher was not running; starting it");
        lock (gate) { latestJson = null; latest = null; } // forget the stale song; the watcher reports the real one
        crashCount = 0;
        StartWatcher();
        return true;
    }

    private void StopWatcher()
    {
        lock (gate)
        {
            stopping = true;
            if (watcher == null) return;
            try { if (!watcher.HasExited) watcher.Kill(); } catch { }
            try { watcher.Dispose(); } catch { }
            watcher = null;
        }
    }

    // If Streamer.bot recompiled the action without a clean shutdown, an old watcher may still be running.
    private void KillOrphan()
    {
        try
        {
            int pid = CPH.GetGlobalVar<int>(PidGlobal, false);
            if (pid <= 0) return;
            var old = Process.GetProcessById(pid);
            if (old.ProcessName.Equals("powershell", StringComparison.OrdinalIgnoreCase)) old.Kill();
        }
        catch { }
    }

    private void OnExited(Process p)
    {
        lock (gate)
        {
            if (stopping || p != watcher) return;
            watcher = null;
            crashCount++;
        }
        if (crashCount > 5)
        {
            SafeLog("Watcher keeps exiting. Check the log lines above, then run the [Spotify] Now Playing action to try again.");
            return;
        }
        SafeLog("Watcher exited, restarting in 5 seconds");
        Task.Delay(5000).ContinueWith(_ =>
        {
            try { if (!stopping) StartWatcher(); }
            catch (Exception ex) { SafeLog("Watcher restart failed: " + ex.Message); }
        });
    }

    private void HandleLine(string data)
    {
        string line = data.Trim().TrimStart('\uFEFF');
        if (!line.StartsWith("{")) return;

        JObject o;
        try { o = JObject.Parse(line); }
        catch (Exception ex) { SafeLog("Could not read a line from the watcher: " + ex.Message); return; }

        // The first line after each start confirms the watcher is working.
        if (!firstLineLogged)
        {
            firstLineLogged = true;
            string t = (string)o["title"];
            SafeLog("Watcher is sending songs. First: " + (string.IsNullOrEmpty(t) ? "nothing playing" : t));
        }

        string title = (string)o["title"] ?? "";
        string artist = (string)o["artist"] ?? "";
        string album = (string)o["album"] ?? "";
        string source = (string)o["source"] ?? "";
        bool playing = o["playing"] != null && (bool)o["playing"];
        bool isAd = o["ad"] != null && o["ad"].Type == JTokenType.Boolean && (bool)o["ad"];

        // The cover travels separately, in small parts (see BroadcastArt): a whole cover in one
        // message is too big for Streamer.bot's WebSocket and the message never arrives.
        string art = (string)o["art"];
        bool hasArt = !string.IsNullOrEmpty(art);
        string artId = hasArt ? art.Length.ToString("x") + "-" + ((uint)art.GetHashCode()).ToString("x") : "";
        o.Remove("art");
        o["artId"] = artId;
        bool artChanged;

        lock (gate)
        {
            latest = o;
            latestJson = o.ToString(Newtonsoft.Json.Formatting.None);
            artChanged = hasArt && artId != latestArtId;
            if (hasArt) { latestArt = art; latestArtId = artId; }
            crashCount = 0;
        }

        CPH.SetGlobalVar("np_ad", isAd, false);
        if (!isAd)
        {
            CPH.SetGlobalVar("np_title", title, false);
            CPH.SetGlobalVar("np_artist", artist, false);
            CPH.SetGlobalVar("np_album", album, false);
            CPH.SetGlobalVar("np_source", source, false);
            CPH.SetGlobalVar("np_playing", playing, false);
        }

        Broadcast();
        if (artChanged) BroadcastArt();

        // Spotify ads: logged once, and no Song Changed trigger.
        if (isAd)
        {
            if (lastTrackKey != "\u0001ad") { lastTrackKey = "\u0001ad"; SafeLog("Spotify ad playing"); }
            return;
        }

        // Log each new song and whether its cover came through, so a missing cover is easy to spot.
        string key = title + "|" + artist;
        if (hasArt && key == lastTrackKey && !coverLogged)
        {
            coverLogged = true;
            SafeLog("Cover arrived for " + title + " (" + (art.Length * 3 / 4 / 1024) + " KB)");
        }
        if (playing && title != "" && key != lastTrackKey)
        {
            lastTrackKey = key;
            coverLogged = hasArt;
            SafeLog("Now playing: " + title + " by " + artist + (hasArt
                ? " (cover: " + (art.Length * 3 / 4 / 1024) + " KB)"
                : " (no cover yet)"));
            try
            {
                CPH.TriggerCodeEvent(TriggerEvent, new Dictionary<string, object>
                {
                    { "npTitle", title }, { "npArtist", artist }, { "npAlbum", album }, { "npSource", source }
                });
            }
            catch (Exception ex) { SafeLog("Trigger failed: " + ex.Message); }
        }
    }

    private void Broadcast()
    {
        string json;
        lock (gate) { json = latestJson; }
        if (json == null) return;
        SendRaw(json);
    }

    // Every message to the overlays goes through here, one at a time: the watcher's thread and
    // action runs (overlays asking, the editor saving the look) can otherwise send at the same moment.
    private static readonly object sendLock = new object();
    private void SendRaw(string json)
    {
        try { lock (sendLock) { CPH.WebsocketBroadcastJson(json); } }
        catch (Exception ex) { SafeLog("Broadcast failed: " + ex.Message); }
    }

    // The look saved by the editor, as overlay link options (theme=vinyl&in=pop...).
    private void BroadcastLook()
    {
        string look = lookParams;
        if (look == null) return;
        SendToOverlays(new JObject { ["type"] = "nowplaying-look", ["params"] = look });
    }

    private void SendToOverlays(JObject msg)
    {
        SendRaw(msg.ToString(Newtonsoft.Json.Formatting.None));
    }

    // Sends the current cover in parts of about 8 KB; the overlay puts them back together by artId.
    private const int ArtPartSize = 8000;
    private int BroadcastArt()
    {
        string art, id;
        lock (gate) { art = latestArt; id = latestArtId; }
        if (string.IsNullOrEmpty(art)) return 0;
        int parts = (art.Length + ArtPartSize - 1) / ArtPartSize;
        try
        {
            for (int i = 0; i < parts; i++)
            {
                var msg = new JObject
                {
                    ["type"] = "nowplaying-art",
                    ["artId"] = id,
                    ["part"] = i,
                    ["parts"] = parts,
                    ["data"] = art.Substring(i * ArtPartSize, Math.Min(ArtPartSize, art.Length - i * ArtPartSize))
                };
                SendRaw(msg.ToString(Newtonsoft.Json.Formatting.None));
            }
            return parts;
        }
        catch (Exception ex) { SafeLog("Sending the cover failed: " + ex.Message); return 0; }
    }

    private void SendSongToChat()
    {
        JObject o;
        lock (gate) { o = latest; }
        string title = o == null ? "" : ((string)o["title"] ?? "");
        bool playing = o != null && o["playing"] != null && (bool)o["playing"];

        if (o != null && o["ad"] != null && o["ad"].Type == JTokenType.Boolean && (bool)o["ad"])
        {
            CPH.SendMessage(AdMessage, true);
            return;
        }
        if (!playing || title == "")
        {
            CPH.SendMessage(NothingMessage, true);
            return;
        }

        string artist = (string)o["artist"] ?? "";
        string msg = SongMessage
            .Replace("{title}", title)
            .Replace("{artist}", artist == "" ? "unknown artist" : artist)
            .Replace("{album}", (string)o["album"] ?? "")
            .Replace("{source}", (string)o["source"] ?? "");
        CPH.SendMessage(msg, true);
    }

    private void SafeLog(string msg)
    {
        try { CPH.LogInfo("[NowPlaying] " + msg); } catch { }
    }
}
