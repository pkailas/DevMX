# update.ps1 - self-update flow: tests, republishes, and relaunches DevMX.
# Called by the app as a detached PowerShell process after the app shuts down.
# The app passes its own PID so this script can wait for it to exit cleanly.
param(
    [int]$AppPid,
    [string]$RepoRoot
)
$ErrorActionPreference = "Stop"

$logDir = Join-Path $env:LOCALAPPDATA "DevMX"
if (!(Test-Path $logDir)) { New-Item -ItemType Directory -Path $logDir -Force | Out-Null }
$logFile = Join-Path $logDir "update.log"

# Overwrite log per run
@() | Out-File -FilePath $logFile -Encoding utf8

function Log($msg) {
    $line = "[$(Get-Date -Format 'yyyy-MM-dd HH:mm:ss')] $msg"
    Add-Content -Path $logFile -Value $line -Encoding utf8
    Write-Host $line
}

function ShowFailureAndRelaunch {
    Log "FAILURE - showing message box to user."
    Add-Type -AssemblyName PresentationFramework
    [System.Windows.MessageBox]::Show(
        "Update aborted - build or tests failed. Old version relaunched.`nLog: $logFile",
        "DevMX Update",
        [System.Windows.MessageBoxButton]::OK,
        [System.Windows.MessageBoxImage]::Warning
    )
    # Relaunch the existing (untouched) dist exe
    $oldExe = Join-Path $RepoRoot "dist\app\DevMX.App.exe"
    if (Test-Path $oldExe) {
        Log "Relaunching existing version: $oldExe"
        Start-Process $oldExe
    } else {
        Log "WARNING: No existing dist exe found at $oldExe"
    }
    exit 1
}

# --- Step 1: Wait for the app process to exit ---
Log "Waiting for DevMX process (PID $AppPid) to exit..."
$proc = Get-Process -Id $AppPid -ErrorAction SilentlyContinue
if ($proc) {
    # Wait up to 30 seconds for graceful exit
    $waited = $proc.WaitForExit(30000)
    if (-not $waited) {
        Log "Process did not exit within 30s, forcing..."
        Stop-Process -Id $AppPid -Force -ErrorAction SilentlyContinue
        Start-Sleep -Seconds 5
    }
} else {
    Log "Process $AppPid not found (already exited)."
}
Log "App process has exited."

# --- Step 2: Run tests ---
Log "Running tests..."
$sln = Join-Path $RepoRoot "DevMX.sln"
$testOutput = & dotnet test $sln -c Release --nologo 2>&1
$testExit = $LASTEXITCODE
Log "dotnet test exit code: $testExit"
foreach ($line in $testOutput) {
    Log "TEST: $line"
}

if ($testExit -ne 0) {
    Log "Tests FAILED."
    ShowFailureAndRelaunch
}
Log "Tests PASSED."

# --- Step 3: Publish ---
Log "Running publish..."
$publishScript = Join-Path $RepoRoot "scripts\publish.ps1"
$publishOutput = & powershell -NoProfile -ExecutionPolicy Bypass -File $publishScript 2>&1
$publishExit = $LASTEXITCODE
Log "publish.ps1 exit code: $publishExit"
foreach ($line in $publishOutput) {
    Log "PUBLISH: $line"
}

if ($publishExit -ne 0) {
    Log "Publish FAILED."
    ShowFailureAndRelaunch
}
Log "Publish succeeded."

# --- Step 4: Launch the new version ---
$newExe = Join-Path $RepoRoot "dist\app\DevMX.App.exe"
Log "Launching updated DevMX: $newExe"
Start-Process $newExe
Log "Update complete. Exit 0."
exit 0
