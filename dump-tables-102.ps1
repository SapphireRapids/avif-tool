param(
    # Inspect the MSI tables of a built package (WiX/Windows Installer debug helper).
    [string]$Msi    = (Join-Path $PSScriptRoot 'dist\avif tool 1.1.msi'),
    [string]$OutDir = (Join-Path $PSScriptRoot '_tables')
)

$ErrorActionPreference = 'Stop'
if (-not (Test-Path $Msi)) { throw "MSI not found: $Msi (build it first with .\build.ps1)" }
New-Item -ItemType Directory -Force -Path $OutDir | Out-Null

function IM($o, $n, $p) { $o.GetType().InvokeMember($n, 'InvokeMethod', $null, $o, $p) }
function PG($o, $n, $p) { $o.GetType().InvokeMember($n, 'GetProperty', $null, $o, $p) }

# Emits tab-joined rows as strings (COM records stay local to this function).
function FetchTsv($db, $sql, $cols) {
    $view = IM $db 'OpenView' $sql
    IM $view 'Execute' $null
    $acc = @()
    while ($true) {
        $rec = IM $view 'Fetch' $null
        if ($null -eq $rec) { break }
        $vals = @()
        for ($i = 1; $i -le $cols; $i++) {
            try { $vals += [string](PG $rec 'StringData' @($i)) } catch { $vals += '' }
        }
        $acc += ,($vals -join "`t")
    }
    $acc
}

$wi = New-Object -ComObject WindowsInstaller.Installer
$db = IM $wi 'OpenDatabase' @($Msi, 0)

$tblList = @(FetchTsv $db 'SELECT `Name` FROM `_Tables`' 1)
$tables = @()
foreach ($x in $tblList) { if ($x) { $tables += [string]$x } }

$valList = @(FetchTsv $db 'SELECT `Table`, `Column` FROM `_Validation`' 2)
$colMap = @{}
foreach ($line in $valList) {
    if (-not $line) { continue }
    $p = ([string]$line) -split "`t"
    if (-not $colMap.ContainsKey($p[0])) { $colMap[$p[0]] = @() }
    $colMap[$p[0]] = @($colMap[$p[0]]) + $p[1]
}

$summary = @()
foreach ($t in $tables) {
    if ($t -eq '_Validation') { continue }
    if ($colMap.ContainsKey($t)) { $cols = @($colMap[$t]) } else { $cols = @() }
    if ($cols.Count -eq 0) {
        $probe = @(FetchTsv $db "SELECT * FROM ``$t``" 40)
        $w = 1
        if ($probe.Count -gt 0) {
            $pv = ([string]$probe[0]) -split "`t"
            for ($i = 0; $i -lt $pv.Count; $i++) { if ($pv[$i]) { $w = $i + 1 } }
        }
        for ($i = 1; $i -le $w; $i++) { $cols += "col$i" }
    }
    $rows = @(FetchTsv $db "SELECT * FROM ``$t``" $cols.Count)
    $lines = @(($cols -join "`t"))
    foreach ($r in $rows) { if ($r) { $lines += [string]$r } }
    $path = Join-Path $OutDir (($t -replace '[^\w]', '_') + '.tsv')
    Set-Content -Path $path -Value $lines -Encoding UTF8
    $summary += [pscustomobject]@{ Table = $t; Rows = $rows.Count; Cols = $cols.Count }
}
$summary | Format-Table -AutoSize | Out-String -Width 200

