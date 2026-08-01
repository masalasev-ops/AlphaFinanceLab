# Register-integrity checks (rule 25, D109). Run by tools/ci.ps1; standalone for iteration:
#   pwsh tools/check-register.ps1            fail on any violation (CI mode)
#   pwsh tools/check-register.ps1 -Report    list violations and exit 0 (survey mode)
#
# WHY THESE ARE CODE AND NOT A CHECKLIST. Three documentation sweeps passed while D87's defect was
# present, because a per-document audit asks "is this recorded" and the defect was RELATIONAL: each
# document was internally consistent and each recorded what it claimed to. Only a check that reads
# the register and the citations TOGETHER can see it. A checklist item can be run and passed while
# the defect is present; these cannot.
#
# ASCII only (the .ps1 rule).
#   pwsh tools/check-register.ps1 -Baseline  fail only on violations NOT in a baseline file
#
# THE BASELINE IS GONE AND THE GUARD IS NOW ABSOLUTE (v1.9.58). The 63 grandfathered violations this
# check shipped with have all been retired, register-baseline.txt is deleted, and ci.ps1 calls this
# script BARE: any violation fails the build. -Baseline is retained but has no file to read, so it
# now behaves identically to bare mode (an absent file means an empty baseline).
#
# WHAT THAT COSTS, recorded so the next supersession is not surprised by it: every future
# supersession must retire ALL its citations before its PR can go green. For D109 that would have
# been 38 sites. It binds at PR level, NOT commit level - so a decision and its cleanup stay
# separable, which is the shape v1.9.58 used (the meaning-changing rewrites in one commit, the
# navigation pointers in the next). Keep that split: it is what stops a decision being buried
# inside its own cleanup.
#
# AND WHAT THIS CHECK STILL CANNOT DO: it finds CITATIONS of a superseded row that omit the
# successor. It cannot find a false CLAIM that never cites a decision at all - v1.9.58 found five
# such lines only via a separate semantic sweep. Retiring a decision-s citations is not the same
# job as retiring its claims; this script automates the first only.
#
# THE BASELINE IS A RATCHET, NOT AN EXEMPTION. The 63 violations present when these checks were
# written are recorded in tools/register-baseline.txt and reported on every run, but do not fail the
# build - they predate the check, and fixing them in the same commit that introduced it would have
# made it impossible to tell whether the check works. Anything NOT in that file fails immediately,
# so the rule binds on all new work from the moment it lands. The file can only shrink: an entry
# that no longer occurs is reported so it can be deleted.
param([switch]$Report, [switch]$Baseline)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$master = Join-Path $repoRoot 'docs/MASTER_DESIGN_v1.9.md'
$violations = New-Object System.Collections.Generic.List[string]

function Add-Violation {
    param([string]$Check, [string]$Text)
    $violations.Add("[$Check] $Text") | Out-Null
}

# ---------------------------------------------------------------- parse the register
# One row per decision: | D<n> | ... | ... | <status> |
$registerStatus = @{}
$registerLine = @{}
$lineNo = 0
foreach ($line in Get-Content -LiteralPath $master -Encoding UTF8) {
    $lineNo++
    if ($line -notmatch '^\|\s*\*{0,2}D(\d+)\*{0,2}\s*\|') { continue }
    $num = [int]$Matches[1]
    # The status is the last non-empty cell on the row.
    $cells = $line.TrimEnd() -split '\|'
    $status = ($cells[$cells.Length - 2]).Trim()
    $registerStatus[$num] = $status
    $registerLine[$num] = $lineNo
}

if ($registerStatus.Count -eq 0) { throw 'check-register: parsed zero register rows - the table shape changed.' }
$declared = ($registerStatus.Keys | Sort-Object)
$maxD = ($declared | Select-Object -Last 1)

# The vocabulary a Status cell may use. Anything else is a typo that would silently disable 3b.
$statusOk = '^(active|reserved|withdrawn|superseded-by\s+D\S+|amended-by\s+D\S+)$'
foreach ($d in $declared) {
    if ($registerStatus[$d] -notmatch $statusOk) {
        Add-Violation '3c' ("D$d has an unrecognized Status '" + $registerStatus[$d] + "' (MASTER line " + $registerLine[$d] + ")")
    }
}

# ---------------------------------------------------------------- the citation corpus
# Everything that can CITE a decision. The CHANGELOG is excluded from 3b (not 3a): it is the
# provenance trace, and an entry recording what v1.9.33 decided must be free to cite D87 without
# naming a successor that did not exist yet. It is NOT excluded from 3a - citing a decision that
# never existed is a defect in a historical record too.
$docFiles = @()
$docFiles += Get-ChildItem -LiteralPath (Join-Path $repoRoot 'docs') -Filter *.md -Recurse -File |
    ForEach-Object { $_.FullName }
$docFiles += @('CLAUDE.md', 'PROGRESS.md', 'README.md', 'START_HERE.md', 'ORIENTATION.md') |
    ForEach-Object { Join-Path $repoRoot $_ } | Where-Object { Test-Path -LiteralPath $_ }
$srcFiles = Get-ChildItem -LiteralPath (Join-Path $repoRoot 'src') -Filter *.cs -Recurse -File |
    Where-Object { $_.FullName -notmatch '\\(bin|obj|Migrations)\\' } | ForEach-Object { $_.FullName }
$citeFiles = @($docFiles) + @($srcFiles)

$changelogPath = (Join-Path $repoRoot 'docs/CHANGELOG_v1.9.md')

# ---------------------------------------------------------------- 3a: every citation resolves
# A citation is D<digits> on a word boundary. Deliberately strict about what follows, so that
# identifiers like "D3D" or a hex blob cannot masquerade as a decision reference.
$citations = Select-String -Path $citeFiles -Pattern '\bD(\d{1,3})\b' -AllMatches -ErrorAction SilentlyContinue
$unresolved = @{}
foreach ($hit in $citations) {
    if ($hit.Path -eq $master -and $hit.LineNumber -ge 130) { continue }   # the register defines them
    foreach ($m in $hit.Matches) {
        $n = [int]$m.Groups[1].Value
        if ($n -lt 1 -or $n -gt $maxD) {
            if (-not $registerStatus.ContainsKey($n)) {
                $key = "D$n"
                if (-not $unresolved.ContainsKey($key)) { $unresolved[$key] = @() }
                if ($unresolved[$key].Count -lt 4) {
                    $rel = $hit.Path.Substring($repoRoot.Length + 1)
                    $unresolved[$key] += ("$rel" + ':' + $hit.LineNumber)
                }
            }
        }
        elseif (-not $registerStatus.ContainsKey($n)) {
            $key = "D$n"
            if (-not $unresolved.ContainsKey($key)) { $unresolved[$key] = @() }
            if ($unresolved[$key].Count -lt 4) {
                $rel = $hit.Path.Substring($repoRoot.Length + 1)
                $unresolved[$key] += ("$rel" + ':' + $hit.LineNumber)
            }
        }
    }
}
foreach ($k in ($unresolved.Keys | Sort-Object)) {
    Add-Violation '3a' ("$k is cited but has no row in MASTER section 2: " + ($unresolved[$k] -join ', '))
}

# ---------------------------------------------------------------- 3b: superseded rows name their successor
# The check that would have caught D87 the day the sign-off statements landed.
#
# SUPERSEDED ONLY, NOT AMENDED, and the distinction is load-bearing. A superseded row no longer
# holds: citing it without its successor points a reader at a decision that has been replaced. An
# AMENDED row still holds - the amendment changed a clause, not the decision - so citing D70 for
# its sourcing rules is correct even though D109 re-amended its widening clause. Applying this to
# amendments produced 200+ violations that were all legitimate citations, which would have made
# the check noise and therefore ignored.
foreach ($d in $declared) {
    $status = $registerStatus[$d]
    if ($status -notmatch '^superseded-by\s+(.+)$') { continue }
    $successorRaw = $Matches[1].Trim()
    # A successor cell may name several rows (e.g. "D79-D82", "D57/D67"). Any one of them satisfies.
    $successors = [regex]::Matches($successorRaw, 'D(\d+)') | ForEach-Object { 'D' + $_.Groups[1].Value }
    if (-not $successors) { continue }

    $hits = Select-String -Path $citeFiles -Pattern ("\bD$d\b") -ErrorAction SilentlyContinue
    foreach ($hit in $hits) {
        if ($hit.Path -eq $master -and $hit.LineNumber -ge 130) { continue }   # the register itself
        if ($hit.Path -eq $changelogPath) { continue }                          # provenance trace
        $named = $false
        foreach ($s in $successors) { if ($hit.Line -match ("\b" + $s + "\b")) { $named = $true; break } }
        if (-not $named) {
            $rel = $hit.Path.Substring($repoRoot.Length + 1)
            Add-Violation '3b' ("D$d is $status but is cited without naming its successor at ${rel}:" + $hit.LineNumber)
        }
    }
}

# ---------------------------------------------------------------- 3c: contiguous numbering
# Every number from 1..max must HAVE a row. A deliberate gap is expressed as a row with status
# 'reserved' or 'withdrawn' - never as an absent row, which is indistinguishable from an accident.
for ($n = 1; $n -le $maxD; $n++) {
    if (-not $registerStatus.ContainsKey($n)) {
        Add-Violation '3c' ("D$n has no register row - a deliberate gap must be a row with status 'reserved' or 'withdrawn', never an absence")
    }
}

# ---------------------------------------------------------------- 3d: pinned constants doc vs code
# LIMITATION, STATED RATHER THAN HIDDEN: a general "find every number in the docs and match it to
# the code" scanner is not possible - most numbers in prose are not constants, and most constants
# are never named in prose. What IS possible, and is what this does, is to read the value from the
# CODE at run time and assert the doc says the same thing. The value is never duplicated inside
# this script, so a code change cannot leave the check stale; only ADDING a pin is manual.
# This cannot catch a constant nobody pinned. It can catch every pinned one drifting.
$pins = @(
    @{ Name = 'n_eff floor';
       CodeFile = 'src/AlphaLab.Evaluation/Signals/SignalTrend.cs';
       CodePattern = 'MinimumCount\s*=\s*(\d+)';
       DocFile = 'docs/MASTER_DESIGN_v1.9.md';
       DocTemplate = 'n_eff = {0}' },
    @{ Name = 'grade horizons';
       CodeFile = 'src/AlphaLab.Core/Config/SignalLibraryOptions.cs';
       CodePattern = 'DefaultHorizonsDays\s*=\s*\[(\d+,\s*\d+)\]';
       DocFile = 'docs/CONFIG_REFERENCE_v1.9.md';
       DocTemplate = '[ {0} ]' },
    @{ Name = 'rolling windows';
       CodeFile = 'src/AlphaLab.Core/Config/SignalLibraryOptions.cs';
       CodePattern = 'DefaultRollingWindowsYears\s*=\s*\[(\d+,\s*\d+)\]';
       DocFile = 'docs/CONFIG_REFERENCE_v1.9.md';
       DocTemplate = '[ {0} ]' }
)
foreach ($pin in $pins) {
    $codePath = Join-Path $repoRoot $pin.CodeFile
    $docPath = Join-Path $repoRoot $pin.DocFile
    if (-not (Test-Path -LiteralPath $codePath)) { Add-Violation '3d' ("pin '" + $pin.Name + "': code file missing " + $pin.CodeFile); continue }
    if (-not (Test-Path -LiteralPath $docPath)) { Add-Violation '3d' ("pin '" + $pin.Name + "': doc file missing " + $pin.DocFile); continue }
    $codeText = Get-Content -LiteralPath $codePath -Raw -Encoding UTF8
    if ($codeText -notmatch $pin.CodePattern) {
        Add-Violation '3d' ("pin '" + $pin.Name + "': the code pattern no longer matches in " + $pin.CodeFile + " - the pin is stale and is silently checking nothing")
        continue
    }
    $value = $Matches[1].Trim()
    $expected = [string]::Format($pin.DocTemplate, $value)
    $docText = Get-Content -LiteralPath $docPath -Raw -Encoding UTF8
    if ($docText -notmatch [regex]::Escape($expected)) {
        Add-Violation '3d' ("pin '" + $pin.Name + "': code says '" + $value + "' but " + $pin.DocFile + " does not contain '" + $expected + "'")
    }
}

# ---------------------------------------------------------------- report
Write-Host ('register: ' + $registerStatus.Count + ' rows, D1..D' + $maxD) -ForegroundColor Cyan
$byStatus = $declared | Group-Object { ($registerStatus[$_] -split '\s+')[0] } | Sort-Object Name
foreach ($g in $byStatus) { Write-Host ('  ' + $g.Name + ': ' + $g.Count) }

if ($violations.Count -eq 0) {
    Write-Host 'check-register OK' -ForegroundColor Green
    exit 0
}

$baselinePath = Join-Path $PSScriptRoot 'register-baseline.txt'
$known = @()
if ($Baseline -and (Test-Path -LiteralPath $baselinePath)) {
    $known = Get-Content -LiteralPath $baselinePath -Encoding UTF8 |
        Where-Object { $_.Trim() -and -not $_.StartsWith('#') }
}

# THE BASELINE IS KEYED BY (check, decision, FILE) AND A COUNT - DELIBERATELY NOT BY LINE NUMBER
# (finding 310). A line-anchored baseline is broken by construction: inserting ANY line above a
# grandfathered violation shifts it, and the same violation then reads as BOTH 'no longer occurs'
# AND 'new'. The first docs pass after this check shipped produced 10 such false positives and 0
# real ones - a guard that cries wolf on every edit is a guard that gets switched off.
#
# The count is the ratchet: one baseline line per occurrence, so N identical lines mean N
# grandfathered occurrences in that file. An ADDITIONAL occurrence in the same file still fails,
# and so does the first occurrence in a new file.
#
# COST, STATED NOT HIDDEN: moving a violation WITHIN a file that already carries one is no longer
# detected. That is the price of the line number, and it is worth paying - the line bought
# detection of a case nobody has ever hit while breaking on a case every docs pass hits.
function Get-BaselineKey { param([string]$V) return ($V -replace ':\d+\s*$', '') }

function Get-KeyCounts {
    param([string[]]$Lines)
    $counts = @{}
    foreach ($line in $Lines) {
        $key = Get-BaselineKey $line
        if ($counts.ContainsKey($key)) { $counts[$key] = $counts[$key] + 1 } else { $counts[$key] = 1 }
    }
    return $counts
}

$currentCounts = Get-KeyCounts $violations
$baseCounts = Get-KeyCounts $known

$newKeys = @()
$carried = @()
foreach ($key in $currentCounts.Keys) {
    $allowed = 0
    if ($baseCounts.ContainsKey($key)) { $allowed = $baseCounts[$key] }
    $actual = $currentCounts[$key]
    $keep = [Math]::Min($actual, $allowed)
    for ($i = 0; $i -lt $keep; $i++) { $carried += $key }
    if ($actual -gt $allowed) { $newKeys += $key }
}

$stale = @()
foreach ($key in $baseCounts.Keys) {
    $actual = 0
    if ($currentCounts.ContainsKey($key)) { $actual = $currentCounts[$key] }
    for ($i = $actual; $i -lt $baseCounts[$key]; $i++) { $stale += $key }
}
$stale = @($stale | Sort-Object)

# Report the CONCRETE file:line occurrences of every over-budget key, so the message still points
# at somewhere to look even though the line is not what was matched.
$new = @()
foreach ($key in ($newKeys | Sort-Object -Unique)) {
    $new += @($violations | Where-Object { (Get-BaselineKey $_) -eq $key })
}
$new = @($new)

Write-Host ''
if ($Baseline) {
    Write-Host ('check-register: ' + $carried.Count + ' baseline violation(s) carried, ' + $new.Count + ' new') -ForegroundColor Cyan
    if ($stale.Count -gt 0) {
        Write-Host ('  ' + $stale.Count + ' baseline entr(ies) no longer occur - delete them from register-baseline.txt:') -ForegroundColor Green
        $stale | Sort-Object | Select-Object -First 10 | ForEach-Object { Write-Host ('    ' + $_) -ForegroundColor Green }
    }
}
else {
    Write-Host ('check-register: ' + $violations.Count + ' violation(s)') -ForegroundColor Yellow
    $violations | Sort-Object | ForEach-Object { Write-Host ('  ' + $_) -ForegroundColor Yellow }
}

if ($new.Count -gt 0) {
    Write-Host ''
    Write-Host ('NEW violation(s) - these are not grandfathered:') -ForegroundColor Red
    $new | ForEach-Object { Write-Host ('  ' + $_) -ForegroundColor Red }
}

if ($Report) { exit 0 }
if ($Baseline) {
    if ($new.Count -gt 0) { throw ('check-register: ' + $new.Count + ' NEW violation(s) beyond the recorded baseline.') }
    Write-Host 'check-register OK (baseline carried)' -ForegroundColor Green
    exit 0
}
throw ('check-register failed with ' + $violations.Count + ' violation(s).')
