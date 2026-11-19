# Builds the Windows drag-and-drop release: one zip whose contents go straight into the Receiver folder.
#
#   powershell -File packaging\make-release.ps1 -BepInEx <BepInEx 5 x64 zip or folder>
#
# The zip holds only what has to be there:
#   winhttp.dll, doorstop_config.ini         (BepInEx's loader, at the game root)
#   BepInEx\core\*.dll                       (BepInEx itself; no docs, changelog or version stamp)
#   BepInEx\plugins\ReceiverMultiplayer\     (the mod, LiteNetLib, the updater, the license notices)
param(
    [Parameter(Mandatory = $true)][string]$BepInEx,
    [string]$Out
)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.IO.Compression, System.IO.Compression.FileSystem

$root = (Resolve-Path (Join-Path (Split-Path -Parent $MyInvocation.MyCommand.Path) '..')).Path
if (-not $Out) { $Out = Join-Path $root 'dist' }
$version = ([xml](Get-Content (Join-Path $root 'src\ReceiverMultiplayer\ReceiverMultiplayer.csproj'))).Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1
$updaterVersion = ([xml](Get-Content (Join-Path $root 'src\Updater\Updater.csproj'))).Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1
if ($version -ne $updaterVersion) { throw "Plugin is $version but the updater is $updaterVersion." }

dotnet build (Join-Path $root 'src\ReceiverMultiplayer\ReceiverMultiplayer.csproj') -c Release -nologo -v q -p:DeployToGame=false
if ($LASTEXITCODE) { throw 'Plugin build failed.' }
dotnet build (Join-Path $root 'src\Updater\Updater.csproj') -c Release -nologo -v q
if ($LASTEXITCODE) { throw 'Updater build failed.' }

# BepInEx: from its release zip or an unpacked copy.
$temp = Join-Path ([IO.Path]::GetTempPath()) ('rmp-release-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory $temp | Out-Null
try {
    $bep = $BepInEx
    if ((Get-Item $BepInEx) -is [IO.FileInfo]) {
        $bep = Join-Path $temp 'bepinex'
        [IO.Compression.ZipFile]::ExtractToDirectory((Resolve-Path $BepInEx).Path, $bep)
    }
    foreach ($need in 'winhttp.dll', 'doorstop_config.ini', 'BepInEx\core\BepInEx.dll', 'BepInEx\core\BepInEx.Preloader.dll') {
        if (-not (Test-Path (Join-Path $bep $need))) { throw "$need is missing from $BepInEx." }
    }

    $files = [ordered]@{}
    $files['winhttp.dll'] = Join-Path $bep 'winhttp.dll'
    $files['doorstop_config.ini'] = Join-Path $bep 'doorstop_config.ini'
    Get-ChildItem (Join-Path $bep 'BepInEx\core') -Filter *.dll | Sort-Object Name | ForEach-Object { $files["BepInEx/core/$($_.Name)"] = $_.FullName }
    $mod = 'BepInEx/plugins/ReceiverMultiplayer'
    $files["$mod/ReceiverMultiplayer.dll"] = Join-Path $root 'src\ReceiverMultiplayer\bin\Release\ReceiverMultiplayer.dll'
    $files["$mod/LiteNetLib.dll"] = Join-Path $root 'src\ReceiverMultiplayer\bin\Release\LiteNetLib.dll'
    $files["$mod/ReceiverMultiplayer-Updater.exe"] = Join-Path $root 'src\Updater\bin\Release\net472\ReceiverMultiplayer-Updater.exe'
    $files["$mod/THIRD-PARTY-NOTICES.txt"] = Join-Path $root 'packaging\THIRD-PARTY-NOTICES.txt'

    New-Item -ItemType Directory -Force $Out | Out-Null
    $zipPath = Join-Path (Resolve-Path $Out).Path "ReceiverMultiplayer-$version.zip"
    if (Test-Path $zipPath) { Remove-Item $zipPath }
    $stamp = [DateTimeOffset]::new(2026, 1, 1, 0, 0, 0, [TimeSpan]::Zero) # fixed: same inputs, same zip
    $zip = [IO.Compression.ZipFile]::Open($zipPath, 'Create')
    try {
        foreach ($name in $files.Keys) {
            $entry = $zip.CreateEntry($name, [IO.Compression.CompressionLevel]::Optimal)
            $entry.LastWriteTime = $stamp
            $dst = $entry.Open()
            $src = [IO.File]::OpenRead($files[$name])
            try { $src.CopyTo($dst) } finally { $src.Dispose(); $dst.Dispose() }
        }
    } finally { $zip.Dispose() }

    $exe = Join-Path $Out 'ReceiverMultiplayer-Updater.exe'
    Copy-Item $files["$mod/ReceiverMultiplayer-Updater.exe"] $exe -Force

    Get-Item $zipPath, $exe | ForEach-Object {
        '{0}  {1,9:N0} bytes  sha256 {2}' -f $_.Name, $_.Length, (Get-FileHash $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
    }
} finally {
    Remove-Item -Recurse -Force $temp -ErrorAction SilentlyContinue
}
