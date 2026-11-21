[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$root = (Resolve-Path (Join-Path $PSScriptRoot '..\..\..')).Path
$baseline = @{}
foreach ($line in Get-Content -LiteralPath (Join-Path $PSScriptRoot 'fixed-wait-baseline.txt') -Encoding UTF8) {
    if ($line -match '^#|^\s*$') { continue }
    if ($line -notmatch '^(\d+)\s+(.+)$') { throw "Invalid fixed-wait baseline entry: $line" }
    $baseline.Add($Matches[2], [int]$Matches[1])
}

# Skip C# comments and string/character literals, including multiline raw JSON fixtures.
$nonCode = '(?s)""".*?"""|@"(?:[^"]|"")*"|"(?:\\.|[^"\\])*"|''(?:\\.|[^''\\])*''|//[^\r\n]*|/\*.*?\*/'
$number = '\d[\d_]*(?:\.\d[\d_]*)?[dDfFlLmM]?'
$duration = "(?:$number|TimeSpan\s*\.\s*From(?:Ticks|Microseconds|Milliseconds|Seconds|Minutes|Hours|Days)\s*\(\s*$number\s*\)|TimeSpan\s*\.\s*Zero)"
$method = '(?:Thread\s*\.\s*Sleep|Task\s*\.\s*Delay|Pump(?:Dispatcher|Onboarding)?)'
$files = @(git -C $root -c core.quotepath=false ls-files --cached --others --exclude-standard -- 'src/*SelfTest*.cs' 'src/*.Tests/*.cs')
if ($LASTEXITCODE -ne 0 -or !$files.Count) { throw 'Could not enumerate self-test sources.' }

$found = @{}
foreach ($file in $files) {
    $path = Join-Path $root $file
    if (!(Test-Path -LiteralPath $path -PathType Leaf)) { continue } # Locally deleted tracked file.
    $code = [regex]::Replace([IO.File]::ReadAllText($path), $nonCode, ' ')
    $fixed = $duration
    # Include named fixed durations such as StoppingWriteHold.
    $constants = [regex]::Matches($code, "\b(?:const|readonly)\s+(?:int|TimeSpan)\s+(\w+)\s*=\s*$duration\s*;")
    foreach ($constant in $constants) { $fixed += '|' + [regex]::Escape($constant.Groups[1].Value) }
    $calls = "\b(?<method>$method)\s*\(\s*(?<delay>$fixed)\s*(?=[,)])|\b(?<method>Pump(?:Onboarding)?)\s*\(\s*\)"
    foreach ($call in [regex]::Matches($code, $calls)) {
        $name = $call.Groups['method'].Value -replace '\s'
        $delay = $call.Groups['delay'].Value -replace '\s'
        $key = "$file $name($delay)"
        $found[$key]++
    }
}

$over = @($found.Keys | Sort-Object | Where-Object { $found[$_] -gt $baseline[$_] })
foreach ($key in $over) {
    Write-Host "FAIL $key (found $($found[$key]), baseline $([int]$baseline[$key]))"
}
if ($over.Count) { throw 'New fixed wait in self-test code. Use PumpUntil or wait for an observable condition.' }
Write-Host "PASS fixed waits ($($found.Count) file/pattern entries)"
