# Builds Miu Smart Storage for Windows: a per-user installer (no admin rights) and a portable .zip.
# Usage: pwsh build/package-windows.ps1 -Version 0.8.0 [-Out artifacts]
param([Parameter(Mandatory = $true)][string]$Version, [string]$Out = "")
$ErrorActionPreference = "Stop"
$Root = Split-Path -Parent $PSScriptRoot
if ($Out -eq "") { $Out = Join-Path $Root "artifacts" }
New-Item -ItemType Directory -Force -Path $Out | Out-Null
$Out = (Resolve-Path $Out).Path
$Rid = "win-x64"
$Work = Join-Path $Out "work-$Rid"
if (Test-Path $Work) { Remove-Item -Recurse -Force $Work }
$Publish = Join-Path $Work "publish"

Write-Host "== publish $Rid $Version"
dotnet publish (Join-Path $Root "src/Miu.App") -c Release -r $Rid --self-contained `
  -p:Version=$Version -p:DebugType=none -p:DebugSymbols=false -o $Publish
if ($LASTEXITCODE -ne 0) { throw "publish failed" }

Write-Host "== portable zip"
Compress-Archive -Path (Join-Path $Publish "*") -DestinationPath (Join-Path $Out "Miu-Smart-Storage-$Version-windows-x64-portable.zip") -Force

Write-Host "== installer"
$Iscc = @("${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe", "$env:ProgramFiles\Inno Setup 6\ISCC.exe") | Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $Iscc) { throw "Inno Setup 6 (ISCC.exe) not found. Install it with: choco install innosetup" }
$Numeric = (($Version -split '\+')[0] -split '-')[0]
& $Iscc "/DAppVersion=$Version" "/DAppNumericVersion=$Numeric" "/DSourceDir=$Publish" "/DOutputDir=$Out" "/DRootDir=$Root" (Join-Path $Root "build/miu.iss")
if ($LASTEXITCODE -ne 0) { throw "installer build failed" }

Remove-Item -Recurse -Force $Work
Write-Host "== done"
Get-ChildItem $Out
