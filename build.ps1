# avif tool 1.2 build script
#   .\build.ps1                  # restore online from nuget.org, publish, then build the MSI
#   .\build.ps1 -NugetRoot 'D:\path\to\.nuget-packages'
#                                # fully offline: use an extracted package folder that already
#                                # contains wpf-ui + communitytoolkit.mvvm + the 10.0.x win-x64
#                                # runtime packs
#   .\build.ps1 -MsiName 'avif tool 1.2.msi'
#   .\build.ps1 -NoMsi           # stop after dotnet publish (no WiX needed)
#
# Note: src\AvifForge links ..\tools\avifenc.exe when it exists (the SVT-AV1 encoding
# engine). Without it the app still compiles but cannot encode; build the engine with
# build-engine\build-avifenc.ps1 - see build-engine\README.md and tools\README.md.
#
# ASCII only on purpose: Windows PowerShell 5.1 reads .ps1 as ANSI without a BOM.
param(
    [string]$NugetRoot = '',
    [string]$MsiName   = 'avif tool 1.2.msi',
    [switch]$NoMsi
)
$ErrorActionPreference = 'Stop'
$Root = Split-Path -Parent $MyInvocation.MyCommand.Path

# --- optional offline package root ----------------------------------------
# Search order: $env:NUGET_PACKAGES, the machine cache, then any .nuget-packages
# next to this folder or in one of its sibling folders. Nothing found is fine:
# dotnet then restores from nuget.org per nuget.config.
if (-not $NugetRoot) {
    $cands = @()
    if ($env:NUGET_PACKAGES) { $cands += $env:NUGET_PACKAGES }
    $cands += (Join-Path $HOME '.nuget\packages')
    $parent = Split-Path -Parent $Root
    $cands += (Join-Path $parent '.nuget-packages')
    $cands += @(Get-ChildItem $parent -Directory -ErrorAction SilentlyContinue |
                ForEach-Object { Join-Path $_.FullName '.nuget-packages' })
    foreach ($c in $cands) {
        if ($c -and (Test-Path (Join-Path $c 'wpf-ui'))) { $NugetRoot = $c; break }
    }
}
if ($NugetRoot -and (Test-Path (Join-Path $NugetRoot 'wpf-ui'))) {
    $env:NUGET_PACKAGES = $NugetRoot
    "offline package root : $NugetRoot"
} else {
    $NugetRoot = ''
    'offline package root : none found -> restoring from nuget.org'
}

$env:DOTNET_CLI_HOME    = Join-Path $Root '_clihome'
$env:TEMP = $env:TMP     = $env:DOTNET_CLI_HOME
$env:DOTNET_NOLOGO      = '1'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
New-Item -ItemType Directory -Force -Path $env:DOTNET_CLI_HOME | Out-Null
Set-Location $Root

$engine = Join-Path $Root 'tools\avifenc.exe'
if (Test-Path $engine) {
    "encoding engine      : $engine"
} else {
    'encoding engine      : tools\avifenc.exe MISSING (app builds, but cannot encode;'
    '                       see build-engine\README.md)'
}

'=== 1) dotnet publish (self-contained, single file) ==='
$pubArgs = @(
    'publish', 'src\AvifForge', '-c', 'Release', '-r', 'win-x64', '--self-contained', 'true',
    '-p:PublishSingleFile=true',
    '-p:IncludeNativeLibrariesForSelfExtract=true',
    '-p:EnableCompressionInSingleFile=true',
    '-o', 'publish'
)
if ($NugetRoot) { $pubArgs += "-p:RestoreSources=$NugetRoot" }
dotnet @pubArgs
if ($LASTEXITCODE -ne 0) { throw "publish failed ($LASTEXITCODE)" }
$exe = Join-Path $Root 'publish\AvifForge.exe'
'  exe : {0:N0} B  ver={1}  product={2}' -f (Get-Item $exe).Length, (Get-Item $exe).VersionInfo.FileVersion, (Get-Item $exe).VersionInfo.ProductName

if ($NoMsi) { '=== skipped the MSI (-NoMsi). done. ==='; exit 0 }

'=== 2) wix build ==='
$wix = Join-Path $Root '.tools\wix.exe'
if (-not (Test-Path $wix)) {
    '  .tools\wix.exe not found - installing the WiX v5 tooling into .tools'
    dotnet tool install --tool-path (Join-Path $Root '.tools') --version 5.0.2 wix
    if ($LASTEXITCODE -ne 0 -or -not (Test-Path $wix)) {
        throw 'wix tooling unavailable: run `dotnet tool install --tool-path .tools --version 5.0.2 wix` online, or grab wix.exe from https://github.com/wixtoolset/releases'
    }
}
New-Item -ItemType Directory -Force -Path (Join-Path $Root 'dist') | Out-Null
# NOTE: -d values must be written as "Name=$(...)" (string interpolation). The form
# Name=(Join-Path ...) makes PowerShell pass the parenthesized expression to the
# native exe as a SEPARATE argument, so wix receives a bare path (WIX0103).
& $wix build installer\package.wxs -arch x64 `
    -o (Join-Path $Root "dist\$MsiName") `
    -d "PublishDir=$(Join-Path $Root 'publish')" `
    -d "SrcDir=$(Join-Path $Root 'src\AvifForge')" `
    -d "InstallerDir=$(Join-Path $Root 'installer')"
if ($LASTEXITCODE -ne 0) { throw "wix build failed ($LASTEXITCODE)" }

$msi = Get-Item (Join-Path $Root "dist\$MsiName")
'=== 3) result ==='
'  MSI : {0:N0} B' -f $msi.Length
'  SHA : {0}' -f (Get-FileHash $msi.FullName).Hash
"DONE  $($msi.FullName)"
