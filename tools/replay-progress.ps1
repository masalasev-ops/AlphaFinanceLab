# Read-only progress reading for an in-flight Arena Replay / calibration run (RUNBOOK section 8).
# Safe to run at any time: it opens the arena store with mode=ro and never writes.
#
# Implemented over python's stdlib sqlite3 because the repo ships no SQLite client for PowerShell and
# this is an ops convenience, not part of the build. If python is absent the script says so and stops
# rather than reporting a hollow zero.

[CmdletBinding()]
param(
    [string]$Arena = 'sp500',
    [string]$From = '2006-01-03',
    [string]$To = '2025-12-31'
)

$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'Resolve-AlphaLabConnection.ps1')

$cs = Resolve-AlphaLabConnectionString -Arena $Arena
$dbPath = Get-AlphaLabDataSourcePath -ConnectionString $cs
if (-not (Test-AlphaLabStoreExists -DbPath $dbPath)) {
    throw "No arena store at '$dbPath' - nothing to report on."
}

$python = Get-Command python -ErrorAction SilentlyContinue
if (-not $python) { $python = Get-Command py -ErrorAction SilentlyContinue }
if (-not $python) {
    throw "This script needs python on PATH (stdlib sqlite3 only) to read the store read-only. Install python or query the store yourself."
}

$worker = @(Get-Process -Name 'AlphaLab.Worker' -ErrorAction SilentlyContinue)

$script = @'
import sqlite3, sys

db, frm, to = sys.argv[1], sys.argv[2], sys.argv[3]
c = sqlite3.connect("file:" + db.replace("\\", "/") + "?mode=ro", uri=True)
cur = c.cursor()

target = cur.execute(
    "select count(*) from trading_calendar where date between ? and ? "
    "and session in (1,'1','true','True')", (frm, to)).fetchone()[0]
if not target:
    target = cur.execute(
        "select count(*) from trading_calendar where date between ? and ?", (frm, to)).fetchone()[0]

done, lo, hi = cur.execute(
    "select count(*), min(as_of), max(as_of) from runs "
    "where run_kind='replay' and status='ok'").fetchone()
running = cur.execute(
    "select count(*) from runs where run_kind='replay' and status='running'").fetchone()[0]
failed = cur.execute(
    "select count(*) from runs where run_kind='replay' and status='failed'").fetchone()[0]
marks = [r[0] for r in cur.execute(
    "select distinct watermark from runs where run_kind='replay'")]
retired = cur.execute(
    "select count(*) from overfitting_status where run_kind='replay' "
    "and lower(status)='retired'").fetchone()[0]
would = cur.execute(
    "select count(*) from go_live_log where run_kind='replay' and verdict='WouldRevert'").fetchone()[0]

# throughput from the most recent committed days, which tracks the current pace rather than the
# average since launch (replay slows as history accumulates)
recent = [r[0] for r in cur.execute(
    "select finished_at from runs where run_kind='replay' and status='ok' "
    "and finished_at is not null order by run_id desc limit 100")]
c.close()

done = done or 0
pct = (done / target * 100) if target else 0
W = 40
fill = int(W * done / target) if target else 0
bar = "#" * fill + "." * (W - fill)

print()
print("  Arena replay / calibration   (at %s)" % (hi or "-"))
print("  [%s] %5.1f%%   %d/%d sessions" % (bar, pct, done, target))
print()
print("  window : %s .. %s   (target %s .. %s)" % (lo or "-", hi or "-", frm, to))
print("  status : %d running   |   %d failed" % (running, failed))
print("  vintage: %s" % (", ".join(marks) if marks else "(no replay rows)"))
print("  plants : retired %d (must stay 0)  |  WouldRevert logged %d" % (retired, would))

if len(recent) >= 2:
    from datetime import datetime
    fmt = "%Y-%m-%dT%H:%M:%SZ"
    newest, oldest = datetime.strptime(recent[0], fmt), datetime.strptime(recent[-1], fmt)
    span_hr = (newest - oldest).total_seconds() / 3600.0
    if span_hr > 0:
        rate = (len(recent) - 1) / span_hr
        left = max(target - done, 0)
        print("  pace   : %.1f sessions/hr over the last %d   |   ~%.1f h (%.1f days) remaining"
              % (rate, len(recent), left / rate, left / rate / 24.0))
print()
if len(marks) > 1:
    print("  WARNING: more than one replay watermark present - mixed vintages poison the D56 curves.")
    print()
'@

$script | & $python.Source - $dbPath $From $To

if ($worker.Count -eq 0) {
    Write-Host "  (no AlphaLab.Worker process running - the figures above are a stopped run)"
    Write-Host ''
}
