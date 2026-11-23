# StreamGPT Now Playing

A now playing overlay and `!song` command that works with any music app, built on Streamer.bot like the rest of the suite.

It reads the song from Windows itself (the same info the volume flyout shows), so there are no API keys, no logins and no extra programs. It works with Spotify, YouTube and YouTube Music in Chrome, Edge or Firefox, Apple Music, TIDAL, Deezer, Amazon Music, foobar2000, MusicBee, Plexamp and anything else that shows up in that flyout.

## How it works

```
Music app ──> Windows media session ──> nowplaying.ps1 ──> "Now Playing" action ──> Streamer.bot WebSocket ──> overlay.html
                                                                │
                                                                └──> !song, globals, "Song Changed" trigger
```

| File | What it is |
| --- | --- |
| `nowplaying.ps1` | Watches Windows for the current song and prints a line of JSON when it changes. |
| `NowPlaying.cs` | The Streamer.bot action. Starts the watcher, handles `!song`, sends updates to the overlay. |
| `overlay.html` | The OBS Browser Source. |
| `editor.html` | Open it in your browser to design the overlay with live changes, then copy the link into OBS. |

## Setup

1. **Copy the files.** Put `nowplaying.ps1` in `C:\StreamGPT\NowPlaying\`. (Somewhere else is fine, just change `ScriptPath` at the top of `NowPlaying.cs`.)
2. **Turn on the WebSocket server.** In Streamer.bot go to Servers/Clients, WebSocket Server, tick Auto Start and click Start. The default `127.0.0.1:8080` is fine.
3. **Create the action.** Make a new action named exactly `Now Playing`. Add the sub-action Core, C#, Execute C# Code, paste in all of `NowPlaying.cs`, and click Save and Compile. It starts by itself every time Streamer.bot opens, so it needs no trigger.
4. **Add `!song` (optional).** Create a command `!song` and add it as a trigger on the same `Now Playing` action.
5. **Design it.** Double-click `editor.html`, pick a theme and settings, and click **Copy link for OBS** (see "Design it live" below).
6. **Add the overlay.** In OBS add a Browser Source, leave Local file unticked, paste the link into URL, and set the size to 800 x 400. Play a song.

Keep `nowplaying.ps1` where `ScriptPath` points, and keep `overlay.html` and `editor.html` together in one folder (any folder; the link points at wherever they are).

## Design it live

Double-click `editor.html` (keep it in the same folder as `overlay.html`). Change the theme, colors, size, side of the screen and what's shown, and the overlay beside the controls updates as you go. Preview it with a sample song, your own cover image, or your real music through Streamer.bot or SMTC Bridge, over a dark, bright or transparent background. When it looks right, click **Copy link for OBS** and paste it into the Browser Source's URL (untick Local file).

## Overlay options

Add these to the end of the URL, for example `overlay.html?theme=strip&align=right&hide=0`.

| Option | Default | What it does |
| --- | --- | --- |
| `theme` | `field` | `field` (the cover beside a block of its color), `strip` (one slim line), `bare` (no block, just the cover and text over your scene), `poster` (a big cover with the text underneath), `vinyl` (just the record, no box or text: the cover is its center label, it turns while music plays and stops when you pause; `progress=1` adds a progress ring around it) or `glow` (the cover glows in its own colors, from a blurred copy of the cover itself; the one theme that breaks the no-glow design rules, so it's opt-in) |
| `color` | `auto` | `auto` gives the overlay each song's main cover color, with light or dark text, whichever reads better. Or set a fixed hex color like `15171c`. |
| `align` | `left` | `left` or `right` |
| `corners` | `round` | `round` or `square` (cards, covers and the strip pill) |
| `scale` | `1` | Size multiplier, e.g. `1.5` |
| `hide` | `5` | Seconds after you pause before the card hides. `0` keeps it up. |
| `popup` | `0` | Show for this many seconds on each new song, then hide. `0` shows it the whole time music plays. |
| `source` | `1` | `0` hides the app name |
| `time` | `1` | `0` hides the elapsed and total time. Vinyl hides it unless you set `time=1`. |
| `progress` | `1` | `0` hides the progress bar |
| `rpm` | `33` | Vinyl only: how fast the record turns. `45` is faster, `10` is a slow spin. |
| `port` / `host` | `8080` / `127.0.0.1` | Streamer.bot WebSocket server |
| `password` | none | Only if you turned on WebSocket authentication |
| `demo` | `0` | `1` cycles fake songs |

## Already running SMTC Bridge?

If you (or someone you set this up for) already use nutty's [SMTC Bridge](https://github.com/nuttylmao/smtc-bridge) tray app, the overlay can read from it directly, with no Streamer.bot needed: add `?bridge=1` to the overlay URL (it reads `http://127.0.0.1:5000`), or `?bridge=192.168.1.20:5000` for another PC. All the themes and colors work the same. `!song`, the globals and the Song Changed trigger need the Streamer.bot setup above.

## Settings in `NowPlaying.cs`

- `IgnoreApps`: apps to skip, comma separated, e.g. `"chrome,msedge"` if you watch YouTube videos in the browser and do not want them showing up.
- `SongMessage`: the `!song` reply. `{title}`, `{artist}`, `{album}` and `{source}` are filled in.

## Using it in other actions

- **Globals** (not saved between restarts): `np_title`, `np_artist`, `np_album`, `np_source`, `np_playing`.
- **Trigger**: Custom, StreamGPT, `Now Playing: Song Changed` fires on every new song, with `%npTitle%`, `%npArtist%`, `%npAlbum%` and `%npSource%`. Use it to post the song in chat, have the AI comment on it, and so on.

## Troubleshooting

- **Nothing shows up.** Open PowerShell and run `powershell -ExecutionPolicy Bypass -File C:\StreamGPT\NowPlaying\nowplaying.ps1`, then play a song. If you see a line of JSON, the watcher works and the problem is in Streamer.bot or OBS. If you see an error, that error is the problem.
- **Check the Streamer.bot log.** Every message from this starts with `[NowPlaying]`.
- **The song is right but there is no cover.** Some apps do not share artwork with Windows. Browsers only share it for sites that support it (YouTube and Spotify web do).
- **Two apps are playing.** It sticks with the one it was already showing. Use `IgnoreApps` to rule one out.
- **Spotify's progress bar jumps.** Spotify reports the position to Windows only now and then. The overlay keeps counting between updates and corrects itself.
