namespace AlphaLab.Worker;

/// <summary>
/// Computes the trading sessions the Worker missed and must replay in order (D47/D54). The real
/// implementation is <see cref="MissedSessionResolver"/>; the runs table IS the state (no state machine),
/// so this is a pure query over runs + bars + the calendar + the clock, never a mutation.
/// </summary>
public interface IMissedSessionResolver
{
    Task<IReadOnlyList<DateOnly>> ResolveAsync(CancellationToken cancellationToken = default);
}
