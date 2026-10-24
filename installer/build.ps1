<#
  Builds MyNotch's installer: publishes the app (Release, x64, self-contained: nothing else needed on the PC), then
  compiles installer\MyNotch.iss with Inno Setup 6.

    powershell -ExecutionPolicy Bypass -File installer\build.ps1

  Result: artifacts\MyNotch-Setup-<version>.exe (the version comes from Directory.Build.props).
  Inno Setup, once: winget install -e --id JRSoftware.InnoSetup
#>
param([string] $Version)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
if (-not $Version) {
    $Version = ([xml](Get-Content (Join-Path $root 'Directory.Build.props'))).Project.PropertyGroup.Version
}

$artifacts = Join-Path $root 'artifacts'
$publish = Join-Path $artifacts 'publish'
if (Test-Path $publish) {
    Remove-Item $publish -Recurse -Force
}

Write-Host "Publication de MyNotch $Version…"
dotnet publish (Join-Path $root 'src\MyNotch\MyNotch.csproj') -c Release -r win-x64 -p:Platform=x64 --self-contained true -o $publish
if ($LASTEXITCODE -ne 0) {
    exit $LASTEXITCODE
}

$iscc = @(
    (Join-Path $env:LOCALAPPDATA 'Programs\Inno Setup 6\ISCC.exe'),
    (Join-Path ${env:ProgramFiles(x86)} 'Inno Setup 6\ISCC.exe'),
    (Join-Path $env:ProgramFiles 'Inno Setup 6\ISCC.exe')
) | Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $iscc) {
    Write-Error "Inno Setup 6 est introuvable. Installez-le une fois : winget install -e --id JRSoftware.InnoSetup"
}

Write-Host "Création de l'installateur…"
& $iscc "/DAppVersion=$Version" "/DSourceDir=$publish" "/DOutputDir=$artifacts" (Join-Path $PSScriptRoot 'MyNotch.iss')
if ($LASTEXITCODE -ne 0) {
    exit $LASTEXITCODE
}

Write-Host "Prêt : $(Join-Path $artifacts "MyNotch-Setup-$Version.exe")"
