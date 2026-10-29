# Builds dist\SanctumHelper-Setup-<version>.exe
#   .\build.ps1              package the app with the current Assets
#   .\build.ps1 -GameData    first re-extract maps + layouts from the Aniimo install (after a game patch;
#                            -Game is the launcher's install or Steam's steamapps\common\Aniimo)
param([switch]$GameData, [string]$Game = "D:\PawPrint\Aniimo")
$ErrorActionPreference = "Stop"
$root = $PSScriptRoot

if ($GameData) {
    python -I "$root\tools\extract_maps.py" --game $Game
    if ($LASTEXITCODE) { throw "extract_maps failed" }
    python -I "$root\tools\extract_icons.py" --game $Game
    if ($LASTEXITCODE) { throw "extract_icons failed" }
    python -I "$root\tools\build_data.py" --game $Game
    if ($LASTEXITCODE) { throw "build_data failed" }
}

$publish = "$root\build\publish"
if (Test-Path $publish) { Remove-Item $publish -Recurse -Force }
dotnet publish "$root\app\SanctumHelper\SanctumHelper.csproj" -c Release -o $publish
if ($LASTEXITCODE) { throw "publish failed" }

# OpenCV's video I/O plugin is never used (we only read screen pixels and PNGs).
Remove-Item "$publish\opencv_videoio_ffmpeg*.dll" -ErrorAction SilentlyContinue

$zip = "$root\build\payload.zip"
if (Test-Path $zip) { Remove-Item $zip }
Add-Type -AssemblyName System.IO.Compression.FileSystem
[System.IO.Compression.ZipFile]::CreateFromDirectory($publish, $zip, [System.IO.Compression.CompressionLevel]::Optimal, $false)

dotnet build "$root\installer\Setup\Setup.csproj" -c Release -o "$root\build\setup"
if ($LASTEXITCODE) { throw "setup build failed" }

[xml]$proj = Get-Content "$root\app\SanctumHelper\SanctumHelper.csproj"
$version = $proj.Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1
New-Item -ItemType Directory -Force "$root\dist" | Out-Null
$out = "$root\dist\SanctumHelper-Setup-$version.exe"
Copy-Item "$root\build\setup\SanctumHelper-Setup.exe" $out -Force
"{0}  ({1:N1} MB)" -f $out, ((Get-Item $out).Length / 1MB)
