using System.Reflection;
using AlphaLab.Core.Llm;
using AlphaLab.Data;
using AlphaLab.Data.Entities;
using AlphaLab.Data.Services;
using AlphaLab.Evaluation.Ai;

namespace AlphaLab.Evaluation.Tests;

/// <summary>
/// The structural guard on golden rule 32's corollary (§23.8.4): the AI-seat artefacts are read **by
/// humans, and by nothing that judges AI output**. No monitor signal, gate input, allocator term,
/// population comparison — or context-pack field — may read <c>ai_context_packs</c> or <c>ai_decisions</c>.
///
/// **Why this is a test and not a comment.** Rule 32 was prose-only in this repo until now; the Signal
/// Library's descriptive-only boundary (v1.9.52) was the first structural enforcement of a
/// "never an input to X" rule, and this is the second. §23.8.4 names the specific hazard: *"a debugging
/// surface is exactly the sort of thing that erodes it by convenience"*. Someone wires a prior decision
/// into a pack "just to see", and AI output is now an input to the thing that prices AI output — with
/// nothing failing.
///
/// **Scoped to the assembly with the AI namespace excluded, default-deny** — deliberately NOT to a list
/// of judging namespaces, for the reason `DescriptiveOnlyGuardTests` states: a namespace enumeration fails
/// by OMISSION at exactly the edit that should have triggered it. Default-deny inverts that; the only
/// maintenance is the narrow exclusion, and forgetting to exclude an AI type reddens CI (fails safe)
/// rather than hiding a consumer.
///
/// **THE CLOSURE SPANS THREE ASSEMBLIES, AND UNTIL D151 IT SPANNED ONE (finding 419).** It scanned
/// `typeof(ContextPackBuilder).Assembly` — AlphaLab.Evaluation — while **all four** of its original
/// forbidden types are declared in **AlphaLab.Core**, and the persistence surface is in AlphaLab.Data.
/// A default-deny closure over an assembly that declares none of the types it forbids reports zero
/// offenders for a reason unrelated to conformance, so it read as enforcement while proving almost
/// nothing. `DescriptiveOnlyGuardTests` had already solved this — it concatenates two assemblies
/// (`:130-131`) — and rule 32's guard simply had not copied that line. The row entities and the pack
/// store are added to <c>Forbidden</c> in the same pass: a judging type could return
/// <c>List&lt;AiDecisionRow&gt;</c> or take an <c>IContextPackStore</c> and pass, which is a hole
/// *inside* the closure's own stated remit rather than beyond it.
///
/// **WHAT THIS CANNOT SEE, stated rather than implied: METHOD BODIES.** Every judging class already takes
/// an <c>AlphaLabDbContext</c>, and <c>db.AiDecisions</c> sits on it, so <c>db.AiDecisions.Count(...)</c>
/// inside a monitor method changes no signature and no reflection guard can observe it. That is why
/// D151 also adds `ci.ps1` guard 4b — the D91 two-guard pattern, belt and brace: the closure catches a
/// typed dependency that mentions no token, the text scan catches a token in a body that carries no type.
/// </summary>
public class Rule32GuardTests
{
    /// <summary>The AI seat's own namespaces, which are of course allowed to handle its own types.
    /// <c>AlphaLab.Core.Llm</c> is where <see cref="ContextPack"/> and friends are DECLARED, so widening
    /// the closure to that assembly without this entry would make the guard report the forbidden types
    /// themselves and go red on clean code.</summary>
    private static readonly string[] SeatNamespaces = ["AlphaLab.Evaluation.Ai", "AlphaLab.Core.Llm"];

    /// <summary>The types no judging component may touch: the pack, its field, the persisted AI record,
    /// the two store ports, and the two ROW entities — the last four added by D151, because a signature
    /// taking <c>IContextPackStore</c> or returning <c>AiDecisionRow</c> is the same breach one layer down.</summary>
    private static readonly Type[] Forbidden =
    [
        typeof(ContextPack), typeof(AiDecisionRecord), typeof(PackField), typeof(IAiDecisionStore),
        typeof(IContextPackStore), typeof(AiContextPackRow), typeof(AiDecisionRow),
    ];

    /// <summary>
    /// SANCTIONED HOLDERS — declared by TYPE, never by namespace (D123's reasoning, applied to rule 32).
    ///
    /// A <c>"AlphaLab.Data.Services"</c> namespace entry would exempt every service in that folder
    /// forever, so a future judging helper dropped beside the stores would inherit the exemption
    /// silently — the failure-by-omission this guard's default-deny design exists to prevent. Three types
    /// need it and no more: the two AI stores, which exist to persist the artefacts, and the DbContext,
    /// which must declare <c>DbSet&lt;AiContextPackRow&gt;</c> to have a table at all. Sanctioning the
    /// context is a SIGNATURE-level exemption only; the body-level reach it opens is exactly what
    /// `ci.ps1` guard 4b covers, which is why the pair is not optional.
    /// </summary>
    private static readonly Type[] SanctionedHolders =
        [typeof(AlphaLabDbContext), typeof(ContextPackStore), typeof(AiDecisionStore)];

    private static bool IsSeat(Type t) =>
        t.Namespace is { } ns && SeatNamespaces.Any(n => ns == n || ns.StartsWith(n + ".", StringComparison.Ordinal));

    private static bool IsSanctioned(Type t)
    {
        for (var cursor = t; cursor is not null; cursor = cursor.DeclaringType)
        {
            if (SanctionedHolders.Contains(cursor)) return true;
        }
        return false;
    }

    private static IEnumerable<Type> Flatten(Type t)
    {
        yield return t;
        if (t.IsGenericType)
        {
            foreach (var arg in t.GetGenericArguments())
                foreach (var inner in Flatten(arg)) yield return inner;
        }
        if (t.IsArray && t.GetElementType() is { } element)
        {
            foreach (var inner in Flatten(element)) yield return inner;
        }
    }

    private static IEnumerable<(Type Owner, string Member, Type Dependency)> SeatDependencies(Assembly assembly)
    {
        const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic |
                                 BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;

        // A forbidden type is never its own offender (it may of course mention itself), and a sanctioned
        // holder is excluded by TYPE. Everything else in the assembly is scanned — default-deny.
        foreach (var type in assembly.GetTypes().Where(t => !IsSeat(t) && !Forbidden.Contains(t) && !IsSanctioned(t)))
        {
            foreach (var ctor in type.GetConstructors(All))
                foreach (var p in ctor.GetParameters())
                    foreach (var dep in Flatten(p.ParameterType).Where(Forbidden.Contains))
                        yield return (type, $".ctor({p.Name})", dep);

            foreach (var f in type.GetFields(All))
                foreach (var dep in Flatten(f.FieldType).Where(Forbidden.Contains))
                    yield return (type, f.Name, dep);

            foreach (var pr in type.GetProperties(All))
                foreach (var dep in Flatten(pr.PropertyType).Where(Forbidden.Contains))
                    yield return (type, pr.Name, dep);

            foreach (var m in type.GetMethods(All))
            {
                foreach (var dep in Flatten(m.ReturnType).Where(Forbidden.Contains))
                    yield return (type, $"{m.Name}()", dep);
                foreach (var p in m.GetParameters())
                    foreach (var dep in Flatten(p.ParameterType).Where(Forbidden.Contains))
                        yield return (type, $"{m.Name}({p.Name})", dep);
            }
        }
    }

    [Fact]
    public void Rule32_NothingThatJudgesAiOutput_TouchesTheAiSeatArtefacts()
    {
        // D151 (finding 419): THREE assemblies, not one. Until now this scanned AlphaLab.Evaluation
        // alone, while every forbidden type is declared in AlphaLab.Core and persisted from
        // AlphaLab.Data — so a zero-offender result said nothing about the assemblies that hold the
        // artefacts. DescriptiveOnlyGuardTests concatenates its two assemblies for the same reason.
        var offenders = SeatDependencies(typeof(ContextPackBuilder).Assembly)   // AlphaLab.Evaluation
            .Concat(SeatDependencies(typeof(ContextPack).Assembly))             // AlphaLab.Core
            .Concat(SeatDependencies(typeof(AlphaLabDbContext).Assembly))       // AlphaLab.Data
            .Select(o => (o.Owner, o.Member, o.Dependency))
            .OrderBy(o => o.Owner.FullName, StringComparer.Ordinal)
            .ToList();

        Assert.True(offenders.Count == 0,
            "Golden rule 32's corollary (§23.8.4): the AI-seat artefacts are read by humans and by NOTHING " +
            "that judges AI output. These types outside AlphaLab.Evaluation.Ai expose a pack or a decision " +
            "in their signature:\n  " +
            string.Join("\n  ", offenders.Select(o => $"{o.Owner.FullName}.{o.Member} -> {o.Dependency.Name}")));
    }

    [Fact]
    public void Rule32_TheGuardActuallyFires()
    {
        // D109's four register checks were each PROVEN TO FIRE rather than merely to pass, and the lesson
        // from finding 310 was that an unproven check is worth little. This runs the same closure over a
        // deliberate violation and asserts it is caught — so a future refactor that neuters the detector
        // (a wrong assembly, an over-broad exclusion) fails HERE rather than passing silently forever.
        var offenders = SeatDependencies(typeof(DeliberateRule32Violation).Assembly)
            .Where(o => o.Owner == typeof(DeliberateRule32Violation))
            .ToList();

        Assert.NotEmpty(offenders);
    }

    [Fact]
    public void D151_AllThreeAssembliesAreActuallyScanned_NotJustNamedInTheCall()
    {
        // WHY THIS EXISTS. Two of the three arms can be falsified by breaking them: removing the
        // AlphaLabDbContext sanction makes AlphaLab.Data report offenders, and removing the
        // AlphaLab.Core.Llm seat entry reddens the narrowness test. The AlphaLab.CORE arm cannot —
        // Core is clean today, so deleting its .Concat changes no result, and "we scan Core" would be a
        // claim nothing examines (D140). This makes the coverage itself the assertion: each assembly
        // must contribute a real, non-trivial set of scanned types, so a future edit that points an arm
        // at the wrong assembly, or that widens SeatNamespaces until an arm scans nothing, fails HERE
        // rather than passing forever. The same discipline as TestParallelizationGuardTests' project
        // floor — a guard that found nothing to check would pass forever while proving nothing.
        var arms = new (string Name, Assembly Assembly)[]
        {
            ("AlphaLab.Evaluation", typeof(ContextPackBuilder).Assembly),
            ("AlphaLab.Core", typeof(ContextPack).Assembly),
            ("AlphaLab.Data", typeof(AlphaLabDbContext).Assembly),
        };

        foreach (var (name, assembly) in arms)
        {
            Assert.Equal(name, assembly.GetName().Name);

            var scanned = assembly.GetTypes()
                .Count(t => !IsSeat(t) && !Forbidden.Contains(t) && !IsSanctioned(t));
            Assert.True(scanned >= 20, $"{name}: the closure scanned only {scanned} types — an arm that scans nothing reports zero offenders for the wrong reason.");
        }

        // And the three arms are genuinely DISTINCT assemblies, so a copy-paste that pointed two of them
        // at the same one would not silently halve the coverage.
        Assert.Equal(3, arms.Select(a => a.Assembly).Distinct().Count());
    }

    [Fact]
    public void D151_TheGuardFires_OnAJudgeInANamespaceItDoesNotName()
    {
        // The DEFAULT-DENY half. Rule32_TheGuardActuallyFires catches a nested private class inside this
        // very file; that proves the reflection walks members, not that coverage is by default-deny. This
        // violator lives in a namespace the guard never enumerates and is caught by virtue of NOT being
        // the seat — the property assembly-scoping exists to deliver. DescriptiveOnlyGuardTests has the
        // same pair (`TheGuardActuallyFires_OnAConsumerInANamespaceItDoesNotName`); rule 32 had neither
        // this nor the two narrowness tests below until D151.
        var offenders = SeatDependencies(typeof(Rule32GuardTests).Assembly).ToList();

        Assert.Contains(offenders, o =>
            o.Owner == typeof(SomeUnjudgedNamespace.PretendMonitor) && o.Dependency == typeof(AiDecisionRecord));
    }

    [Fact]
    public void D151_TheSeatExclusionIsNarrow_JudgingCodeIsStillScanned()
    {
        // The exclusion must cover the seat and NOTHING more. Without this, widening SeatNamespaces to
        // "AlphaLab.Evaluation" — a one-word edit — would make the closure scan almost nothing while
        // every test above stayed green, because Rule32_TheGuardActuallyFires scans the TEST assembly.
        Assert.True(IsSeat(typeof(ContextPackBuilder)));       // AlphaLab.Evaluation.Ai
        Assert.True(IsSeat(typeof(ContextPack)));              // AlphaLab.Core.Llm

        Assert.False(IsSeat(typeof(Allocator.EnsembleAllocator)));
        Assert.False(IsSeat(typeof(Monitor.OverfittingMonitor)));
        Assert.False(IsSeat(typeof(Gate.PromotionGate)));
        Assert.False(IsSeat(typeof(AlphaLabDbContext)));
    }

    [Fact]
    public void D151_TheSanctionIsScopedToTheTypeAndNotItsNamespace()
    {
        // D123's rule, applied here: a namespace entry for AlphaLab.Data.Services would exempt every
        // service in that folder forever, including one added later for an entirely different reason.
        Assert.True(IsSanctioned(typeof(ContextPackStore)));
        Assert.True(IsSanctioned(typeof(AiDecisionStore)));
        Assert.True(IsSanctioned(typeof(AlphaLabDbContext)));

        Assert.False(IsSanctioned(typeof(LedgerStore)));       // its namespace-mate, NOT sanctioned
        Assert.False(IsSanctioned(typeof(BarIngestionService)));

        // And the sanction is narrow in the other direction: it exempts a HOLDER, never an artefact, so
        // it cannot be used to smuggle a forbidden type out of scope.
        Assert.DoesNotContain(typeof(ContextPack), SanctionedHolders);
        Assert.DoesNotContain(typeof(AiDecisionRecord), SanctionedHolders);
    }

    /// <summary>A deliberate violation living in the TEST assembly — never in the product one — so the
    /// guard above has something to catch without shipping a real breach.</summary>
    private sealed class DeliberateRule32Violation
    {
        public ContextPack? Pack { get; init; }
    }
}
