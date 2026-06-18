<#
.SYNOPSIS
    Builds the CryptoAI Terminal Windows installer, optionally code-signing it.

.DESCRIPTION
    1. (optional) Publishes the self-contained win-x64 build.
    2. (optional) Code-signs the app exe BEFORE packaging - only if a certificate
       is provided (env CODESIGN_PFX + CODESIGN_PASS, or CODESIGN_THUMBPRINT).
    3. Compiles installer\CryptoAITerminal.iss with Inno Setup (ISCC.exe).
    4. (optional) Code-signs the resulting setup .exe.

    Without a certificate the installer is still produced - just unsigned (Windows
    SmartScreen will warn until you add a purchased code-signing certificate).

.PARAMETER Version
    Version string baked into the installer + output filename. Default 1.6.0.

.PARAMETER Publish
    Run `dotnet publish` first. Omit to reuse the existing publish folder.

.EXAMPLE
    powershell installer\build-installer.ps1 -Version 1.6.0 -Publish
#>
param(
    [string]$Version = "1.6.0",
    [switch]$Publish
)

$ErrorActionPreference = "Stop"
$repo = Split-Path -Parent $PSScriptRoot
$proj = Join-Path $repo "CryptoAITerminal.TerminalUI\CryptoAITerminal.TerminalUI.csproj"
$publishDir = Join-Path $repo "CryptoAITerminal.TerminalUI\bin\Release\net8.0-windows\win-x64\publish"
$appExe = Join-Path $publishDir "CryptoAITerminal.TerminalUI.exe"
$iss = Join-Path $PSScriptRoot "CryptoAITerminal.iss"

function Find-Tool([string[]]$candidates, [string]$name) {
    foreach ($c in $candidates) {
        $hit = Get-ChildItem $c -ErrorAction SilentlyContinue | Select-Object -First 1
        if ($hit) { return $hit.FullName }
    }
    $cmd = Get-Command $name -ErrorAction SilentlyContinue
    if ($cmd) { return $cmd.Source }
    return $null
}

$iscc = Find-Tool @(
    "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe",
    "C:\Program Files (x86)\Inno Setup 6\ISCC.exe",
    "C:\Program Files\Inno Setup 6\ISCC.exe"
) "ISCC.exe"
if (-not $iscc) { throw "Inno Setup (ISCC.exe) not found. Install: winget install JRSoftware.InnoSetup" }

$signtool = Find-Tool @("C:\Program Files (x86)\Windows Kits\10\bin\*\x64\signtool.exe") "signtool.exe"

# Step 1. Publish
if ($Publish) {
    Write-Host "Publishing self-contained win-x64..." -ForegroundColor Cyan
    try { Get-Process CryptoAITerminal.TerminalUI -ErrorAction Stop | Stop-Process -Force } catch {}
    dotnet publish $proj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=false --nologo
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed." }
}
if (-not (Test-Path $appExe)) { throw "Publish output not found at $appExe. Run with -Publish." }

# Helper: sign a file if a certificate is configured (otherwise no-op)
function Invoke-Sign([string]$file) {
    $pfx = $env:CODESIGN_PFX
    $pass = $env:CODESIGN_PASS
    $thumb = $env:CODESIGN_THUMBPRINT
    if (-not $signtool) { Write-Host "  (signtool not found - skipping signing)" -ForegroundColor Yellow; return }
    if ((-not $pfx) -and (-not $thumb)) { return }

    $ts = "http://timestamp.sectigo.com"
    if ($thumb) {
        & $signtool sign /sha1 $thumb /fd SHA256 /tr $ts /td SHA256 $file
    } else {
        & $signtool sign /f $pfx /p $pass /fd SHA256 /tr $ts /td SHA256 $file
    }
    if ($LASTEXITCODE -ne 0) { throw "signtool failed for $file" }
    Write-Host "  signed: $file" -ForegroundColor Green
}

# Step 2. Sign the inner app exe (before packaging)
Write-Host "Signing app exe (if certificate configured)..." -ForegroundColor Cyan
Invoke-Sign $appExe

# Step 3. Compile the installer
Write-Host "Building installer with Inno Setup..." -ForegroundColor Cyan
& $iscc "/DMyAppVersion=$Version" "/DSourceDir=$publishDir" $iss
if ($LASTEXITCODE -ne 0) { throw "ISCC failed." }

$setup = Join-Path $PSScriptRoot "output\CryptoAITerminal-Setup-$Version.exe"
if (-not (Test-Path $setup)) { throw "Installer not produced at $setup" }

# Step 4. Sign the installer
Write-Host "Signing installer (if certificate configured)..." -ForegroundColor Cyan
Invoke-Sign $setup

$mb = [math]::Round((Get-Item $setup).Length / 1MB, 1)
Write-Host ""
Write-Host "DONE -> $setup ($mb MB)" -ForegroundColor Green
