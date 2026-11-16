# Builds Flashback into the "dist" folder.
#
#   powershell -ExecutionPolicy Bypass -File build.ps1
#
# Needs the .NET 10 SDK. FFmpeg is downloaded once into tools\ffmpeg if it is not there yet.

$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$dist = Join-Path $root 'dist'
$ffDir = Join-Path $root 'tools\ffmpeg'
$ff = Join-Path $ffDir 'ffmpeg.exe'

# .NET SDK: use the first dotnet that really has a version 10 SDK. A dotnet on PATH may be a runtime-only install.
$dotnet = $null
$candidates = @((Get-Command dotnet -ErrorAction SilentlyContinue).Source, (Join-Path $env:LOCALAPPDATA 'Microsoft\dotnet\dotnet.exe'))
foreach ($candidate in $candidates) {
    if ($candidate -and (Test-Path $candidate) -and ((& $candidate --list-sdks) -match '^10\.')) { $dotnet = $candidate; break }
}
if (-not $dotnet) { throw 'The .NET 10 SDK was not found. Install it from https://dot.net and run this again.' }

# FFmpeg: download once if missing. The download and the unpacked archive are removed afterwards.
if (-not (Test-Path $ff)) {
    $tmp = Join-Path ([IO.Path]::GetTempPath()) ('flashback-ffmpeg-' + [Guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Force $tmp | Out-Null
    try {
        $zip = Join-Path $tmp 'ffmpeg.zip'
        $ProgressPreference = 'SilentlyContinue'
        'Downloading FFmpeg (about 190 MB)...'
        Invoke-WebRequest -UseBasicParsing 'https://github.com/BtbN/FFmpeg-Builds/releases/download/latest/ffmpeg-n8.1-latest-win64-gpl-8.1.zip' -OutFile $zip
        Expand-Archive $zip -DestinationPath $tmp
        $found = Get-ChildItem -Recurse $tmp -Filter ffmpeg.exe | Select-Object -First 1
        if (-not $found) { throw 'ffmpeg.exe was not found in the downloaded archive.' }
        New-Item -ItemType Directory -Force $ffDir | Out-Null
        Copy-Item $found.FullName $ff -Force
    }
    finally {
        Remove-Item -Recurse -Force $tmp -ErrorAction SilentlyContinue
    }
}

# The app's files cannot be replaced while it is running.
$running = Get-Process Flashback -ErrorAction SilentlyContinue | Where-Object { $_.Path -like "$dist\*" }
if ($running) { throw 'Flashback is running. Exit it from its tray icon first, then run this again.' }

# Always publish from a clean state. Publishing a second time into the same folder makes the .NET SDK delete the
# native DLLs next to the exe (it wrongly treats unchanged files as stale), and the app then cannot start.
if (Test-Path $dist) {
    Get-ChildItem $dist -File |
        Where-Object { $_.Name -in 'Flashback.exe', 'ffmpeg.exe' -or $_.Extension -in '.dll', '.pdb' } |
        Remove-Item -Force
}
Get-ChildItem (Join-Path $root 'src\Flashback\obj') -Recurse -Filter 'PublishOutputs.*.txt' -ErrorAction SilentlyContinue |
    Remove-Item -Force

$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
& $dotnet publish (Join-Path $root 'src\Flashback\Flashback.csproj') -c Release -o $dist --nologo -v q
if ($LASTEXITCODE -ne 0) { throw 'Build failed.' }
foreach ($needed in 'Flashback.exe', 'wpfgfx_cor3.dll', 'PresentationNative_cor3.dll', 'D3DCompiler_47_cor3.dll', 'PenImc_cor3.dll', 'vcruntime140_cor3.dll') {
    if (-not (Test-Path (Join-Path $dist $needed))) { throw "The build is incomplete: $needed is missing from $dist." }
}

# FFmpeg sits next to the app. A hard link uses no extra disk space; fall back to a copy if linking is not possible.
$target = Join-Path $dist 'ffmpeg.exe'
if (Test-Path $target) { Remove-Item $target -Force }
try { New-Item -ItemType HardLink -Path $target -Target $ff -ErrorAction Stop | Out-Null }
catch { Copy-Item $ff $target }

"Built: $(Join-Path $dist 'Flashback.exe')"
