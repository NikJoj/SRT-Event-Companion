<#
.SYNOPSIS
  Builds the SRT Event Companion installer for rigs.

.EXAMPLE
  .\installer\build-installer.ps1 -Version 1.0.0

.EXAMPLE
  # With code signing (certificate installed in your Windows certificate store)
  .\installer\build-installer.ps1 -Version 1.1.0 -CertThumbprint 0123ABCD...

.NOTES
  Needs: .NET 10 SDK, Inno Setup 6.3+ (winget install JRSoftware.InnoSetup).
  Output: artifacts\installer\SRT-Event-Companion-Setup-<version>.exe and its SHA-256.
  The installer is self-contained: rigs do not need .NET installed.
#>
param(
  [Parameter(Mandatory = $true)][ValidatePattern('^\d+\.\d+\.\d+$')][string]$Version,
  [string]$CertThumbprint,
  [string]$InnoSetup = "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
  [string]$TimestampUrl = 'http://timestamp.digicert.com',
  [switch]$SkipTests
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$project = Join-Path $root 'src\Srt.EventCompanion.Desktop\Srt.EventCompanion.Desktop.csproj'
$publish = Join-Path $root 'artifacts\publish'
$output = Join-Path $root 'artifacts\installer'

function Step($text) { Write-Host "`n== $text" -ForegroundColor Red }

if (-not (Test-Path $InnoSetup)) {
  # winget installs Inno Setup per-user (LocalAppData) unless run as admin, so look in every usual place.
  $candidates = @(
    "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe",
    "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
    "$env:ProgramFiles\Inno Setup 6\ISCC.exe"
  )
  $InnoSetup = $candidates | Where-Object { Test-Path $_ } | Select-Object -First 1
  if (-not $InnoSetup) { $InnoSetup = (Get-Command ISCC.exe -ErrorAction SilentlyContinue).Source }
  if (-not $InnoSetup) { throw 'Inno Setup not found. Install it with: winget install JRSoftware.InnoSetup (or pass -InnoSetup "<path>\ISCC.exe")' }
}

if (-not $SkipTests) {
  Step 'Running tests'
  dotnet run --project (Join-Path $root 'tests\Srt.EventCompanion.Core.Tests\Srt.EventCompanion.Core.Tests.csproj')
  if ($LASTEXITCODE -ne 0) { throw 'Tests failed. Fix them before building a release.' }
}

Step "Publishing v$Version (self-contained, win-x64)"
if (Test-Path $publish) { Remove-Item $publish -Recurse -Force }
dotnet publish $project -c Release -r win-x64 --self-contained true `
  -p:Version=$Version -p:PublishReadyToRun=true -p:DebugType=none -o $publish
if ($LASTEXITCODE -ne 0) { throw 'dotnet publish failed.' }

$signArgs = @()
if ($CertThumbprint) {
  $signtool = Get-ChildItem "${env:ProgramFiles(x86)}\Windows Kits\10\bin\*\x64\signtool.exe" -ErrorAction SilentlyContinue |
    Sort-Object FullName -Descending | Select-Object -First 1 -ExpandProperty FullName
  if (-not $signtool) { throw 'signtool.exe not found. Install the Windows SDK, or build without -CertThumbprint.' }
  Step 'Signing the app'
  & $signtool sign /sha1 $CertThumbprint /fd sha256 /tr $TimestampUrl /td sha256 (Join-Path $publish 'SRTEventCompanion.exe')
  if ($LASTEXITCODE -ne 0) { throw 'Signing the app failed.' }
  $signArgs = @('/DSign', "/Ssrtsign=`"$signtool`" sign /sha1 $CertThumbprint /fd sha256 /tr $TimestampUrl /td sha256 `$f")
}

Step 'Building the installer'
& $InnoSetup "/DAppVersion=$Version" @signArgs (Join-Path $PSScriptRoot 'SrtEventCompanion.iss')
if ($LASTEXITCODE -ne 0) { throw 'Inno Setup failed.' }

$setup = Join-Path $output "SRT-Event-Companion-Setup-$Version.exe"
$hash = (Get-FileHash $setup -Algorithm SHA256).Hash.ToLowerInvariant()
Set-Content -Path "$setup.sha256" -Value "$hash  SRT-Event-Companion-Setup-$Version.exe" -Encoding ascii

Step 'Done'
Write-Host "Installer : $setup"
Write-Host "Size      : $([math]::Round((Get-Item $setup).Length / 1MB, 1)) MB"
Write-Host "SHA-256   : $hash" -ForegroundColor Yellow
Write-Host ''
Write-Host 'To roll this out to every rig:'
Write-Host '  1. Upload the installer somewhere public over https (for example a GitHub release).'
Write-Host '  2. Admin > Kerala Race Wars > Companion app release > Publish new version.'
Write-Host "  3. Enter version $Version, the download link and the SHA-256 above."
Write-Host 'Rigs install it the next time they are idle (never during a session).'
if (-not $CertThumbprint) { Write-Host "`nNot code-signed: Windows SmartScreen will warn on first install (More info > Run anyway)." -ForegroundColor DarkYellow }
