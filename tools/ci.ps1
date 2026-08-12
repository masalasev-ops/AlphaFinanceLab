# Local CI: build, test, and guard greps. Mirrors what a CI server would run.
#   tools/ci.ps1                 build + test + guards
#   tools/ci.ps1 -SkipTests      build + guards only
param([switch]$SkipTests)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot

function Invoke-Native {
    param([Parameter(Mandatory)][scriptblock]$Command, [Parameter(Mandatory)][string]$What)
    # Run a native command tolerant of stderr warnings (NU1903), gate on the exit code.
    $previousEap = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    & $Command
    $code = $LASTEXITCODE
    $ErrorActionPreference = $previousEap
    if ($code -ne 0) { throw "$What failed with exit code $code." }
}

function Assert-NoMatch {
    param(
        [Parameter(Mandatory)][string[]]$Files,
        [Parameter(Mandatory)][string]$Pattern,
        [Parameter(Mandatory)][string]$Message,
        [switch]$AllowEmpty,
        # Suppress the red hit listing. ONLY for the self-tests below, where a throw is the PASS and the
        # listing would read as a build failure in the CI log. Never use it for a real guard.
        [switch]$Quiet
    )
    # AN EMPTY FILE LIST IS A FAILURE, NOT A PASS (D151, finding 418). This used to `return`, so a guard
    # whose scoped directory had been renamed away reported success having scanned NOTHING - a
    # green-forever check of the exact kind this guard set exists to prevent, sitting in the helper every
    # guard below inherits. Guards that legitimately may scan nothing must say so with -AllowEmpty.
    if (-not $Files) {
        if ($AllowEmpty) { return }
        throw "Guard grep scanned NO FILES (scope is empty or mis-pathed): $Message"
    }
    $hits = Select-String -Path $Files -Pattern $Pattern -AllMatches -ErrorAction SilentlyContinue
    if ($hits) {
        if (-not $Quiet) {
            Write-Host "GUARD FAILED: $Message" -ForegroundColor Red
            $hits | ForEach-Object { Write-Host "  $($_.Path):$($_.LineNumber): $($_.Line.Trim())" -ForegroundColor Red }
        }
        throw "Guard grep failed: $Message"
    }
}

function Get-CommittableFiles {
    # Files eligible to be committed: tracked + untracked-but-not-ignored (so gitignored
    # appsettings.Secrets.json is excluded). Falls back to a working-tree scan if git is absent.
    Push-Location $repoRoot
    try {
        # Relax EAP around the native git call: under $ErrorActionPreference='Stop', a missing git
        # executable throws before $LASTEXITCODE can be read (defeating the fallback), and native
        # stderr can raise a terminating NativeCommandError (PS 5.1). Same guard the repo uses in
        # Invoke-Native / migrate.ps1 (finding 119 class). Gate on presence + exit code, not stderr.
        $files = $null
        $previousEap = $ErrorActionPreference
        $ErrorActionPreference = 'Continue'
        try {
            if (Get-Command git -ErrorAction SilentlyContinue) {
                $files = git ls-files --cached --others --exclude-standard 2>$null
                if ($LASTEXITCODE -ne 0) { $files = $null }
            }
        }
        catch { $files = $null }
        finally { $ErrorActionPreference = $previousEap }

        if (-not $files) {
            $files = Get-ChildItem -Recurse -File |
                Where-Object { $_.FullName -notmatch '\\(bin|obj)\\' } |
                ForEach-Object { Resolve-Path -Relative $_.FullName }
        }
        return $files | ForEach-Object { Join-Path $repoRoot $_ } | Where-Object { Test-Path $_ }
    }
    finally { Pop-Location }
}

function Assert-ReferenceGraph {
    # Full reference-graph guard (D57 — CI-enforced at the <ProjectReference> level, matching BUILD
    # 0.1). Each src project may reference ONLY the AlphaLab.* projects in its allowlist; an illegal
    # edge (e.g. Web -> Data, or Evaluation -> Strategies) fails CI. This makes D57's swappable-UI
    # promise and D58's honesty placement structural, not aspirational.
    $allowed = @{
        'AlphaLab.Core'       = @()
        'AlphaLab.Data'       = @('AlphaLab.Core')
        'AlphaLab.Strategies' = @('AlphaLab.Core', 'AlphaLab.Data')
        'AlphaLab.Llm'        = @('AlphaLab.Core')
        'AlphaLab.Evaluation' = @('AlphaLab.Core', 'AlphaLab.Data')
        'AlphaLab.Api'        = @('AlphaLab.Core', 'AlphaLab.Data', 'AlphaLab.Evaluation')
        'AlphaLab.Worker'     = @('AlphaLab.Core', 'AlphaLab.Data', 'AlphaLab.Evaluation', 'AlphaLab.Strategies', 'AlphaLab.Llm')
        'AlphaLab.Web'        = @('AlphaLab.Core')
    }
    $violations = @()
    foreach ($proj in $allowed.Keys) {
        $csproj = Join-Path $repoRoot "src/$proj/$proj.csproj"
        if (-not (Test-Path $csproj)) { $violations += "${proj}: csproj not found at src/$proj/"; continue }
        $refs = Select-String -Path $csproj -Pattern '<ProjectReference[^>]*Include="[^"]*[\\/](AlphaLab\.[A-Za-z]+)\.csproj"' -AllMatches |
            ForEach-Object { $_.Matches } | ForEach-Object { $_.Groups[1].Value } | Sort-Object -Unique
        foreach ($ref in $refs) {
            if ($allowed[$proj] -notcontains $ref) {
                $violations += "$proj must not reference $ref (allowed: $($allowed[$proj] -join ', '))."
            }
        }
    }
    if ($violations) {
        Write-Host 'GUARD FAILED: reference graph (D57)' -ForegroundColor Red
        $violations | ForEach-Object { Write-Host "  $_" -ForegroundColor Red }
        throw 'Guard failed: reference-graph violation (D57).'
    }
}

Push-Location $repoRoot
try {
    Write-Host '== build ==' -ForegroundColor Cyan
    Invoke-Native -What 'dotnet build' -Command { dotnet build 'AlphaLab.slnx' -c Debug --nologo }

    # Report-only vulnerability audit (after build so restore assets exist; runs under -SkipTests too).
    # NOT a gate yet: two transitive NU1903 advisories (SQLitePCLRaw.lib.e_sqlite3 2.1.11 via EF Core;
    # Microsoft.OpenApi 2.0.0 via AspNetCore.OpenApi) are non-blocking today and clear on Microsoft's next
    # 10.0.x servicing bump. Make this a HARD gate (throw on any advisory) once they clear. EAP is relaxed
    # so a native stderr line / non-zero exit here never fails CI (report only).
    Write-Host '== vuln audit (report-only) ==' -ForegroundColor Cyan
    $vulnEap = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    dotnet list 'AlphaLab.slnx' package --vulnerable --include-transitive
    $ErrorActionPreference = $vulnEap

    if (-not $SkipTests) {
        Write-Host '== test ==' -ForegroundColor Cyan
        # Category!=LiveSmoke excludes the ONE test that calls a real vendor endpoint (TEST_PLAN §6).
        # A trait filter, not an env flag: D67 bars environment variables for configuration, and the same
        # reasoning applies to gating — the exclusion belongs here, where it is visible and reproducible,
        # rather than in a machine's environment where it is neither. Run it deliberately with
        #   dotnet test tests/AlphaLab.Worker.Tests --filter "Category=LiveSmoke"
        Invoke-Native -What 'dotnet test' -Command {
            dotnet test 'AlphaLab.slnx' -c Debug --nologo --no-build --filter 'Category!=LiveSmoke'
        }
    }

    Write-Host '== guards ==' -ForegroundColor Cyan

    # 1. Bars are versioned append-only - never UPDATE or DELETE a bar row (rule 3). Word-boundary
    #    \bbars\b so 'UPDATE barstool' does not false-positive (v1.9.6).
    $codeFiles = Get-ChildItem -Path (Join-Path $repoRoot 'src'), (Join-Path $repoRoot 'tests'), (Join-Path $repoRoot 'tools') -Recurse -File -Include *.cs, *.sql -ErrorAction SilentlyContinue |
        Where-Object { $_.FullName -notmatch '\\(bin|obj)\\' } | ForEach-Object { $_.FullName }
    Assert-NoMatch -Files $codeFiles -Pattern 'DELETE\s+FROM\s+bars\b' -Message 'DELETE FROM bars is forbidden (rule 3).'
    Assert-NoMatch -Files $codeFiles -Pattern 'UPDATE\s+bars\b'         -Message 'UPDATE bars is forbidden (rule 3).'

    # 1a-bis. THE RECOMPUTE HARNESS WRITES NOTHING (D117 clause 1), and the claim is now CHECKED rather
    #     than merely asserted. RecomputeHarness's own summary says "no SaveChanges, anywhere on this
    #     path" - a statement about the code that nothing verified, which is the shape D140 forbids: a
    #     line may state a fact it verifies, or state that it cannot verify it, but not state a fact
    #     whose truth it never examines. It was the last of the three such claims the 6.5 sweep found.
    #     Cheap to enforce because the whole path lives in one directory: report-only means the operator
    #     can point this verb at the LIVE store, so a stray write here is a write to the frozen generation.
    #     The pattern matches a CALL (`.SaveChanges(`), not the bare word: the first draft matched the
    #     doc comment that MAKES the claim, which is a fitting way to be wrong but still wrong.
    $recomputeFiles = Get-ChildItem -Path (Join-Path $repoRoot 'src/AlphaLab.Evaluation/Recompute') -Recurse -File -Include *.cs -ErrorAction SilentlyContinue |
        Where-Object { $_.FullName -notmatch '\\(bin|obj)\\' } | ForEach-Object { $_.FullName }
    Assert-NoMatch -Files $recomputeFiles -Pattern '\.SaveChanges\s*\(' -Message 'The recompute harness must not write (D117 clause 1): a SaveChanges CALL is forbidden under src/AlphaLab.Evaluation/Recompute.'

    # 1b. corporate_actions is versioned append-only too (D76 extended rule 3 to a second table); the
    #     guard follows. A restatement INSERTs a new version — never an UPDATE/DELETE. (The always-NULL
    #     processed_on column was dropped by D94/M5, resolving P5.)
    Assert-NoMatch -Files $codeFiles -Pattern 'DELETE\s+FROM\s+corporate_actions\b' -Message 'DELETE FROM corporate_actions is forbidden (D76 append-only).'
    Assert-NoMatch -Files $codeFiles -Pattern 'UPDATE\s+corporate_actions\b'         -Message 'UPDATE corporate_actions is forbidden (D76 append-only).'

    # 1c. config is the THIRD append-only-versioned table (rule 24 / D72, finding 108): a change INSERTs
    #     (key, version+1) and the current value is MAX(version) per key. Never an UPDATE or DELETE - the
    #     frozen calibration rows ARE the audit trail, and a mutated one is indistinguishable from a value
    #     that was always there. Added at v1.9.85 (finding 365): the invariant held by review alone until
    #     now, and this corpus has already shown (finding 363) that an unchecked invariant can be broken
    #     for two generations without anyone noticing.
    #
    #     SCOPED TO src + tools, NOT tests - deliberately, and this is the one exclusion in the guard set.
    #     TWO test sites rely on it, and both are named here because the comment used to name only one
    #     (D151): DetectabilityGateTests clears its Calibration.DetectionPower row to exercise the
    #     no-curves branch, and ReplayEngineTests clears Replay.LedgerArithmeticVersion to build the
    #     unstamped-generation case D144 refuses. Both are tests CONSTRUCTING a scenario in a throwaway
    #     fixture DB, not the lab mutating its own config history. If a future test needs the same,
    #     prefer a fresh arena.
    $srcToolFiles = Get-ChildItem -Path (Join-Path $repoRoot 'src'), (Join-Path $repoRoot 'tools') -Recurse -File -Include *.cs, *.sql -ErrorAction SilentlyContinue |
        Where-Object { $_.FullName -notmatch '\\(bin|obj)\\' } | ForEach-Object { $_.FullName }
    Assert-NoMatch -Files $srcToolFiles -Pattern 'DELETE\s+FROM\s+config\b' -Message 'DELETE FROM config is forbidden - config is append-only-versioned (rule 24 / D72).'
    Assert-NoMatch -Files $srcToolFiles -Pattern 'UPDATE\s+config\b'         -Message 'UPDATE config is forbidden - a change INSERTs (key, version+1) (rule 24 / D72).'
    Assert-NoMatch -Files $srcToolFiles -Pattern 'Config\.Remove(Range)?\s*\(' -Message 'db.Config.Remove/RemoveRange is forbidden - config is append-only-versioned (rule 24 / D72).'

    # 1d. THE SAME THREE APPEND-ONLY TABLES, GUARDED AGAINST THE IDIOM THIS CODEBASE ACTUALLY WRITES
    #     (D151, finding 418). Guards 1, 1b and the SQL halves of 1c match `DELETE FROM x` / `UPDATE x`,
    #     and those strings appear NOWHERE in src, tests or tools - not once, including migrations. Six
    #     patterns with nothing to match, while the repo performs DML through EF: 43 ExecuteDelete /
    #     ExecuteUpdate / Remove / RemoveRange sites across ReplayRunner, ScratchStore and LedgerStore.
    #     So "CI greps enforce" (hard rule 3) was a claim about a check that could not see the writes it
    #     claimed to forbid - D140's shape. The SQL patterns are KEPT rather than replaced: they cost
    #     nothing and they still cover a future raw-SQL migration.
    #
    #     TABLE-QUALIFIED ON PURPOSE, and this is the whole design of the pattern. An unqualified
    #     \.ExecuteDelete\( would fire on all 41 LEGITIMATE calls in ReplayRunner.DeleteReplayGeneration
    #     and ScratchStore.Rewind - the replay --reset and catch-up rewind paths the arena depends on -
    #     and a bare \.Remove\( would fire on HashSet<T>.Remove in HistoricalMembershipIngestion and
    #     MembershipRefresh. Anchoring on the DbSet name immediately before the verb excludes all of them,
    #     and also keeps the guard off the PROSE that explains the rule (guard 5 records that its own
    #     first draft fired on a comment describing the very invariant it enforces).
    #
    #     KNOWN GAP, stated rather than hidden (guard 5's discipline): the pattern is SINGLE-LINE. Every
    #     real DML site today is written `db.<Set>.Where(...).ExecuteDelete();` on one line, but a
    #     multi-line `db.Bars\n  .Where(...)\n  .ExecuteDelete()` would evade this - and ScratchStore is
    #     ALREADY written that way for other tables (:218-220, :233-235), so the form is live in the repo
    #     and this is a real hole, not a theoretical one. The brace is that bars/corporate_actions/config
    #     have no such call at all today; if one is ever added legitimately, widen this to a multiline scan.
    #
    #     SCOPED src + tools, NOT tests - guard 1c's reasoning, which applies verbatim: a test that
    #     constructs a data gap in a throwaway fixture DB (BarFeatureViewTests db.Bars.RemoveRange,
    #     DetectabilityGateTests and ReplayEngineTests on config) is not the lab mutating its own store.
    #     Note guard 1's SQL patterns above DO include tests; that asymmetry is deliberate and survives.
    $appendOnlySets = 'Bars|CorporateActions|Config'
    $efDmlVerbs = 'Remove|RemoveRange|ExecuteDelete|ExecuteUpdate|ExecuteDeleteAsync|ExecuteUpdateAsync'
    $efDmlPattern = '\b(' + $appendOnlySets + ')\s*\.\s*(' + $efDmlVerbs + ')\s*\(' +
                    '|\b(' + $appendOnlySets + ')\s*\.\s*Where\s*\(.*\)\s*\.\s*(' + $efDmlVerbs + ')\s*\('
    Assert-NoMatch -Files $srcToolFiles -Pattern $efDmlPattern `
        -Message 'EF DML on an append-only table is forbidden: bars/corporate_actions are versioned append-only (rule 3 / D40 / D76) and config INSERTs (key, version+1) (rule 24 / D72). Corrections append a new version.'

    # 1e. THE SELF-TEST FOR 1d, because a guard nobody has seen fire is a guard nobody knows works.
    #     check-register.ps1:283-311 is the precedent, and finding 383 is the lesson it records: its own
    #     first self-test RE-IMPLEMENTED the thing it was testing and so passed on an engine where the
    #     real loop failed. This one therefore calls the SAME Assert-NoMatch with the SAME $efDmlPattern
    #     variable - never a re-declared regex - against a synthetic violation, and asserts it THROWS.
    $probeDir = Join-Path ([System.IO.Path]::GetTempPath()) ("alphalab-guard-probe-" + [guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Path $probeDir -Force | Out-Null
    try {
        $probe = Join-Path $probeDir 'Probe.cs'
        # One line per form the guard must catch, including the chained .Where(...) shape.
        @(
            'db.Bars.ExecuteDelete();'
            'db.CorporateActions.Where(c => c.Id == 1).ExecuteDelete();'
            'db.Config.RemoveRange(stale);'
            'db.Config.Where(c => c.Key == k).ExecuteUpdate(s => s.SetProperty(x => x.Value, "v"));'
        ) | Set-Content -Path $probe -Encoding UTF8
        $probeLines = (Get-Content $probe).Count
        $fired = $false
        try { Assert-NoMatch -Files @($probe) -Pattern $efDmlPattern -Message 'self-test probe' -Quiet }
        catch { $fired = $true }
        if (-not $fired) { throw 'Guard 1d SELF-TEST failed: the EF-DML pattern did not fire on a synthetic violation - the guard is not enforcing what its message claims.' }

        $hitLines = (Select-String -Path $probe -Pattern $efDmlPattern -AllMatches | Measure-Object).Count
        if ($hitLines -ne $probeLines) {
            throw "Guard 1d SELF-TEST failed: the pattern matched $hitLines of $probeLines probe forms - one of the DML shapes is not covered."
        }

        # And the other direction: the guard must NOT fire on the legitimate idioms, or it gets deleted
        # the first time it blocks a replay --reset (guard 5: 'a guard that cannot be satisfied is a
        # guard that gets deleted'). These four lines are copied from real, sanctioned call sites.
        $clean = Join-Path $probeDir 'Clean.cs'
        @(
            'db.Trades.Where(t => t.RunKind == ReplayKind).ExecuteDelete();'
            'db.Positions.Remove(existing);'
            'open.Remove(id);'
            'db.Config.Add(new ConfigRow { Key = k });'
            '// Never bars, corporate_actions, config - the append-only tables stay untouched.'
        ) | Set-Content -Path $clean -Encoding UTF8
        Assert-NoMatch -Files @($clean) -Pattern $efDmlPattern -Message 'Guard 1d SELF-TEST failed: the EF-DML pattern fired on a LEGITIMATE call site or on prose describing the rule.'

        # 1f. Assert-NoMatch's empty-list behaviour is itself self-tested (finding 418): it used to
        #     silently pass, so every guard inherited a green-forever mode reachable by renaming a folder.
        $empty = $false
        try { Assert-NoMatch -Files @() -Pattern 'anything' -Message 'self-test empty scope' }
        catch { $empty = $true }
        if (-not $empty) { throw 'Assert-NoMatch SELF-TEST failed: an empty file list must fail, not pass silently.' }
    }
    finally { Remove-Item $probeDir -Recurse -Force -ErrorAction SilentlyContinue }

    # 2. No committed secret-key material (D67). appsettings.Secrets.json is gitignored, so it is
    #    excluded from the committable set below.
    $committable = Get-CommittableFiles
    Assert-NoMatch -Files $committable -Pattern 'sk-ant-[A-Za-z0-9]{12,}' -Message 'An Anthropic API key pattern is present in a committable file.'
    if ($committable | Where-Object { $_ -match 'appsettings\.Secrets\.json$' }) {
        throw 'Guard failed: appsettings.Secrets.json is committable - it must be gitignored (D67).'
    }

    # 3. The full reference graph is CI-enforced at the <ProjectReference> level (D57, BUILD 0.1):
    #    every src project may reference only the AlphaLab.* projects in its allowlist.
    Assert-ReferenceGraph

    # Register integrity (rule 25 / D109): a register row is changed only by another register row.
    # Baseline mode - the violations that predate the check are carried; anything new fails.
    & (Join-Path $PSScriptRoot 'check-register.ps1')
    if ($LASTEXITCODE -ne 0) { throw 'check-register failed.' }

    # 3b. Belt-and-suspenders at the SOURCE level for the UI boundary: AlphaLab.Web must not even
    #     `using` Evaluation/Data (a source reach the graph check cannot see, e.g. a transitive type).
    $webDir = Join-Path $repoRoot 'src/AlphaLab.Web'
    $webCs = Get-ChildItem -Path $webDir -Recurse -File -Include *.cs, *.razor -ErrorAction SilentlyContinue |
        Where-Object { $_.FullName -notmatch '\\(bin|obj)\\' } | ForEach-Object { $_.FullName }
    Assert-NoMatch -Files $webCs -Pattern 'using\s+AlphaLab\.(Evaluation|Data)' -Message 'AlphaLab.Web must not use AlphaLab.Evaluation/AlphaLab.Data (D57).'

    # 4. The Signal Library is DESCRIPTIVE ONLY (D91, MASTER 24.5): its output is never an input to the
    #    allocator, any gate, sizing, or eligibility. Scoped to those CONSUMER directories on purpose -
    #    an unscoped scan would fire on the library's own engine, read-model, entities and tests.
    #
    #    This is the belt; the brace is DescriptiveOnlyGuardTests, an assembly-scoped reflection closure
    #    that catches a DI-injected consumer this text scan cannot see AND runs on both CI legs (greps
    #    run on the Windows leg only). Keep both: the grep catches a raw table read in SQL that carries
    #    no type, the closure catches a typed dependency that mentions no token.
    $consumerDirs = @(
        'src/AlphaLab.Evaluation/Allocator', 'src/AlphaLab.Evaluation/Gate',
        'src/AlphaLab.Evaluation/Candidates', 'src/AlphaLab.Evaluation/Power',
        'src/AlphaLab.Core/Funnel'
    ) | ForEach-Object { Join-Path $repoRoot $_ } | Where-Object { Test-Path $_ }
    $consumerCs = @()
    if ($consumerDirs) {
        $consumerCs = Get-ChildItem -Path $consumerDirs -Recurse -File -Include *.cs -ErrorAction SilentlyContinue |
            Where-Object { $_.FullName -notmatch '\\(bin|obj)\\' } | ForEach-Object { $_.FullName }
    }
    Assert-NoMatch -Files $consumerCs -Pattern 'signal_ic|ISignal\b|SignalIc|SignalLibrary' -Message 'The Signal Library is descriptive only (D91) - the allocator/gate/sizing/eligibility must never read it.'

    # 4b. GOLDEN RULE 32's COROLLARY (MASTER 23.8.4) GETS THE SAME TWO-GUARD TREATMENT (D151, finding 419).
    #     "These artifacts are read by humans, and by nothing that judges AI output": no monitor signal,
    #     gate input, allocator term or population comparison may read ai_context_packs or ai_decisions.
    #     Rule32GuardTests is the closure (the brace); this is the belt, and until now rule 32 had only
    #     the closure while D91 had both. The asymmetry mattered because the closure reads SIGNATURES:
    #     every judging class already takes AlphaLabDbContext, on which db.AiDecisions and db.AiContextPacks
    #     sit, so `db.AiDecisions.Count(...)` inside a monitor method changes no signature, compiles, and
    #     passes every reflection guard in the repo. A body-level text scan is the only thing that sees it.
    #
    #     Monitor, Calibration and ReadModels are added to the judging set here and NOT to guard 4's
    #     $consumerDirs: D91's boundary deliberately sanctions AlphaLab.Evaluation.ReadModels as the FR-46
    #     signal consumer (DescriptiveOnlyGuardTests names it), whereas rule 32 sanctions no read-model at
    #     all. Two rules, two consumer sets - merging them would quietly widen one of them.
    $judgingDirs = @(
        'src/AlphaLab.Evaluation/Allocator', 'src/AlphaLab.Evaluation/Gate',
        'src/AlphaLab.Evaluation/Candidates', 'src/AlphaLab.Evaluation/Power',
        'src/AlphaLab.Evaluation/Monitor', 'src/AlphaLab.Evaluation/Calibration',
        'src/AlphaLab.Evaluation/ReadModels', 'src/AlphaLab.Evaluation/Metrics',
        'src/AlphaLab.Core/Funnel'
    ) | ForEach-Object { Join-Path $repoRoot $_ } | Where-Object { Test-Path $_ }
    $judgingCs = @()
    if ($judgingDirs) {
        $judgingCs = Get-ChildItem -Path $judgingDirs -Recurse -File -Include *.cs -ErrorAction SilentlyContinue |
            Where-Object { $_.FullName -notmatch '\\(bin|obj)\\' } | ForEach-Object { $_.FullName }
    }
    Assert-NoMatch -Files $judgingCs -Pattern 'ai_context_packs|ai_decisions|AiContextPacks\b|AiDecisions\b|AiDecisionRow|AiContextPackRow|AiDecisionRecord|ContextPack\b|IAiDecisionStore' `
        -Message 'Golden rule 32 (MASTER 23.8.4): no monitor signal, gate input, allocator term, population comparison or read-model may read the AI-seat artefacts (ai_context_packs / ai_decisions).'

    # 4c. NO CONTROL CHARACTERS IN THE DOC CORPUS (D155, finding 428). The documents ARE the sources of
    #     truth, and a stray control byte in one is invisible in every renderer while silently corrupting
    #     the sentence around it. Two were found by a corpus sweep and BOTH arrived the same way - a
    #     PowerShell escape sequence surviving into a file because the text was assembled inside a
    #     DOUBLE-QUOTED string: `f (form feed) ate the backtick of `floor` in DESIGN_IMPROVEMENTS, leaving
    #     "the presence of the loor token"; `a (bell) ate a path separator in PROGRESS, leaving
    #     "sp500<BEL>lphalab.db". Neither is visible on screen and neither failed anything.
    #
    #     THIS IS A TOOLING HAZARD, NOT A TYPO CLASS, which is why it gets a guard rather than a proofread:
    #     every doc edit in this repo goes through a .ps1, the repo already mandates ASCII-only .ps1 files
    #     for a related reason (PS 5.1 decodes .ps1 as ANSI), and the same escape set will keep firing.
    #     Tab (0x09), LF (0x0A) and CR (0x0D) are legitimate; everything else below 0x20 is not.
    $docFiles = @(Get-ChildItem -Path (Join-Path $repoRoot 'docs') -Recurse -File -Include *.md -ErrorAction SilentlyContinue |
        ForEach-Object { $_.FullName })
    $docFiles += @('CLAUDE.md', 'PROGRESS.md', 'START_HERE.md') |
        ForEach-Object { Join-Path $repoRoot $_ } | Where-Object { Test-Path $_ }
    Assert-NoMatch -Files $docFiles -Pattern '[\x00-\x08\x0B\x0C\x0E-\x1F]' `
        -Message 'A control character is present in the documentation corpus - almost certainly a PowerShell escape (`f, `a, `b, `e, `0) that survived into the file from a double-quoted string. Rewrite the edit using a single-quoted string or a UTF-8 data file.'

    # 5. Ledger money is C# decimal persisted as TEXT, NEVER double/REAL (rule 20 / D69). Added at
    #    v1.9.85 (finding 365) for the same reason as 1c: the invariant was held by review alone.
    #
    #    The pattern is a NAME LIST, not a type scan, and that is the whole design of it. Money and rates
    #    are both numbers; only the name distinguishes them. `double ImpactK`, `double HalfSpreadBp` and
    #    `double ParticipationCapPctAdv` are CORRECT - a coefficient, a basis-point rate and a percentage
    #    are not money and must not be forced to decimal. So the guard names the members that carry
    #    currency and says nothing about anything else. It is a RATCHET: it catches a new money member
    #    declared as double, which is how this defect would actually arrive.
    #
    #    TWO SCOPING DECISIONS, both learned by RUNNING the guard rather than reasoning about it - its
    #    first execution failed on two false positives, which is the argument for writing it at all:
    #
    #    (a) ANCHORED ON AN ACCESS MODIFIER, so it matches a DECLARATION and not prose. Without this it
    #        fired on `// ... A double commission would ...` - a comment explaining this very rule.
    #    (b) src ONLY, never tests. A test theory cannot comply even in principle: C# attribute arguments
    #        may not be decimal constants, so `[InlineData]` money MUST be double or string and a test
    #        signature like `(double cash)` is forced, not sloppy. A guard that cannot be satisfied is a
    #        guard that gets deleted.
    #
    #    KNOWN GAP, stated rather than hidden: a record POSITIONAL parameter (`record X(double Cash)`)
    #    carries no access modifier and so is not matched. Every money member in src today is a property
    #    with a modifier; if that changes, widen this.
    $moneyNames = 'StartingCash|CostBasis|RawFillPrice|Commission|SpreadCost|ImpactCost|CashDelta|' +
                  'TotalCost|CashPerShare|LastPrintPrice|SpinoffBasisAllocated|Equity|Cash|Amount'
    $srcCs = Get-ChildItem -Path (Join-Path $repoRoot 'src') -Recurse -File -Include *.cs -ErrorAction SilentlyContinue |
        Where-Object { $_.FullName -notmatch '\\(bin|obj)\\' } | ForEach-Object { $_.FullName }
    Assert-NoMatch -Files $srcCs -Pattern ('\b(public|private|internal|protected)\s+(double|float)\??\s+(' + $moneyNames + ')\b') `
        -Message 'Ledger money must be decimal, never double/float (rule 20 / D69).'

    Write-Host 'CI OK' -ForegroundColor Green
}
finally {
    Pop-Location
}
