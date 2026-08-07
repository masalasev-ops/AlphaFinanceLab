using Xunit;

// ---------------------------------------------------------------------------------------------------
// P20 (finding 387) - TEST PARALLELIZATION IS DISABLED ASSEMBLY-WIDE, DELIBERATELY.
//
// `SqliteConnection.ClearAllPools()` is PROCESS-GLOBAL. It is called from per-test cleanup at thirteen
// sites across AlphaLab.Data.Tests, AlphaLab.Worker.Tests and AlphaLab.Strategies.Tests, while xUnit
// runs test classes in parallel by default - so one class's teardown disposes a connection another
// class is still using, and the victim throws `ObjectDisposedException: SQLitePCL.sqlite3`, usually
// inside `Migrate()`. Measured at 1 failure in 3 runs on clean `main`.
//
// WHY IT HAD TO BE FIXED RATHER THAN RE-RUN: finding 383 established that the remote check is part of
// the gate, and finding 310's lesson is that a guard which cries wolf gets switched off. An
// intermittently red suite makes every Phase-6 DoD unverifiable and trains the operator to re-run
// rather than read.
//
// THE ATTRIBUTE IS ON EVERY TEST ASSEMBLY, not only the three that own the call sites, because the
// call is process-global: a project with no site of its own is still a VICTIM of one. That uniformity
// is what `TestParallelizationGuardTests` asserts, so a new project cannot silently opt out.
//
// THE LONG-TERM FIX IS `Pooling=False`, NOT THIS. See PROGRESS's P20 entry for its named triggers -
// this attribute makes the call SAFE, it does not make it CORRECT, and the idiom stays in the code.
// ---------------------------------------------------------------------------------------------------
[assembly: CollectionBehavior(DisableTestParallelization = true)]
