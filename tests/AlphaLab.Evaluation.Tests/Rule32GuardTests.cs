using System.Reflection;
using AlphaLab.Core.Llm;
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
/// </summary>
public class Rule32GuardTests
{
    /// <summary>The AI seat's own namespaces, which are of course allowed to handle its own types.</summary>
    private static readonly string[] SeatNamespaces = ["AlphaLab.Evaluation.Ai"];

    /// <summary>The types no judging component may touch: the persisted AI record, and the pack.</summary>
    private static readonly Type[] Forbidden =
        [typeof(ContextPack), typeof(AiDecisionRecord), typeof(PackField), typeof(IAiDecisionStore)];

    private static bool IsSeat(Type t) =>
        t.Namespace is { } ns && SeatNamespaces.Any(n => ns == n || ns.StartsWith(n + ".", StringComparison.Ordinal));

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

        foreach (var type in assembly.GetTypes().Where(t => !IsSeat(t)))
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
        var offenders = SeatDependencies(typeof(ContextPackBuilder).Assembly).ToList();

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

    /// <summary>A deliberate violation living in the TEST assembly — never in the product one — so the
    /// guard above has something to catch without shipping a real breach.</summary>
    private sealed class DeliberateRule32Violation
    {
        public ContextPack? Pack { get; init; }
    }
}
