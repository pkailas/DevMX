# publish.ps1 - build and deploy DevMX.App for daily use.
# Output: dist\app\DevMX.App.exe (+ shortcuts). Close any running DevMX before publishing.
param(
    [string]$Configuration = "Release"
)
$ErrorActionPreference = "Stop"
$repo = Split-Path $PSScriptRoot -Parent
$out  = Join-Path $repo "dist\app"

# Refuse to publish over a running instance (locked image).
if (Get-Process DevMX.App -ErrorAction SilentlyContinue) {
    Write-Host "DevMX.App is running - close it first (the exe is locked while running)." -ForegroundColor Yellow
    exit 1
}

Write-Host "Publishing DevMX.App ($Configuration) -> $out"
dotnet publish (Join-Path $repo "src\DevMX.App\DevMX.App.csproj") `
    -c $Configuration -r win-x64 --self-contained false `
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true `
    -o $out
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

# Shortcuts: Start Menu + Desktop.
$ws = New-Object -ComObject WScript.Shell
foreach ($dir in @([Environment]::GetFolderPath("Programs"), [Environment]::GetFolderPath("Desktop"))) {
    $lnk = $ws.CreateShortcut((Join-Path $dir "DevMX.lnk"))
    $lnk.TargetPath = Join-Path $out "DevMX.App.exe"
    $lnk.WorkingDirectory = $out
    $lnk.Description = "DevMX - model-agnostic agent companion (DevMind backend)"
    $lnk.Save()
}
Write-Host "Done. Launch DevMX from the Start Menu or Desktop shortcut." -ForegroundColor Green
