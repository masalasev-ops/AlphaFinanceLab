using System.Text.Json;
using AlphaLab.Core.Json;
using AlphaLab.Core.Signals;
using AlphaLab.Data.Entities;

namespace AlphaLab.Data.Services;

/// <summary>
/// Writes the pre-registered <see cref="SignalRegistry.V1"/> set into the <c>signals</c> table (D91,
/// FR-43). Idempotent: an already-registered signal is LEFT UNTOUCHED, never rewritten.
///
/// WHY LEFT UNTOUCHED RATHER THAN UPSERTED. Registry rows are frozen instruments (§24.1/§24.3): the
/// grades in <c>signal_ic</c> were computed by the <c>code_version</c> recorded beside the params, so
/// silently rewriting a row would leave a grade record describing arithmetic that no longer exists. A
/// genuine change is a NEW registration — new id, or a deliberate migration that also restates the
/// affected grades — never an edit in place. This mirrors the plant-seeding rule (an existing plant is
/// left alone because its parameters are frozen in its id).
///
/// A registration is NOT a candidate (D99): no `strategies` row, no `trials_registry` row, and
/// `CandidateFactory` is never involved, so registering an instrument spends none of the trials budget.
/// </summary>
public sealed class SignalRegistrar(AlphaLabDbContext db)
{
    /// <summary>
    /// Register any of the v1 signals not already present, stamped <paramref name="registeredOn"/>.
    /// Returns the number of rows actually written (0 on a re-run, which is the idempotency contract).
    /// </summary>
    public int RegisterV1(string registeredOn)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(registeredOn);

        var existing = db.Signals.Select(s => s.SignalId).ToHashSet(StringComparer.Ordinal);
        var added = 0;
        foreach (var signal in SignalRegistry.V1)
        {
            if (existing.Contains(signal.SignalId)) continue;
            db.Signals.Add(new SignalRow
            {
                SignalId = signal.SignalId,
                Family = signal.Family,
                // Ordered by key so the frozen JSON is byte-stable across runs and platforms — the row is
                // a provenance record, and a record whose bytes wobble cannot back a determinism claim.
                ConfigJson = JsonSerializer.Serialize(
                    signal.Params.OrderBy(p => p.Key, StringComparer.Ordinal)
                        .ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal),
                    AlphaLabJson.Options),
                CodeVersion = signal.CodeVersion,
                RegisteredOn = registeredOn,
            });
            added++;
        }
        if (added > 0) db.SaveChanges();
        return added;
    }
}
