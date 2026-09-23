# build-avifenc.ps1
# Reproducible build of avifenc.exe = upstream libavif + official SVT-AV1, static, SVT-only encoder.
# Mirrors AOMediaCodec/libavif CI (ci-windows-artifacts.yml + ci-windows.yml) with SVT swapped in.
#
# Run inside "Developer PowerShell for VS 2022" (or after running vcvars64.bat) ON A MACHINE WITH INTERNET.
# Prereqs on PATH: git, cmake, nasm, cl.  libyuv needs clang-cl (VS component "C++ Clang Compiler for Windows")
# unless -NoLibyuv is given.
#
# ASCII only on purpose: Windows PowerShell 5.1 reads .ps1 as ANSI without a BOM.

param(
    # 'main' already pins SVT-AV1 v4.2.0 upstream (verified 2026-09-10 in ext/svt.cmd).
    # 'v1.4.2' keeps the released libavif version and patches the pin forward.
    [ValidateSet('main', 'v1.4.2')]
    [string]$LibavifRef = 'main',
    [string]$SvtTag = 'v4.2.0',
    [switch]$NoLibyuv,
    [string]$Work = (Join-Path $PSScriptRoot '_work'),
    [string]$Out = (Join-Path $PSScriptRoot 'out'),
    [string]$Sample   # optional .png/.jpg to smoke-encode
)

$ErrorActionPreference = 'Stop'

function Step($m) { Write-Host "=== $m ===" -ForegroundColor Cyan }
function Need($n) {
    $c = Get-Command $n -ErrorAction SilentlyContinue
    if (-not $c) { throw "missing on PATH: $n  (install it, or run from the right dev shell)" }
    "  found {0,-6} {1}" -f $n, $c.Source
}

Step '0. prerequisites'
Need git; Need cmake; Need nasm
$cl = Get-Command cl -ErrorAction SilentlyContinue
if (-not $cl) { throw 'cl.exe not on PATH - run this from "Developer PowerShell for VS 2022" or call vcvars64.bat first' }
"  found cl     $($cl.Source)"
if (-not $NoLibyuv) {
    $cc = Get-Command clang-cl -ErrorAction SilentlyContinue
    if (-not $cc) { Write-Warning 'clang-cl not found; re-run with -NoLibyuv (colour conversion then uses libavif''s built-in C path, which differs byte-for-byte from a libyuv build)' }
    else { "  found clang-cl $($cc.Source)" }
}
# must be a 64-bit toolchain: the app ships an x64 exe
if ([Environment]::Is64BitProcess -eq $false) { throw 'run in a 64-bit shell' }

if (Test-Path $Work) { Remove-Item $Work -Recurse -Force }
New-Item -ItemType Directory -Force -Path $Work, $Out | Out-Null
Set-Location $Work

Step "1. clone libavif ($LibavifRef)"
& git clone -b $LibavifRef --depth 1 --recursive https://github.com/AOMediaCodec/libavif.git
if ($LASTEXITCODE) { throw 'git clone libavif failed' }
$avif = Join-Path $Work 'libavif'

Step "2. SVT-AV1 pin -> $SvtTag"
foreach ($f in 'ext/svt.cmd', 'ext/svt.sh') {
    $p = Join-Path $avif $f
    $raw = Get-Content $p -Raw
    if ($raw -match "-b\s+v[\d.]+") {
        "  $f currently pins: $($Matches[0])"
    }
    $new = $raw -replace '(-b\s+)v\d+\.\d+(\.\d+)?', "`$1$SvtTag"
    if ($new -ne $raw) {
        Set-Content -Path $p -Value $new -NoNewline -Encoding ASCII
        "  $f patched to $SvtTag"
    } else {
        "  $f already at $SvtTag (or pattern not found - CHECK MANUALLY)"
    }
}

Step '3. build third-party deps (official ext/*.cmd, from the ext dir, as cmd batch)'
# Order matters only in that each script is independent; svt.cmd clones + builds SVT-AV1 static.
# IMPORTANT: run them with cmd, not bash - ext/svt.cmd forwards to svt.sh when run under sh.
$ext = Join-Path $avif 'ext'
$deps = @('zlibpng.cmd', 'libjpeg.cmd')
if (-not $NoLibyuv) { $deps += @('libyuv.cmd', 'libsharpyuv.cmd') }
$deps += 'svt.cmd'
foreach ($d in $deps) {
    Push-Location $ext
    try {
        & cmd /c $d
        if ($LASTEXITCODE) { throw "$d failed with exit $LASTEXITCODE" }
        "  ok: $d"
    } finally { Pop-Location }
}
if (-not (Test-Path (Join-Path $ext 'SVT-AV1\lib\svt-av1.lib'))) {
    Write-Warning 'ext\SVT-AV1\lib\svt-av1.lib not where expected - check cmake/Modules/LocalSvt.cmake for the path it wants'
}

Step '4. configure libavif (static, SVT-only encoder, apps on)'
$libyuvFlag = if ($NoLibyuv) { 'OFF' } else { 'LOCAL' }
& cmake -A x64 -S $avif -B (Join-Path $avif 'build') `
    -DCMAKE_BUILD_TYPE=Release -DBUILD_SHARED_LIBS=OFF `
    -DAVIF_CODEC_SVT=LOCAL `
    -DAVIF_CODEC_AOM=OFF -DAVIF_CODEC_DAV1D=OFF -DAVIF_CODEC_RAV1E=OFF -DAVIF_CODEC_LIBGAV1=OFF -DAVIF_CODEC_AVM=OFF `
    "-DAVIF_LIBYUV=$libyuvFlag" -DAVIF_LIBSHARPYUV=$(if ($NoLibyuv) { 'OFF' } else { 'LOCAL' }) `
    -DAVIF_JPEG=LOCAL -DAVIF_ZLIBPNG=LOCAL -DAVIF_LIBXML2=OFF `
    -DAVIF_BUILD_APPS=ON -DAVIF_BUILD_EXAMPLES=OFF -DAVIF_BUILD_TESTS=OFF `
    -DAVIF_ENABLE_WERROR=OFF
if ($LASTEXITCODE) { throw 'cmake configure failed' }

Step '5. build'
& cmake --build (Join-Path $avif 'build') --config Release --parallel
if ($LASTEXITCODE) { throw 'build failed' }

Step '6. collect avifenc.exe'
$found = Get-ChildItem (Join-Path $avif 'build') -Recurse -Filter avifenc.exe -ErrorAction SilentlyContinue |
         Where-Object { $_.FullName -match 'Release' } | Select-Object -First 1
if (-not $found) { throw 'avifenc.exe not found under build\Release' }
Copy-Item $found.FullName (Join-Path $Out 'avifenc.exe') -Force
$target = Join-Path $Out 'avifenc.exe'
"  built : $($found.FullName)"
"  size  : $((Get-Item $target).Length) B"
"  sha256: $((Get-FileHash $target -Algorithm SHA256).Hash)"
''
'--- version string (must contain "svt [enc]' + "$SvtTag" + '") ---'
& $target --version 2>&1
''
$ver = (& $target --version 2>&1) -join ' '
if ($ver -notmatch 'svt \[enc\]') { throw 'PROBE WOULD FAIL: AvifEncRunner.ProbeAsync requires the string "svt [enc]" in --version output' }
if ($ver -notmatch [regex]::Escape($SvtTag)) { Write-Warning "SVT tag in version string is not $SvtTag - inspect above" }
if ($ver -match 'aom|dav1d|rav1e') { Write-Warning 'extra codecs linked; current shipped build is SVT-only' }
''
if ($Sample) {
    '--- smoke encode ---'
    $dst = Join-Path $Out 'smoke.avif'
    if (Test-Path $dst) { Remove-Item $dst -Force }
    & $target -c svt -s 6 -q 60 -d 10 -o $dst $Sample 2>&1
    "  exit=$LASTEXITCODE  out=$(if (Test-Path $dst) { (Get-Item $dst).Length } else { 'none' }) B"
}
''
'--- next: install the engine into the project ------------------------------'
$install = Join-Path $PSScriptRoot '..\tools\avifenc.exe'
"  copy out\avifenc.exe -> $install"
"  then re-run ..\build.ps1 to republish the app / rebuild the MSI"
"  (build-avifenc does not overwrite tools\avifenc.exe automatically; check the SHA-256 above first)"
