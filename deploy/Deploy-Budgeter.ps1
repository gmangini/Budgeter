<#
.SYNOPSIS
  Builds Budgeter for Linux and installs it on a server as the `budgeter` systemd service.

.DESCRIPTION
  Dry run by default: builds and stages the bundle, then prints what it would copy and run (exit 5).
  -Apply copies the bundle over SSH and runs deploy/install.sh on the server.
  -FirstRun also copies .env (your keys) and data/ (budget, categories, watch state). Use it once; later
  deploys leave the server's .env and data alone.

.EXAMPLE
  pwsh deploy/Deploy-Budgeter.ps1 -Server root@203.0.113.10 -FirstRun
  pwsh deploy/Deploy-Budgeter.ps1 -Server root@203.0.113.10 -FirstRun -Apply
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string] $Server,
    [string] $TimeZone,
    [switch] $FirstRun,
    [switch] $Apply
)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$stage = Join-Path $root 'artifacts/deploy/budgeter-deploy'

if (-not $TimeZone) {
    $iana = $null
    if (-not [TimeZoneInfo]::TryConvertWindowsIdToIanaId([TimeZoneInfo]::Local.Id, [ref]$iana)) {
        throw "Couldn't work out your time zone; pass -TimeZone, e.g. America/New_York."
    }
    $TimeZone = $iana
}

Write-Host "Building for Linux..."
Remove-Item -Recurse -Force (Join-Path $root 'artifacts/deploy') -ErrorAction SilentlyContinue
dotnet publish (Join-Path $root 'src/Budgeter') -c Release -r linux-x64 --self-contained `
    -p:PublishSingleFile=true -p:DebugType=none -o (Join-Path $stage 'app') --nologo -v quiet
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed." }

# Shell files must have Unix line endings or sh and systemd choke on them.
function Copy-Lf($from, $to, $replace = @{}) {
    $text = (Get-Content -Raw $from) -replace "`r`n", "`n"
    foreach ($k in $replace.Keys) { $text = $text.Replace($k, $replace[$k]) }
    [IO.File]::WriteAllText($to, $text)
}
Copy-Lf (Join-Path $PSScriptRoot 'budgeter.service') (Join-Path $stage 'budgeter.service') @{ '__TIMEZONE__' = $TimeZone }
Copy-Lf (Join-Path $PSScriptRoot 'budgeter') (Join-Path $stage 'budgeter')
Copy-Lf (Join-Path $PSScriptRoot 'install.sh') (Join-Path $stage 'install.sh')

$copied = @('app/ (Budgeter for linux-x64)', 'budgeter.service', 'budgeter (CLI wrapper)', 'install.sh')
if ($FirstRun) {
    $envFile = Join-Path $root '.env'
    if (-not (Test-Path $envFile)) { throw "No .env in $root to copy." }
    Copy-Item $envFile (Join-Path $stage '.env')
    $copied += '.env (your keys, mode 600 on the server)'
    $data = Join-Path $root 'data'
    $files = 'budget.json', 'categories.json', 'watch-state.json' |
        Where-Object { Test-Path (Join-Path $data $_) }
    if ($files) {
        New-Item -ItemType Directory (Join-Path $stage 'data') | Out-Null
        foreach ($f in $files) { Copy-Item (Join-Path $data $f) (Join-Path $stage "data/$f") }
        $copied += "data/: $($files -join ', ')"
    }
}

$bundle = Join-Path $root 'artifacts/deploy/budgeter-deploy.tgz'
# Relative paths only: GNU tar (Git Bash) reads "C:\..." as a remote host.
Push-Location (Join-Path $root 'artifacts/deploy')
try { tar -czf budgeter-deploy.tgz budgeter-deploy } finally { Pop-Location }
if ($LASTEXITCODE -ne 0) { throw "tar failed." }
# The bundle may hold a copy of .env; don't leave a second loose copy of the keys lying around.
Remove-Item -Recurse -Force $stage
$size = '{0:N0} MB' -f ((Get-Item $bundle).Length / 1MB)

Write-Host ""
Write-Host "Server:    $Server"
Write-Host "Time zone: $TimeZone"
Write-Host "Bundle:    $size, containing:"
$copied | ForEach-Object { Write-Host "  + $_" }
Write-Host "On the server: install to /opt/budgeter, (re)start the budgeter service, add /usr/local/bin/budgeter."

if (-not $Apply) {
    Remove-Item -Recurse -Force (Join-Path $root 'artifacts/deploy')
    Write-Host "`nDRY RUN: nothing was copied. Re-run with -Apply to deploy."
    exit 5
}

Write-Host "`nCopying bundle..."
scp -q $bundle "${Server}:/tmp/budgeter-deploy.tgz"
if ($LASTEXITCODE -ne 0) { throw "scp failed." }
ssh $Server "cd /tmp && tar -xzf budgeter-deploy.tgz && rm budgeter-deploy.tgz && sh budgeter-deploy/install.sh"
if ($LASTEXITCODE -ne 0) { throw "Install on the server failed (see above)." }
Remove-Item -Recurse -Force (Join-Path $root 'artifacts/deploy')
Write-Host "`nDeployed. Logs: ssh $Server journalctl -u budgeter -f"
