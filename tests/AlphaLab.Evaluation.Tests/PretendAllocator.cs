using AlphaLab.Core.Signals;

namespace SomeUnnamedNamespace;

/// <summary>
/// A DELIBERATE violation of the §24.5 descriptive-only boundary, in a namespace
/// <c>DescriptiveOnlyGuardTests</c> never enumerates. It exists so the guard's negative half proves
/// DEFAULT-DENY coverage — that a consumer is caught by virtue of not being the library, rather than by
/// appearing on some list — which is the exact failure mode assembly-scoping exists to close.
///
/// Test assembly only. It is never referenced by production code, and the guard's positive half scans
/// AlphaLab.Core and AlphaLab.Evaluation, not this assembly.
/// </summary>
internal sealed class PretendAllocator(ISignal signal)
{
    public ISignal Signal { get; } = signal;
}
