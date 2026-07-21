<#
.SYNOPSIS
    Deploys the latest build from staging to the VerbellaCMG production IIS site.
    MUST be run as Administrator - stopping IIS app pools requires elevation.
#>
#Requires -RunAsAdministrator

$ErrorActionPreference = "Stop"

$StagingDir  = "C:\deploy\verbella-cm"
$ProdDir     = "C:\Program Files\Verbella CMG, LLC\VerbellaCMG Case Management"
$AppPoolName = "VCMG_CaseManagement"
$SiteUrl     = "http://support.ionline.com:8081"

Write-Host "============================================================" -ForegroundColor Cyan
Write-Host "  VerbellaCMG Case Management - Deploy to Production" -ForegroundColor Cyan
Write-Host "============================================================" -ForegroundColor Cyan
Write-Host ""

# ---- 1. Stop the app pool ----
Write-Host "[1/4] Stopping app pool '$AppPoolName'..." -ForegroundColor Yellow
$appcmd = "$env:SystemRoot\System32\inetsrv\appcmd.exe"
& $appcmd stop apppool /apppool.name:$AppPoolName
if ($LASTEXITCODE -ne 0) {
    Write-Warning "appcmd stop returned exit code $LASTEXITCODE (may already be stopped)"
}
Start-Sleep -Seconds 3

# ---- 2. Kill any lingering worker process ----
Write-Host "[2/4] Ensuring no w3wp.exe processes remain..." -ForegroundColor Yellow
$w3wpProcs = Get-Process -Name "w3wp" -ErrorAction SilentlyContinue
if ($w3wpProcs) {
    $w3wpProcs | ForEach-Object {
        Write-Host "  Killing PID $($_.Id)..."
        Stop-Process -Id $_.Id -Force -ErrorAction SilentlyContinue
    }
    Start-Sleep -Seconds 2
}
else {
    Write-Host "  No w3wp.exe processes found."
}

# ---- 3. Copy everything (pool is stopped, no files are locked) ----
Write-Host "[3/4] Copying all files from staging..." -ForegroundColor Yellow
& robocopy $StagingDir $ProdDir /MIR /NP /NDL /NJH /NJS /R:5 /W:3 /XD Logs
$rc = $LASTEXITCODE
if ($rc -ge 8) {
    Write-Error "robocopy failed with exit code $rc"
    exit $rc
}
Write-Host "  robocopy exit: $rc (OK)" -ForegroundColor Green

# ---- 4. Start the app pool ----
Write-Host "[4/4] Starting app pool '$AppPoolName'..." -ForegroundColor Yellow
& $appcmd start apppool /apppool.name:$AppPoolName
if ($LASTEXITCODE -ne 0) {
    Write-Error "appcmd start failed with exit code $LASTEXITCODE"
    exit $LASTEXITCODE
}

# ---- Verify ----
Write-Host ""
Write-Host "Waiting for site to warm up..." -ForegroundColor Yellow
Start-Sleep -Seconds 5

try {
    $response = Invoke-WebRequest -Uri $SiteUrl -UseBasicParsing -TimeoutSec 15
    Write-Host "  HTTP $($response.StatusCode) - site is up!" -ForegroundColor Green
}
catch {
    Write-Warning "  Site check failed: $_"
}

# Show the log tail
$today   = Get-Date -Format "yyyyMMdd"
$logPath = Join-Path $ProdDir "logs\verbella-cm-$today.log"
if (Test-Path $logPath) {
    Write-Host ""
    Write-Host "--- Latest log entries ---" -ForegroundColor Cyan
    Get-Content $logPath -Tail 20
}
else {
    Write-Warning "No log file found at $logPath - the app may not have started."
}

Write-Host ""
Write-Host "Deployment complete." -ForegroundColor Green
Write-Host "  Open:  $SiteUrl"
Write-Host "  Login: pkailas@verbella.com / Admin123!"
