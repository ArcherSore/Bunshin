[CmdletBinding()]
param(
    # Rewrites the baseline from the current sources; review the diff before committing it.
    [switch]$UpdateBaseline
)

$ErrorActionPreference = 'Stop'
$root = (Resolve-Path (Join-Path $PSScriptRoot '..\..\..')).Path
$baselinePath = Join-Path $PSScriptRoot 'fixed-wait-baseline.txt'
$baseline = @{}
foreach ($line in Get-Content -LiteralPath $baselinePath -Encoding UTF8) {
    if ($line -match '^#|^\s*$') { continue }
    if ($line -notmatch '^(\d+)\s+(.+)$') { throw "Invalid fixed-wait baseline entry: $line" }
    $baseline.Add($Matches[2], [int]$Matches[1])
}

function Get-Files
{
    param([string[]]$Patterns)
    $listed = @(git -C $root -c core.quotepath=false ls-files --cached --others --exclude-standard -- $Patterns)
    if ($LASTEXITCODE -ne 0 -or !$listed.Count) { throw "Could not enumerate sources: $Patterns" }
    # A locally deleted tracked file is still listed.
    $listed | Where-Object { Test-Path -LiteralPath (Join-Path $root $_) -PathType Leaf }
}

$found = @{}

# ---- C# self-tests ----
# Skip comments and string/character literals, including multiline raw JSON fixtures.
$csNonCode = '(?s)""".*?"""|@"(?:[^"]|"")*"|"(?:\\.|[^"\\])*"|''(?:\\.|[^''\\])*''|//[^\r\n]*|/\*.*?\*/'
$number = '\d[\d_]*(?:\.\d[\d_]*)?[dDfFlLmM]?'
$duration = "(?:$number|TimeSpan\s*\.\s*From(?:Ticks|Microseconds|Milliseconds|Seconds|Minutes|Hours|Days)\s*\(\s*$number\s*\)|TimeSpan\s*\.\s*Zero)"
$method = '(?:Thread\s*\.\s*Sleep|Task\s*\.\s*Delay|Pump(?:Dispatcher|Onboarding)?)'
foreach ($file in Get-Files 'src/*SelfTest*.cs', 'src/*.Tests/*.cs') {
    $code = [regex]::Replace([IO.File]::ReadAllText((Join-Path $root $file)), $csNonCode, ' ')
    $fixed = $duration
    # Named fixed durations: constants, readonly fields and ordinary locals initialised from a literal.
    $names = [regex]::Matches($code, "\b(?:var|int|long|double|TimeSpan)\s+(\w+)\s*=\s*$duration\s*;") |
        ForEach-Object { $_.Groups[1].Value } | Sort-Object -Unique
    foreach ($name in $names) { $fixed += '|' + [regex]::Escape($name) }
    $calls = "\b(?<method>$method)\s*\(\s*(?<delay>$fixed)\s*(?=[,)])|\b(?<method>Pump(?:Onboarding)?)\s*\(\s*\)"
    foreach ($call in [regex]::Matches($code, $calls)) {
        $name = $call.Groups['method'].Value -replace '\s'
        $delay = $call.Groups['delay'].Value -replace '\s'
        $found["$file $name($delay)"]++
    }
}

# ---- Rust Engine tests ----
# Integration tests, *_tests.rs files, and the #[cfg(test)] tail of other sources. Production sleeps are not checked.
$rsNonCode = '(?s)r(#*)".*?"\1|"(?:\\.|[^"\\])*"|''(?:\\.|[^''\\])''|//[^\r\n]*|/\*.*?\*/'
$rsDuration = '(?:std\s*::\s*)?(?:time\s*::\s*)?Duration\s*::\s*from_(?:millis|secs|micros|nanos|secs_f32|secs_f64)\s*\(\s*' +
    '\d[\d_]*(?:\.\d[\d_]*)?\s*\)'
foreach ($file in Get-Files 'src/UpdateEngine/tests/*.rs', 'src/UpdateEngine/tests/*/*.rs', 'src/UpdateEngine/src/*.rs', 'src/UpdateEngine/examples/*.rs') {
    $code = [regex]::Replace([IO.File]::ReadAllText((Join-Path $root $file)), $rsNonCode, ' ')
    if ($file -match '^src/UpdateEngine/(src|examples)/' -and $file -notmatch '_tests\.rs$') {
        $start = $code.IndexOf('#[cfg(test)]', [StringComparison]::Ordinal)
        if ($start -lt 0) { continue }
        $code = $code.Substring($start)
    }
    $fixed = $rsDuration
    $names = [regex]::Matches($code, "\b(?:const|static|let)\s+(?:mut\s+)?(\w+)\s*(?::\s*[\w:]+\s*)?=\s*$rsDuration\s*;") |
        ForEach-Object { $_.Groups[1].Value } | Sort-Object -Unique
    foreach ($name in $names) { $fixed += '|' + [regex]::Escape($name) }
    $calls = "\b(?:std\s*::\s*)?thread\s*::\s*sleep\s*\(\s*(?<delay>$fixed)\s*(?=[.)])"
    foreach ($call in [regex]::Matches($code, $calls)) {
        $delay = ($call.Groups['delay'].Value -replace '\s') -replace '^std::', '' -replace '^time::', ''
        $found["$file thread::sleep($delay)"]++
    }
}

if ($UpdateBaseline) {
    $lines = @('# Maximum occurrences per source file and normalized call (first duration argument).') +
        @($found.Keys | Sort-Object { $_ } -CaseSensitive | ForEach-Object { "$($found[$_]) $_" })
    [IO.File]::WriteAllText($baselinePath, ($lines -join "`n") + "`n", [Text.UTF8Encoding]::new($false))
    Write-Host "Baseline rewritten: $($found.Count) entries"
    exit 0
}

$over = @($found.Keys | Sort-Object | Where-Object { $found[$_] -gt $baseline[$_] })
foreach ($key in $over) {
    Write-Host "FAIL $key (found $($found[$key]), baseline $([int]$baseline[$key]))"
}
# Ratchet: a removed wait must also lower the baseline, so the freed slot cannot be reused by a new one.
$under = @($baseline.Keys | Sort-Object | Where-Object { [int]$found[$_] -lt $baseline[$_] })
foreach ($key in $under) {
    Write-Host "FAIL $key (found $([int]$found[$key]), baseline $($baseline[$key])): lower the baseline"
}
if ($over.Count) { throw 'New fixed wait in test code. Use PumpUntil for conditions, and task.WaitAsync(timeout) for timeout guards.' }
if ($under.Count) {
    throw 'Fixed waits were removed. Run check-fixed-waits.ps1 -UpdateBaseline and commit the lower baseline.'
}
Write-Host "PASS fixed waits ($($found.Count) file/pattern entries)"
