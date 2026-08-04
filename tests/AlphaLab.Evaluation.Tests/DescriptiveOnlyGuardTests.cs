using System.Reflection;
using AlphaLab.Core.Signals;
using AlphaLab.Evaluation.Construction;
using AlphaLab.Evaluation.Signals;

namespace AlphaLab.Evaluation.Tests;

/// <summary>
/// The structural guard on §24.5's descriptive-only boundary: nothing that judges a strategy may take a
/// dependency on the Signal Library. Before this, the boundary was prose in three documents — and this
/// is the FIRST structural enforcement of a "never an input to X" rule in the repo (rule 32 is still
/// prose-only; the bars rule is enforced, but as a SQL grep).
///
/// SCOPED TO THE ASSEMBLY WITH THE LIBRARY'S OWN TYPES EXCLUDED — deliberately NOT to a list of consumer
/// namespaces. A namespace enumeration fails by OMISSION at exactly the edit that should have triggered
/// it: someone adds `AlphaLab.Evaluation.Sizing`, wires a signal into it, and the guard says nothing
/// because nobody remembered to extend the list. Default-deny inverts that — a new consumer namespace is
/// covered the moment it exists, and the only maintenance is the narrow exclusion, which fails SAFE
/// (forgetting to exclude a library type reddens CI; it cannot hide a consumer).
///
/// This runs on BOTH CI legs, unlike the companion ci.ps1 grep which runs on the Windows leg only.
/// </summary>
public class DescriptiveOnlyGuardTests
{
    /// <summary>The library's own types, which are of course allowed to depend on themselves.</summary>
    private static readonly string[] LibraryNamespaces =
    [
        "AlphaLab.Core.Signals",
        "AlphaLab.Evaluation.Signals",
        "AlphaLab.Evaluation.ReadModels",   // the FR-46 read-model builder IS the sanctioned consumer
    ];

    private static readonly Type[] Forbidden = [typeof(ISignal), typeof(SignalGrade), typeof(SignalContext)];

    /// <summary>
    /// SANCTIONED CONSUMERS — declared by TYPE, never by namespace (D123, v1.9.88).
    ///
    /// <see cref="ConstructionStudyEngine"/> reads every registered scorer to measure what a long-only
    /// and a long-short construction do to tracking error. That is legitimate under §24.5 on the rule's
    /// own terms: it is not the allocator, a gate, sizing, or eligibility, it judges no strategy, and
    /// nothing reads its output at runtime — it writes one archived markdown report and returns.
    ///
    /// WHY BY TYPE AND NOT BY NAMESPACE, which is what the three entries above use. A namespace entry
    /// would exempt `AlphaLab.Evaluation.Construction` *forever*, so a future type dropped into that
    /// folder — an allocator input, say — would inherit the exemption silently. That is the failure
    /// by OMISSION this guard's whole default-deny design exists to prevent, and it would be perverse to
    /// reintroduce it while adding to the exclusion list. A type entry costs one line per genuinely
    /// sanctioned consumer and leaves everything else caught.
    ///
    /// THE ALTERNATIVE WAS WORSE. The engine could have taken a scoring DELEGATE instead of an
    /// `ISignal`, which would compile clean and never trip this guard. That is obfuscation, not
    /// compliance: the dependency would still exist, and the one instrument built to make it visible
    /// would have stopped seeing it. Declaring the consumer is the honest form.
    /// </summary>
    private static readonly Type[] SanctionedConsumers = [typeof(ConstructionStudyEngine)];

    private static bool IsLibrary(Type t) =>
        t.Namespace is { } ns && LibraryNamespaces.Any(n => ns == n || ns.StartsWith(n + ".", StringComparison.Ordinal));

    /// <summary>A sanctioned type, or one nested inside it (the engine's private per-signal accumulator
    /// is a distinct <see cref="Type"/> and would otherwise be reported on its own).</summary>
    private static bool IsSanctioned(Type t)
    {
        for (var cursor = t; cursor is not null; cursor = cursor.DeclaringType)
        {
            if (SanctionedConsumers.Contains(cursor)) return true;
        }
        return false;
    }

    /// <summary>Every type a member's signature exposes, flattened through generics (so a
    /// <c>Func&lt;ISignal&gt;</c> or <c>IReadOnlyList&lt;ISignal&gt;</c> is caught, not just a bare parameter).</summary>
    private static IEnumerable<Type> Flatten(Type t)
    {
        yield return t;
        if (t.IsGenericType)
        {
            foreach (var arg in t.GetGenericArguments())
            {
                foreach (var inner in Flatten(arg)) yield return inner;
            }
        }
        if (t.IsArray && t.GetElementType() is { } element)
        {
            foreach (var inner in Flatten(element)) yield return inner;
        }
    }

    private static IEnumerable<(Type Owner, string Member, Type Dependency)> SignalDependencies(Assembly assembly)
    {
        const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic |
                                 BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;

        foreach (var type in assembly.GetTypes().Where(t => !IsLibrary(t) && !IsSanctioned(t)))
        {
            foreach (var ctor in type.GetConstructors(All))
            {
                foreach (var p in ctor.GetParameters())
                {
                    foreach (var dep in Flatten(p.ParameterType).Where(Forbidden.Contains))
                        yield return (type, $".ctor({p.Name})", dep);
                }
            }
            foreach (var f in type.GetFields(All))
            {
                foreach (var dep in Flatten(f.FieldType).Where(Forbidden.Contains))
                    yield return (type, $"field {f.Name}", dep);
            }
            foreach (var p in type.GetProperties(All))
            {
                foreach (var dep in Flatten(p.PropertyType).Where(Forbidden.Contains))
                    yield return (type, $"property {p.Name}", dep);
            }
            foreach (var m in type.GetMethods(All))
            {
                foreach (var dep in Flatten(m.ReturnType).Where(Forbidden.Contains))
                    yield return (type, $"method {m.Name} (return)", dep);
                foreach (var param in m.GetParameters())
                {
                    foreach (var dep in Flatten(param.ParameterType).Where(Forbidden.Contains))
                        yield return (type, $"method {m.Name}({param.Name})", dep);
                }
            }
        }
    }

    [Fact]
    public void NothingOutsideTheLibrary_TakesADependencyOnASignal()
    {
        var offenders = SignalDependencies(typeof(SignalIcEngine).Assembly)          // AlphaLab.Evaluation
            .Concat(SignalDependencies(typeof(ISignal).Assembly))                    // AlphaLab.Core
            .Select(x => $"{x.Owner.FullName}.{x.Member} depends on {x.Dependency.Name}")
            .OrderBy(s => s, StringComparer.Ordinal)
            .ToList();

        Assert.True(offenders.Count == 0,
            "Signal Library output is DESCRIPTIVE ONLY (§24.5): it must never be an input to the allocator, " +
            "a gate, sizing, or eligibility. These members take a dependency on it:\n  " +
            string.Join("\n  ", offenders));
    }

    [Fact]
    public void TheGuardActuallyFires_OnAConsumerInANamespaceItDoesNotName()
    {
        // The negative half, and the specific failure assembly-scoping exists to close. This fake
        // consumer lives in a namespace the guard never enumerates — under default-deny it is caught
        // anyway. Without this, a guard that scanned nothing would pass its own test
        // (the ConfigConsistencyTests "not a LINQ tautology" discipline).
        var offenders = SignalDependencies(typeof(DescriptiveOnlyGuardTests).Assembly).ToList();

        Assert.Contains(offenders, o =>
            o.Owner == typeof(SomeUnnamedNamespace.PretendAllocator) && o.Dependency == typeof(ISignal));
    }

    [Fact]
    public void TheExclusionIsNarrow_TheLibraryItselfIsNotScanned()
    {
        // The exclusion must cover the library and nothing more, or it would hide real consumers.
        Assert.True(IsLibrary(typeof(ISignal)));
        Assert.True(IsLibrary(typeof(SignalIcEngine)));
        Assert.False(IsLibrary(typeof(Numerics.NeweyWest)));
        Assert.False(IsLibrary(typeof(Power.MdeCalculator)));
        Assert.False(IsLibrary(typeof(Allocator.EnsembleAllocator)));
    }

    /// <summary>
    /// The D123 sanction is TYPE-scoped, and this is the assertion that keeps it that way.
    ///
    /// `ConstructionStudyEngine` is exempt; `AdjClosePanel` — its immediate neighbour in the SAME
    /// namespace — is not. Had the sanction been written as a namespace entry (which is how the three
    /// library exclusions above are written), everything ever added to
    /// `AlphaLab.Evaluation.Construction` would inherit the exemption silently, which is precisely the
    /// failure-by-omission that assembly-scoped default-deny exists to close. This test fails the moment
    /// someone widens it.
    /// </summary>
    [Fact]
    public void TheD123Sanction_IsScopedToTheTypeAndNotItsNamespace()
    {
        Assert.True(IsSanctioned(typeof(ConstructionStudyEngine)));
        Assert.False(IsSanctioned(typeof(AdjClosePanel)));
        Assert.False(IsSanctioned(typeof(ConstructionStudyOptions)));
        Assert.False(IsSanctioned(typeof(SignalMeasurement)));

        // And the sanction is narrow in the other direction too: it exempts a consumer, never a
        // library type, so it cannot be used to smuggle the boundary itself out of scope.
        Assert.False(SanctionedConsumers.Contains(typeof(ISignal)));
    }
}

