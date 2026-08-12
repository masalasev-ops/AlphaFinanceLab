using AlphaLab.Core.Llm;

namespace SomeUnjudgedNamespace;

/// <summary>
/// A DELIBERATE violation of golden rule 32's corollary (§23.8.4), in a namespace
/// <c>Rule32GuardTests</c> never enumerates. It exists so the guard's default-deny half has something to
/// prove: that a judging component is caught by virtue of NOT being the AI seat, rather than by appearing
/// on some list of judging namespaces — which is the failure mode assembly-scoping exists to close.
///
/// It is the sibling of <c>SomeUnnamedNamespace.PretendAllocator</c>, which does the same job for the
/// D91 descriptive-only boundary. Test assembly only: never referenced by production code, and the
/// guard's positive half scans AlphaLab.Evaluation, AlphaLab.Core and AlphaLab.Data, not this one.
/// </summary>
internal sealed class PretendMonitor(AiDecisionRecord priorDecision)
{
    public AiDecisionRecord PriorDecision { get; } = priorDecision;
}
