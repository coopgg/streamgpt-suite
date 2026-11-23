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

## Setup

1. **Copy the files.** Put `nowplaying.ps1` in `C:\StreamGPT\NowPlaying\`. (Somewhere else is fine, just change `ScriptPath` at the top of `NowPlaying.cs`.)
2. **Turn on the WebSocket server.** In Streamer.bot go to Servers/Clients, WebSocket Server, tick Auto Start and click Start. The default `127.0.0.1:8080` is fine.
3. **Create the action.** Make a new action named exactly `Now Playing`. Add the sub-action Core, C#, Execute C# Code, paste in all of `NowPlaying.cs`, and click Save and Compile. It starts by itself every time Streamer.bot opens, so it needs no trigger.
4. **Add `!song` (optional).** Create a command `!song` and add it as a trigger on the same `Now Playing` action.
5. **Add the overlay.** In OBS add a Browser Source, tick Local file, pick `overlay.html`, and set the size to 800 x 200. Play a song.

To position the overlay without playing music, use the URL `overlay.html?demo=1` (untick Local file and paste the full `file:///C:/...` path with `?demo=1` on the end).

## Overlay options

Add these to the end of the URL, for example `overlay.html?align=right&accent=7ddc9a&hide=0`.

| Option | Default | What it does |
| --- | --- | --- |
| `align` | `left` | `left` or `right` |
| `accent` | `ff5a5a` | Hex color for the label and progress bar |
| `scale` | `1` | Size multiplier, e.g. `1.5` |
| `hide` | `5` | Seconds after you pause before the card hides. `0` keeps it up. |
| `popup` | `0` | Show for this many seconds on each new song, then hide. `0` shows it the whole time music plays. |
| `source` | `1` | `0` hides the app name |
| `time` | `1` | `0` hides the elapsed and total time |
| `progress` | `1` | `0` hides the progress bar |
| `port` / `host` | `8080` / `127.0.0.1` | Streamer.bot WebSocket server |
| `password` | none | Only if you turned on WebSocket authentication |
| `demo` | `0` | `1` cycles fake songs |

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
