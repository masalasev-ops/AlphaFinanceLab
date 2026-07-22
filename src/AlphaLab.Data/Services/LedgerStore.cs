using AlphaLab.Core.Domain;
using AlphaLab.Core.Ledger;
using AlphaLab.Data.Entities;

namespace AlphaLab.Data.Services;

/// <summary>
/// Persists and reads the ledger (SCHEMA accounts/positions/trades/cash_events/equity_curve/
/// capacity_rejections). The pure Core engines decide WHAT happens; this writes it down.
///
/// TRANSACTIONS ARE THE CALLER'S. Like every other AlphaLab.Data service, this calls SaveChanges
/// but opens no transaction of its own. The D53 pipeline wraps a whole trading day in ONE explicit
/// transaction (Golden Rule 16), and EF enlists these saves into it rather than auto-committing —
/// which is exactly how the existing ingestion services get folded into the daily write.
///
/// QUARANTINE (hard rule 1 / D37). Every read here takes a <see cref="RunKind"/> and filters on
/// it. There is deliberately no "read all kinds" overload: a forward view must be unable to
/// return a replay row by construction, not by the caller remembering to filter.
/// </summary>
public interface ILedgerStore
{
    /// <summary>Register an account and return it with its assigned account_id. Also writes the
    /// opening <see cref="CashEventType.Deposit"/>, so the cash curve reconciles from events
    /// alone rather than from a starting balance nobody recorded.</summary>
    Account OpenAccount(Account account, string asOf);

    Account? GetAccount(long accountId);
    IReadOnlyList<Account> GetAccounts(RunKind runKind);

    IReadOnlyList<Position> GetPositions(long accountId);
    Position? GetPosition(long accountId, SecurityId securityId);

    /// <summary>Insert or update a position. A position at zero shares is DELETED rather than
    /// kept as a zero row: positions is current state (PK account_id+security_id), not a log —
    /// the log is trades. A zero-share row would otherwise show as a holding on the Live screen.</summary>
    void UpsertPosition(Position position);

    /// <summary>Freeze a position and say why (D39/rule 10 — fail closed). Valuation pins at the
    /// last print until an operator resolves it; never silently mispriced.</summary>
    void FreezePosition(long accountId, SecurityId securityId, string reason);

    Trade RecordTrade(Trade trade);
    IReadOnlyList<Trade> GetTrades(long accountId, RunKind runKind);

    CashEvent RecordCashEvent(CashEvent cashEvent);
    IReadOnlyList<CashEvent> GetCashEvents(long accountId, RunKind runKind);

    /// <summary>Log a participation-cap rejection (D43). The rejected quantity is surfaced, never
    /// dropped silently — that is the entire point of the cap.</summary>
    void RecordCapacityRejection(long accountId, SecurityId securityId, string asOf,
        double intendedShares, double allowedShares, double adv21Shares);

    /// <summary>Write the day's equity point. Idempotent per (account, as_of, run_kind) so a
    /// re-run of a recovered day overwrites rather than duplicating (FR-7 idempotency).</summary>
    void RecordEquityPoint(long accountId, string asOf, decimal equity, decimal cash, RunKind runKind);

    IReadOnlyList<(string AsOf, decimal Equity, decimal Cash)> GetEquityCurve(long accountId, RunKind runKind);

    /// <summary>Persist the funnel's stage-1..6 snapshot (the "Why this trade" provenance, and the
    /// carrier for orders decided at close T awaiting their T+1 open fill — there is no orders
    /// table). Idempotent per (account, as_of, run_kind).</summary>
    void RecordDecision(long accountId, string asOf, string stageJson, RunKind runKind);

    string? GetDecisionJson(long accountId, string asOf, RunKind runKind);

    /// <summary>Persist the END-OF-DAY BOOK for this account/session (D90) — the as-of record that
    /// makes a past day's pre-trade state recoverable, because `positions` is current state and
    /// corporate actions rewrite it in place with no reversible log row. Idempotent per
    /// (account, as_of, run_kind): a re-run of a recovered day rewrites that day's rows rather than
    /// duplicating them, and a name that left the book does not survive as a stale row.</summary>
    void RecordPositionSnapshot(long accountId, string asOf, IReadOnlyList<Position> book, RunKind runKind);

    /// <summary>The book at the close of <paramref name="asOf"/> (D90). Empty ⇒ no snapshot for that
    /// session (before the account's inception, or before D90 shipped).</summary>
    IReadOnlyList<Position> GetPositionSnapshot(long accountId, string asOf, RunKind runKind);
}

public sealed class LedgerStore(AlphaLabDbContext db) : ILedgerStore
{
    public Account OpenAccount(Account account, string asOf)
    {
        ArgumentNullException.ThrowIfNull(account);
        ArgumentException.ThrowIfNullOrWhiteSpace(asOf);

        var row = LedgerMapping.ToRow(account);
        row.AccountId = 0;                 // let SQLite assign the rowid
        db.Accounts.Add(row);
        db.SaveChanges();

        var opened = LedgerMapping.ToDomain(row);

        // The opening deposit: equity/cash must reconcile from cash_events + trades alone. Without
        // it, starting_cash would be a number only the accounts row knows about.
        RecordCashEvent(new CashEvent
        {
            AccountId = opened.AccountId,
            AsOf = asOf,
            Type = CashEventType.Deposit,
            Amount = opened.StartingCash,
            RunKind = opened.RunKind,
        });

        return opened;
    }

    public Account? GetAccount(long accountId)
    {
        var row = db.Accounts.FirstOrDefault(a => a.AccountId == accountId);
        return row is null ? null : LedgerMapping.ToDomain(row);
    }

    public IReadOnlyList<Account> GetAccounts(RunKind runKind)
    {
        var token = LedgerMapping.RunKindToken(runKind);
        return db.Accounts.Where(a => a.RunKind == token)
            .OrderBy(a => a.AccountId)
            .AsEnumerable()
            .Select(LedgerMapping.ToDomain)
            .ToList();
    }

    public IReadOnlyList<Position> GetPositions(long accountId) =>
        db.Positions.Where(p => p.AccountId == accountId)
            .OrderBy(p => p.SecurityId)
            .AsEnumerable()
            .Select(LedgerMapping.ToDomain)
            .ToList();

    public Position? GetPosition(long accountId, SecurityId securityId)
    {
        var row = Find(accountId, securityId);
        return row is null ? null : LedgerMapping.ToDomain(row);
    }

    public void UpsertPosition(Position position)
    {
        ArgumentNullException.ThrowIfNull(position);
        var existing = Find(position.AccountId, position.SecurityId);

        // Zero shares means "not held". positions is current state, not a log — a zero row would
        // render as a holding. The trades log keeps the history.
        if (position.Shares == 0)
        {
            if (existing is not null)
            {
                db.Positions.Remove(existing);
                db.SaveChanges();
            }
            return;
        }

        if (existing is null)
        {
            db.Positions.Add(LedgerMapping.ToRow(position));
        }
        else
        {
            existing.Shares = position.Shares;
            existing.CostBasis = position.CostBasis;
            existing.OpenedOn = position.OpenedOn;
            existing.Frozen = position.Frozen;
            existing.FrozenReason = position.FrozenReason;
        }
        db.SaveChanges();
    }

    public void FreezePosition(long accountId, SecurityId securityId, string reason)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        var row = Find(accountId, securityId)
            ?? throw new InvalidOperationException(
                $"Cannot freeze position (account={accountId}, security={securityId}): it does not exist. " +
                "A freeze on a phantom position means the caller's view of the book is wrong.");

        row.Frozen = true;
        row.FrozenReason = reason;
        db.SaveChanges();
    }

    public Trade RecordTrade(Trade trade)
    {
        ArgumentNullException.ThrowIfNull(trade);

        // reason='corp_action' and action_id travel together: a forced close whose cause is not
        // recorded is unauditable, and an action_id on a signal trade is a miscategorized fill.
        if (trade.Reason == TradeReason.CorpAction && trade.ActionId is null)
        {
            throw new InvalidOperationException(
                "A corp_action trade must carry the action_id that forced it (§13.6 — every forced " +
                "event is logged with its action id).");
        }
        if (trade.Reason != TradeReason.CorpAction && trade.ActionId is not null)
        {
            throw new InvalidOperationException(
                $"A '{LedgerMapping.ReasonToken(trade.Reason)}' trade must not carry an action_id — " +
                "only a corporate action forces a fill outside the funnel.");
        }

        var row = LedgerMapping.ToRow(trade);
        row.TradeId = 0;
        db.Trades.Add(row);
        db.SaveChanges();
        return LedgerMapping.ToDomain(row);
    }

    public IReadOnlyList<Trade> GetTrades(long accountId, RunKind runKind)
    {
        var token = LedgerMapping.RunKindToken(runKind);
        return db.Trades.Where(t => t.AccountId == accountId && t.RunKind == token)
            .OrderBy(t => t.TradeId)
            .AsEnumerable()
            .Select(LedgerMapping.ToDomain)
            .ToList();
    }

    public CashEvent RecordCashEvent(CashEvent cashEvent)
    {
        ArgumentNullException.ThrowIfNull(cashEvent);
        var row = LedgerMapping.ToRow(cashEvent);
        row.EventId = 0;
        db.CashEvents.Add(row);
        db.SaveChanges();
        return LedgerMapping.ToDomain(row);
    }

    public IReadOnlyList<CashEvent> GetCashEvents(long accountId, RunKind runKind)
    {
        var token = LedgerMapping.RunKindToken(runKind);
        return db.CashEvents.Where(c => c.AccountId == accountId && c.RunKind == token)
            .OrderBy(c => c.EventId)
            .AsEnumerable()
            .Select(LedgerMapping.ToDomain)
            .ToList();
    }

    public void RecordCapacityRejection(long accountId, SecurityId securityId, string asOf,
        double intendedShares, double allowedShares, double adv21Shares)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(asOf);
        var existing = db.CapacityRejections.FirstOrDefault(
            r => r.AccountId == accountId && r.SecurityId == securityId.Value && r.AsOf == asOf);

        if (existing is null)
        {
            db.CapacityRejections.Add(new CapacityRejectionRow
            {
                AccountId = accountId,
                SecurityId = securityId.Value,
                AsOf = asOf,
                IntendedShares = intendedShares,
                AllowedShares = allowedShares,
                Adv21 = adv21Shares,
            });
        }
        else
        {
            existing.IntendedShares = intendedShares;
            existing.AllowedShares = allowedShares;
            existing.Adv21 = adv21Shares;
        }
        db.SaveChanges();
    }

    public void RecordEquityPoint(long accountId, string asOf, decimal equity, decimal cash, RunKind runKind)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(asOf);
        var token = LedgerMapping.RunKindToken(runKind);
        var existing = db.EquityCurve.FirstOrDefault(
            e => e.AccountId == accountId && e.AsOf == asOf && e.RunKind == token);

        if (existing is null)
        {
            db.EquityCurve.Add(new EquityCurveRow
            {
                AccountId = accountId, AsOf = asOf, Equity = equity, Cash = cash, RunKind = token,
            });
        }
        else
        {
            // Re-running a recovered day must land on the same point, not a second one (FR-7).
            existing.Equity = equity;
            existing.Cash = cash;
        }
        db.SaveChanges();
    }

    public IReadOnlyList<(string AsOf, decimal Equity, decimal Cash)> GetEquityCurve(long accountId, RunKind runKind)
    {
        var token = LedgerMapping.RunKindToken(runKind);
        return db.EquityCurve.Where(e => e.AccountId == accountId && e.RunKind == token)
            .OrderBy(e => e.AsOf)
            .AsEnumerable()
            .Select(e => (e.AsOf, e.Equity, e.Cash))
            .ToList();
    }

    public void RecordDecision(long accountId, string asOf, string stageJson, RunKind runKind)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(asOf);
        ArgumentException.ThrowIfNullOrWhiteSpace(stageJson);
        var token = LedgerMapping.RunKindToken(runKind);
        var existing = db.Decisions.FirstOrDefault(
            d => d.AccountId == accountId && d.AsOf == asOf && d.RunKind == token);

        if (existing is null)
        {
            db.Decisions.Add(new DecisionRow
            {
                AccountId = accountId, AsOf = asOf, StageJson = stageJson, RunKind = token,
            });
        }
        else
        {
            existing.StageJson = stageJson;
        }
        db.SaveChanges();
    }

    public string? GetDecisionJson(long accountId, string asOf, RunKind runKind)
    {
        var token = LedgerMapping.RunKindToken(runKind);
        return db.Decisions
            .FirstOrDefault(d => d.AccountId == accountId && d.AsOf == asOf && d.RunKind == token)
            ?.StageJson;
    }

    public void RecordPositionSnapshot(long accountId, string asOf, IReadOnlyList<Position> book, RunKind runKind)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(asOf);
        ArgumentNullException.ThrowIfNull(book);
        var token = LedgerMapping.RunKindToken(runKind);

        // Re-running a recovered day must land on the SAME book, not a second one (the
        // RecordEquityPoint idempotency contract). A name that left the book between the two runs
        // must not survive as a stale row, so the day's rows are rewritten wholesale rather than
        // upserted name-by-name. This is the one exception to "append-only": it rewrites only rows
        // this very (account, as_of, run_kind) wrote, never another day's book.
        var stale = db.PositionSnapshots
            .Where(p => p.AccountId == accountId && p.AsOf == asOf && p.RunKind == token)
            .ToList();
        if (stale.Count > 0) db.PositionSnapshots.RemoveRange(stale);

        foreach (var p in book.OrderBy(p => p.SecurityId.Value))
        {
            db.PositionSnapshots.Add(new PositionSnapshotRow
            {
                AccountId = accountId,
                AsOf = asOf,
                SecurityId = p.SecurityId.Value,
                Shares = p.Shares,
                CostBasis = p.CostBasis,
                OpenedOn = p.OpenedOn,
                Frozen = p.Frozen,
                FrozenReason = p.FrozenReason,
                RunKind = token,
            });
        }
        db.SaveChanges();
    }

    public IReadOnlyList<Position> GetPositionSnapshot(long accountId, string asOf, RunKind runKind)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(asOf);
        var token = LedgerMapping.RunKindToken(runKind);
        return db.PositionSnapshots
            .Where(p => p.AccountId == accountId && p.AsOf == asOf && p.RunKind == token)
            .OrderBy(p => p.SecurityId)
            .AsEnumerable()
            .Select(p => new Position
            {
                AccountId = p.AccountId,
                SecurityId = new SecurityId(p.SecurityId),
                Shares = p.Shares,
                CostBasis = p.CostBasis,
                OpenedOn = p.OpenedOn,
                Frozen = p.Frozen,
                FrozenReason = p.FrozenReason,
            })
            .ToList();
    }

    private PositionRow? Find(long accountId, SecurityId securityId) =>
        db.Positions.FirstOrDefault(p => p.AccountId == accountId && p.SecurityId == securityId.Value);
}
