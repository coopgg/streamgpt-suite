// StreamGPT Now Playing: Streamer.bot C# action
//
// Paste this into an action named exactly "Now Playing" (Execute C# Code sub-action).
// It starts nowplaying.ps1 in the background when Streamer.bot loads, reads the
// song info it prints, and broadcasts it to the overlay over the Streamer.bot
// WebSocket server.
//
// What running the action does:
//   no arguments              restart the watcher
//   from a command (!song)    post the current song in chat
//   npMode = "resend"         rebroadcast the current song (the overlay does this on connect)
//   npMode = "stop"           stop the watcher
//
// It also sets these globals (not persisted): np_title, np_artist, np_album,
// np_source, np_playing. And it fires the custom trigger
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
    // Apps to skip, comma separated, matched against the app id. Example: "chrome,msedge"
    private const string IgnoreApps = "";
    // Chat reply for !song. {title} {artist} {album} {source} are filled in.
    private const string SongMessage = "Now playing: {title} by {artist} ({source})";
    private const string NothingMessage = "Nothing is playing right now.";
    // ------------------------------

    private const string TriggerEvent = "nowPlayingSongChanged";
    private const string PidGlobal = "np_watcherPid";

    private readonly object gate = new object();
    private Process watcher;
    private bool stopping;
    private int crashCount;
    private string latestJson;
    private JObject latest;
    private string lastTrackKey;

    public void Init()
    {
        try
        {
            CPH.RegisterCustomTrigger("Now Playing: Song Changed", TriggerEvent, new[] { "StreamGPT" });
        }
        catch (Exception ex) { SafeLog("Could not register trigger: " + ex.Message); }
        StartWatcher();
    }

    public void Dispose()
    {
        StopWatcher();
    }

    public bool Execute()
    {
        string mode = "";
        object raw;
        if (args.TryGetValue("npMode", out raw) && raw != null) mode = raw.ToString().Trim().ToLowerInvariant();
        else if (args.ContainsKey("command")) mode = "song";

        switch (mode)
        {
            case "resend":
                Broadcast();
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

            var p = new Process { StartInfo = psi, EnableRaisingEvents = true };
            p.OutputDataReceived += (s, e) => { if (e.Data != null) HandleLine(e.Data); };
            p.ErrorDataReceived += (s, e) => { if (!string.IsNullOrWhiteSpace(e.Data)) SafeLog(e.Data); };
            p.Exited += (s, e) => OnExited(p);

            try
            {
                p.Start();
                p.BeginOutputReadLine();
                p.BeginErrorReadLine();
                watcher = p;
                CPH.SetGlobalVar(PidGlobal, p.Id, false);
                SafeLog("Watcher started (pid " + p.Id + ")");
            }
            catch (Exception ex)
            {
                SafeLog("Could not start PowerShell: " + ex.Message);
            }
        }
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
            SafeLog("Watcher keeps exiting. Check the log lines above, then run the Now Playing action to try again.");
            return;
        }
        SafeLog("Watcher exited, restarting in 5 seconds");
        Task.Delay(5000).ContinueWith(_ => { if (!stopping) StartWatcher(); });
    }

    private void HandleLine(string data)
    {
        string line = data.Trim().TrimStart('\uFEFF');
        if (!line.StartsWith("{")) return;

        JObject o;
        try { o = JObject.Parse(line); }
        catch { return; }

        string title = (string)o["title"] ?? "";
        string artist = (string)o["artist"] ?? "";
        string album = (string)o["album"] ?? "";
        string source = (string)o["source"] ?? "";
        bool playing = o["playing"] != null && (bool)o["playing"];

        lock (gate)
        {
            latest = o;
            latestJson = o.ToString(Newtonsoft.Json.Formatting.None);
            crashCount = 0;
        }

        CPH.SetGlobalVar("np_title", title, false);
        CPH.SetGlobalVar("np_artist", artist, false);
        CPH.SetGlobalVar("np_album", album, false);
        CPH.SetGlobalVar("np_source", source, false);
        CPH.SetGlobalVar("np_playing", playing, false);

        Broadcast();

        string key = title + "|" + artist;
        if (playing && title != "" && key != lastTrackKey)
        {
            lastTrackKey = key;
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
        try { CPH.WebsocketBroadcastJson(json); }
        catch (Exception ex) { SafeLog("Broadcast failed: " + ex.Message); }
    }

    private void SendSongToChat()
    {
        JObject o;
        lock (gate) { o = latest; }
        string title = o == null ? "" : ((string)o["title"] ?? "");
        bool playing = o != null && o["playing"] != null && (bool)o["playing"];

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
