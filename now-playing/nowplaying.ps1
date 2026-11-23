<#
  StreamGPT Now Playing watcher

  Reads the Windows media session (the same info the Windows volume flyout
  shows), so it works with Spotify, YouTube and YouTube Music in a browser,
  Apple Music, Tidal, Deezer, Amazon Music, foobar2000, MusicBee and anything
  else that reports to Windows.

  It prints one JSON line to stdout every time something changes. The
  Streamer.bot "[Spotify] Now Playing" action starts this script and reads those lines.

  Test it by itself in a PowerShell window:
    powershell -ExecutionPolicy Bypass -File C:\StreamGPT\NowPlaying\nowplaying.ps1
  Play a song and you should see a line of JSON. Press Ctrl+C to stop.
#>
param(
  [int]$ParentPid = 0,        # exit when this process (Streamer.bot) closes
  [int]$IntervalMs = 1000,    # how often to check, in milliseconds
  [string]$Only = "",         # comma separated app ids to use, nothing else, e.g. "spotify"
  [string]$Ignore = "",       # comma separated app ids to skip, e.g. "chrome,msedge"
  [int]$HeartbeatSec = 15     # resend while playing so overlays stay in sync
)

$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = New-Object System.Text.UTF8Encoding $false

Add-Type -AssemblyName System.Runtime.WindowsRuntime
$null = [Windows.Media.Control.GlobalSystemMediaTransportControlsSessionManager, Windows.Media.Control, ContentType = WindowsRuntime]
$null = [Windows.Storage.Streams.IRandomAccessStreamWithContentType, Windows.Storage.Streams, ContentType = WindowsRuntime]
$null = [Windows.Storage.Streams.DataReader, Windows.Storage.Streams, ContentType = WindowsRuntime]

$MgrType    = [Windows.Media.Control.GlobalSystemMediaTransportControlsSessionManager]
$PropsType  = [Windows.Media.Control.GlobalSystemMediaTransportControlsSessionMediaProperties]
$StreamType = [Windows.Storage.Streams.IRandomAccessStreamWithContentType]

# WinRT async calls have to be turned into .NET Tasks before PowerShell can wait on them.
$AsTaskOp = [System.WindowsRuntimeSystemExtensions].GetMethods() | Where-Object {
  $_.Name -eq 'AsTask' -and $_.GetParameters().Count -eq 1 -and
  $_.GetParameters()[0].ParameterType.Name -eq 'IAsyncOperation`1'
} | Select-Object -First 1

function Await($op, [Type]$type) {
  $task = $AsTaskOp.MakeGenericMethod($type).Invoke($null, @($op))
  if (-not $task.Wait(5000)) { throw "Windows media call timed out" }
  return $task.Result
}

$IgnoreList = @($Ignore.Split(',') | ForEach-Object { $_.Trim().ToLowerInvariant() } | Where-Object { $_ })
$OnlyList = @($Only.Split(',') | ForEach-Object { $_.Trim().ToLowerInvariant() } | Where-Object { $_ })

# An app is skipped if it is on the ignore list, or if there is an only list and it is not on it.
function Test-Ignored([string]$appId) {
  $l = ([string]$appId).ToLowerInvariant()
  if ($OnlyList.Count -gt 0) {
    $allowed = $false
    foreach ($o in $OnlyList) { if ($l.Contains($o)) { $allowed = $true } }
    if (-not $allowed) { return $true }
  }
  if (-not $appId) { return $false }
  foreach ($i in $IgnoreList) { if ($l.Contains($i)) { return $true } }
  return $false
}

function Get-SourceName([string]$appId) {
  if (-not $appId) { return 'Unknown' }
  $l = $appId.ToLowerInvariant()
  $map = [ordered]@{
    'spotify' = 'Spotify'; 'applemusic' = 'Apple Music'; 'itunes' = 'iTunes'
    'youtube-music' = 'YouTube Music'; 'youtubemusic' = 'YouTube Music'
    'tidal' = 'TIDAL'; 'deezer' = 'Deezer'; 'amazon' = 'Amazon Music'
    'cider' = 'Cider'; 'plexamp' = 'Plexamp'; 'foobar' = 'foobar2000'
    'musicbee' = 'MusicBee'; 'winamp' = 'Winamp'; 'aimp' = 'AIMP'; 'vlc' = 'VLC'
    'zunemusic' = 'Media Player'; 'soundcloud' = 'SoundCloud'
    'msedge' = 'Edge'; 'chrome' = 'Chrome'; 'firefox' = 'Firefox'
    '308046b0af4a39cb' = 'Firefox'; 'brave' = 'Brave'; 'opera' = 'Opera'; 'vivaldi' = 'Vivaldi'
  }
  foreach ($k in $map.Keys) { if ($l.Contains($k)) { return $map[$k] } }
  $name = ($appId -split '!')[0] -replace '\.exe$', ''
  if ($name -match '^[^_]+_[a-z0-9]{13}$') { $name = ($name -split '_')[0] }  # store app ids
  $name = ($name -split '\.')[-1]
  return $name
}

# Reads the cover into bytes. Windows' own DataReader first, reading in chunks until the stream
# runs out: PowerShell 5.1 often cannot see the stream's Size, which reads as 0, so it is not used.
# The .NET stream bridge is a second try. Problems go to stderr, which Streamer.bot logs.
$script:artError = $null
function Get-ArtBytes($thumb) {
  try {
    $ras = Await ($thumb.OpenReadAsync()) $StreamType
    $reader = [Windows.Storage.Streams.DataReader]::new($ras)
    $ms = New-Object System.IO.MemoryStream
    while ($true) {
      $n = [uint32](Await ($reader.LoadAsync(65536)) ([uint32]))
      if ($n -eq 0) { break }
      $chunk = New-Object byte[] $n
      $reader.ReadBytes($chunk)
      $ms.Write($chunk, 0, $chunk.Length)
      if ($ms.Length -gt 2MB) { break }
    }
    $reader.Dispose()
    if ($ms.Length -gt 0) { return ,$ms.ToArray() }
    $first = 'the cover was empty'
  } catch { $first = $_.Exception.Message }
  try {
    # A fresh stream: the first try may have used this one up.
    $ras = Await ($thumb.OpenReadAsync()) $StreamType
    $src = [System.IO.WindowsRuntimeStreamExtensions]::AsStreamForRead($ras)
    $ms = New-Object System.IO.MemoryStream
    $src.CopyTo($ms)
    $src.Dispose()
    if ($ms.Length -gt 0) { return ,$ms.ToArray() }
    throw 'the cover was empty'
  } catch { throw "DataReader: $first / stream: $($_.Exception.Message)" }
}

# Says once per problem why there is no cover, so a missing cover is never a mystery.
function Write-ArtProblem([string]$msg) {
  if ($msg -ne $script:artError) { [Console]::Error.WriteLine("nowplaying: $msg") }
  $script:artError = $msg
}

function Get-ArtDataUri($props) {
  if ($null -eq $props.Thumbnail) { Write-ArtProblem 'the app has not shared a cover for this song (yet)'; return $null }
  try {
    $bytes = Get-ArtBytes $props.Thumbnail
    if ($bytes.Length -gt 2MB) { Write-ArtProblem 'the cover is over 2 MB, skipped'; return $null }
    # Tell PNG from JPEG by the first bytes (the stream's own type is often not visible from PowerShell).
    $ct = if ($bytes.Length -gt 3 -and $bytes[0] -eq 0x89 -and $bytes[1] -eq 0x50) { 'image/png' } else { 'image/jpeg' }
    $script:artError = $null
    return "data:$ct;base64," + [Convert]::ToBase64String($bytes)
  } catch {
    Write-ArtProblem ("could not read the cover: " + $_.Exception.Message)
    return $null
  }
}

$script:lastAppId = $null
function Select-Session($mgr) {
  $sessions = @($mgr.GetSessions() | Where-Object { -not (Test-Ignored $_.SourceAppUserModelId) })
  if ($sessions.Count -eq 0) { return $null }
  $playing = @($sessions | Where-Object { $_.GetPlaybackInfo().PlaybackStatus -eq 'Playing' })
  # Stick with the app we were already showing if it is still playing.
  $sticky = $playing | Where-Object { $_.SourceAppUserModelId -eq $script:lastAppId } | Select-Object -First 1
  if ($sticky) { return $sticky }
  if ($playing.Count -gt 0) { return $playing[0] }
  $cur = $mgr.GetCurrentSession()
  if ($cur -and -not (Test-Ignored $cur.SourceAppUserModelId)) { return $cur }
  $last = $sessions | Where-Object { $_.SourceAppUserModelId -eq $script:lastAppId } | Select-Object -First 1
  if ($last) { return $last }
  return $sessions[0]
}

function Emit($state) {
  # Run by hand (no Streamer.bot), print the cover's size instead of the long image text so the line stays readable.
  if ($ParentPid -eq 0) {
    $shown = [ordered]@{}
    foreach ($k in $state.Keys) { $shown[$k] = $state[$k] }
    $shown.art = if ($state.art) { "(cover found: {0} KB)" -f [Math]::Round($state.art.Length * 0.75 / 1KB) } else { '(no cover)' }
    $state = $shown
  }
  [Console]::Out.WriteLine(($state | ConvertTo-Json -Compress))
  [Console]::Out.Flush()
}

$mgr = Await ($MgrType::RequestAsync()) $MgrType

$lastSig = $null
$lastTrack = $null
$art = $null
$artTries = 0
$lastEmit = [DateTime]::MinValue
$expectedPos = 0.0
$lastTick = [DateTime]::UtcNow

while ($true) {
  if ($ParentPid -gt 0 -and -not (Get-Process -Id $ParentPid -ErrorAction SilentlyContinue)) { exit 0 }

  try {
    $now = [DateTime]::UtcNow
    $elapsed = ($now - $lastTick).TotalSeconds
    $lastTick = $now

    $s = Select-Session $mgr
    if ($null -eq $s) {
      $state = [ordered]@{ type = 'nowplaying'; playing = $false; status = 'None'; title = ''; artist = ''; album = ''
        source = ''; appId = ''; position = 0; duration = 0; art = $null; ts = [DateTimeOffset]::UtcNow.ToUnixTimeMilliseconds() }
      if ($lastSig -ne 'none') { Emit $state; $lastSig = 'none'; $lastTrack = $null; $lastEmit = $now }
    }
    else {
      $appId  = [string]$s.SourceAppUserModelId
      $script:lastAppId = $appId
      $status = [string]$s.GetPlaybackInfo().PlaybackStatus
      $isPlaying = $status -eq 'Playing'
      $props  = Await ($s.TryGetMediaPropertiesAsync()) $PropsType
      $title  = [string]$props.Title
      $artist = [string]$props.Artist
      if (-not $artist) { $artist = [string]$props.AlbumArtist }
      $album  = [string]$props.AlbumTitle

      $tl = $s.GetTimelineProperties()
      $duration = [Math]::Max(0, ($tl.EndTime - $tl.StartTime).TotalSeconds)
      $position = $tl.Position.TotalSeconds
      if ($isPlaying -and $tl.LastUpdatedTime.Year -gt 2000) {
        $position += ([DateTimeOffset]::Now - $tl.LastUpdatedTime).TotalSeconds
      }
      if ($duration -gt 0) { $position = [Math]::Min($position, $duration) }
      $position = [Math]::Max(0, $position)

      $track = "$appId|$title|$artist|$album"
      if ($track -ne $lastTrack) { $lastTrack = $track; $art = $null; $artTries = 0 }

      # Spotify can change the title a few seconds before the cover arrives, so keep looking for 10 checks.
      $artChanged = $false
      if ($artTries -lt 10) {
        $artTries++
        $newArt = Get-ArtDataUri $props
        if ($newArt -and $newArt -ne $art) { $art = $newArt; $artChanged = $true }
      }

      if ($isPlaying) { $expectedPos += $elapsed }
      $seeked = [Math]::Abs($position - $expectedPos) -gt 3

      $sig = "$track|$status|$([Math]::Round($duration))"
      $heartbeat = $isPlaying -and ($now - $lastEmit).TotalSeconds -ge $HeartbeatSec

      if ($sig -ne $lastSig -or $artChanged -or $seeked -or $heartbeat) {
        Emit ([ordered]@{
          type = 'nowplaying'; playing = $isPlaying; status = $status
          title = $title; artist = $artist; album = $album
          source = (Get-SourceName $appId); appId = $appId
          position = [Math]::Round($position, 1); duration = [Math]::Round($duration, 1)
          art = $art; ts = [DateTimeOffset]::UtcNow.ToUnixTimeMilliseconds()
        })
        $lastSig = $sig
        $lastEmit = $now
      }
      $expectedPos = $position
    }
  }
  catch {
    [Console]::Error.WriteLine("nowplaying: " + $_.Exception.Message)
    try { $mgr = Await ($MgrType::RequestAsync()) $MgrType } catch { }
    Start-Sleep -Seconds 2
  }

  Start-Sleep -Milliseconds $IntervalMs
}
