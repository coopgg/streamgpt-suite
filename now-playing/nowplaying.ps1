<#
  StreamGPT Now Playing watcher

  Reads the Windows media session (the same info the Windows volume flyout
  shows), so it works with Spotify, YouTube and YouTube Music in a browser,
  Apple Music, Tidal, Deezer, Amazon Music, foobar2000, MusicBee and anything
  else that reports to Windows.

  It prints one JSON line to stdout every time something changes. The
  Streamer.bot "Now Playing" action starts this script and reads those lines.

  Test it by itself in a PowerShell window:
    powershell -ExecutionPolicy Bypass -File C:\StreamGPT\NowPlaying\nowplaying.ps1
  Play a song and you should see a line of JSON. Press Ctrl+C to stop.
#>
param(
  [int]$ParentPid = 0,        # exit when this process (Streamer.bot) closes
  [int]$IntervalMs = 1000,    # how often to check, in milliseconds
  [string]$Ignore = "",       # comma separated app ids to skip, e.g. "chrome,msedge"
  [int]$HeartbeatSec = 15     # resend while playing so overlays stay in sync
)

$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = New-Object System.Text.UTF8Encoding $false

Add-Type -AssemblyName System.Runtime.WindowsRuntime
$null = [Windows.Media.Control.GlobalSystemMediaTransportControlsSessionManager, Windows.Media.Control, ContentType = WindowsRuntime]
$null = [Windows.Storage.Streams.IRandomAccessStreamWithContentType, Windows.Storage.Streams, ContentType = WindowsRuntime]

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

function Test-Ignored([string]$appId) {
  if (-not $appId) { return $false }
  $l = $appId.ToLowerInvariant()
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

function Get-ArtDataUri($props) {
  if ($null -eq $props.Thumbnail) { return $null }
  try {
    $ras = Await ($props.Thumbnail.OpenReadAsync()) $StreamType
    $src = [System.IO.WindowsRuntimeStreamExtensions]::AsStreamForRead($ras)
    $ms = New-Object System.IO.MemoryStream
    $src.CopyTo($ms)
    $src.Dispose()
    if ($ms.Length -eq 0 -or $ms.Length -gt 1MB) { return $null }
    $ct = [string]$ras.ContentType
    if (-not $ct -or -not $ct.StartsWith('image/')) { $ct = 'image/png' }
    return "data:$ct;base64," + [Convert]::ToBase64String($ms.ToArray())
  } catch { return $null }
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

      # Some apps change the title a moment before the cover, so look again for a few seconds.
      $artChanged = $false
      if ($artTries -lt 3) {
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
