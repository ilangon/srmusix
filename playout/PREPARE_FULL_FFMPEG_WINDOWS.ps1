$ErrorActionPreference = 'Stop'
$project = Split-Path -Parent $MyInvocation.MyCommand.Path
$bin = Join-Path $project 'bin'
New-Item -ItemType Directory -Force -Path $bin | Out-Null

Write-Host 'Installing the Full FFmpeg codec package for bundling...'
if (Get-Command choco -ErrorAction SilentlyContinue) {
  choco install ffmpeg-full -y --no-progress
} elseif (Get-Command winget -ErrorAction SilentlyContinue) {
  winget install --id Gyan.FFmpeg -e --accept-package-agreements --accept-source-agreements --silent
} else {
  throw 'Chocolatey or Winget is required to prepare Full FFmpeg.'
}

$searchRoots = @(
  "$env:ChocolateyInstall\bin",
  "$env:ChocolateyInstall\lib",
  "$env:LOCALAPPDATA\Microsoft\WinGet\Packages",
  "$env:ProgramFiles\ffmpeg",
  "$env:ProgramData\chocolatey"
) | Where-Object { $_ -and (Test-Path $_) }

foreach ($tool in @('ffmpeg.exe','ffprobe.exe','ffplay.exe')) {
  $cmd = Get-Command $tool -ErrorAction SilentlyContinue
  $source = if ($cmd -and (Test-Path $cmd.Source)) { $cmd.Source } else {
    Get-ChildItem -Path $searchRoots -Filter $tool -File -Recurse -ErrorAction SilentlyContinue |
      Where-Object { $_.Length -gt 1000000 } | Select-Object -First 1 -ExpandProperty FullName
  }
  if (-not $source) { throw "$tool was not found after installing Full FFmpeg." }
  Copy-Item -Force $source (Join-Path $bin $tool)
}

foreach ($tool in @('ffmpeg.exe','ffprobe.exe','ffplay.exe')) {
  $target = Join-Path $bin $tool
  if (-not (Test-Path $target) -or (Get-Item $target).Length -lt 1000000) { throw "Invalid bundled $tool" }
}
Write-Host 'Full FFmpeg decoder/encoder/preview package is ready in bin.'
