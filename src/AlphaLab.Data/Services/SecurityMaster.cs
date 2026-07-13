using AlphaLab.Data.Entities;

namespace AlphaLab.Data.Services;

/// <summary>
/// The security master (FR-3 / D39): all identity is a permanent <c>security_id</c>; tickers are
/// time-ranged aliases in <c>ticker_history</c>. A ticker change never mints a new identity — it
/// closes the old alias interval and opens a new one under the SAME security_id, and records a
/// <c>corporate_actions(type='ticker_change')</c> row. Symbols are recycled across distinct
/// securities over time (D39), so a symbol resolves to an id only WITHIN a validity interval.
/// </summary>
public interface ISecurityMaster
{
    /// <summary>Resolve a (symbol, exchange) to its security_id AS OF a date via the ticker_history
    /// interval that contains <paramref name="asOf"/> (valid_from ≤ asOf &lt; valid_to). Null if none.</summary>
    long? ResolveAsOf(string symbol, string exchange, string asOf);

    /// <summary>Register a brand-new security with its opening ticker_history alias. Returns the
    /// assigned security_id. Persists immediately (identity is atomic).</summary>
    long Register(string symbol, string exchange, string firstSeen,
        string? name = null, string? sector = null, string? industry = null);

    /// <summary>Resolve the symbol as of the date, or register it (firstSeen = asOf) if unseen.</summary>
    long ResolveOrRegister(string symbol, string exchange, string asOf);

    /// <summary>Record a ticker change on <paramref name="effectiveDate"/>: close the current alias
    /// (valid_to = effectiveDate), open a new one (valid_from = effectiveDate), update
    /// securities.current_symbol, and write a ticker_change corporate action — all under the SAME
    /// security_id (zero identity break). Persists atomically.</summary>
    void RecordTickerChange(long securityId, string newSymbol, string effectiveDate, string? observedAt = null);
}

/// <summary>EF-backed <see cref="ISecurityMaster"/>. Identity mutations persist atomically via a
/// single SaveChanges so a half-applied ticker change can never exist.</summary>
public sealed class SecurityMaster(AlphaLabDbContext db) : ISecurityMaster
{
    public long? ResolveAsOf(string symbol, string exchange, string asOf)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(symbol);
        ArgumentException.ThrowIfNullOrWhiteSpace(asOf);

        // Few rows per symbol (aliases are rare); pull candidates and range-filter in memory so the
        // date comparison is plain ordinal string compare (ISO-8601 sorts chronologically) — no
        // dependence on EF's string.Compare translation.
        var candidates = db.TickerHistory.Where(t => t.Symbol == symbol).ToList();
        foreach (var t in candidates)
        {
            var startsOnOrBefore = string.CompareOrdinal(t.ValidFrom, asOf) <= 0;
            var endsAfter = t.ValidTo is null || string.CompareOrdinal(asOf, t.ValidTo) < 0;
            if (startsOnOrBefore && endsAfter)
            {
                var sec = db.Securities.Find(t.SecurityId);
                if (sec is not null && sec.Exchange == exchange)
                {
                    return t.SecurityId;
                }
            }
        }
        return null;
    }

    public long Register(string symbol, string exchange, string firstSeen,
        string? name = null, string? sector = null, string? industry = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(symbol);
        ArgumentException.ThrowIfNullOrWhiteSpace(exchange);
        ArgumentException.ThrowIfNullOrWhiteSpace(firstSeen);

        var sec = new SecurityRow
        {
            CurrentSymbol = symbol,
            Exchange = exchange,
            FirstSeen = firstSeen,
            Name = name,
            Sector = sector,
            Industry = industry
        };
        db.Securities.Add(sec);
        db.SaveChanges(); // assigns security_id (rowid)

        db.TickerHistory.Add(new TickerHistoryRow
        {
            SecurityId = sec.SecurityId,
            Symbol = symbol,
            ValidFrom = firstSeen,
            ValidTo = null
        });
        db.SaveChanges();
        return sec.SecurityId;
    }

    public long ResolveOrRegister(string symbol, string exchange, string asOf) =>
        ResolveAsOf(symbol, exchange, asOf) ?? Register(symbol, exchange, asOf);

    public void RecordTickerChange(long securityId, string newSymbol, string effectiveDate, string? observedAt = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(newSymbol);
        ArgumentException.ThrowIfNullOrWhiteSpace(effectiveDate);

        var sec = db.Securities.Find(securityId)
            ?? throw new InvalidOperationException($"security_id {securityId} not found.");

        // Close the current (open-ended) alias.
        var current = db.TickerHistory
            .Where(t => t.SecurityId == securityId && t.ValidTo == null)
            .ToList()
            .SingleOrDefault()
            ?? throw new InvalidOperationException($"security_id {securityId} has no open ticker_history alias.");
        current.ValidTo = effectiveDate;

        // Open the new alias under the SAME id.
        db.TickerHistory.Add(new TickerHistoryRow
        {
            SecurityId = securityId,
            Symbol = newSymbol,
            ValidFrom = effectiveDate,
            ValidTo = null
        });

        sec.CurrentSymbol = newSymbol;

        db.CorporateActions.Add(new CorporateActionRow
        {
            SecurityId = securityId,
            Type = "ticker_change",
            EffectiveDate = effectiveDate,
            NewSymbol = newSymbol,
            ObservedAt = observedAt ?? effectiveDate,
            Source = "eodhd"
        });

        db.SaveChanges(); // one transaction: close + open + rename + action
    }
}
