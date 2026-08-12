using System.Globalization;
using System.Text.Json;
using AlphaLab.Core.Domain;
using AlphaLab.Core.Json;
using AlphaLab.Core.Ledger;
using AlphaLab.Data;
using AlphaLab.Data.Entities;
using AlphaLab.Data.Services;

namespace AlphaLab.Strategies;

/// <summary>
/// Seeds the Phase-2 dummy roster — the three baseline/dummy strategies (STRATEGY_CATALOG §5.1/§5) and
/// their isolated paper-trading accounts (D30/D59). This is the first thing to WRITE the <c>strategies</c>
/// table; the pure models decide behaviour, this records their identity + a book to trade in.
///
/// IDEMPOTENT (FR-7 in spirit): a re-run seeds nothing new. A strategy already registered is left
/// untouched (its config is FROZEN, D17 — re-serializing over it would be a silent tune), and an
/// account already opened for it is reused rather than duplicated.
///
/// STARTING CASH IS A VERSIONED CONFIG ROW (finding K), the <c>Regime.ProxySecurityId</c> precedent:
/// the authoritative runtime value is <c>MAX(version)</c> of <c>Accounts.StartingCash</c>, not
/// appsettings. On a fresh store this writes version 1 = the CONFIG default ($100,000) so the value the
/// accounts opened at is recorded and auditable, never a literal only this code knew.
/// </summary>
public sealed class DummyRoster(AlphaLabDbContext db, ILedgerStore ledger)
{
    /// <summary>The append-only versioned config key for the accounts' opening capital.</summary>
    public const string StartingCashConfigKey = "Accounts.StartingCash";

    /// <summary>CONFIG_REFERENCE "Accounts.StartingCash" default — $100,000 (decimal, D69).</summary>
    public const decimal DefaultStartingCash = 100_000m;

    /// <summary>
    /// Register the three dummies and open an account for each under <paramref name="runKind"/>, at the
    /// resolved starting cash. Strategy rows are SHARED across run kinds (identity is the strategy);
    /// accounts are per kind (D37 — a replay trades its own isolated books). Returns the accounts
    /// (existing or newly opened), in seed order. Idempotent.
    /// </summary>
    public IReadOnlyList<Account> Seed(
        string asOf, string watermark, decimal? startingCashOverride = null, RunKind runKind = RunKind.Live)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(asOf);
        ArgumentException.ThrowIfNullOrWhiteSpace(watermark);

        var startingCash = ResolveStartingCash(asOf, watermark, startingCashOverride ?? DefaultStartingCash);

        // (model, family, status). Buy&Hold are permanent baselines (D26/D27); the trend dummy is a
        // candidate honestly flagged unregistered in its own config (rule 16).
        var seeds = new (IModel Model, string Family, string Status)[]
        {
            (BuyAndHoldModel.CapWeight(),  "passive", "baseline"),
            (BuyAndHoldModel.EqualWeight(), "passive", "baseline"),
            (ThresholdModel.Create(),       "passive", "candidate"),
        };

        var accounts = new List<Account>(seeds.Length);
        foreach (var (model, family, status) in seeds)
        {
            RegisterStrategy(model, family, status, asOf);
            accounts.Add(OpenAccountIfAbsent(model.Id, startingCash, asOf, runKind));
        }
        return accounts;
    }

    /// <summary>The starting cash for new accounts, resolved AS-OF the run's watermark (D141), writing
    /// version 1 = <paramref name="defaultCash"/> on a fresh store (append-only; a re-resolve writes
    /// nothing).
    ///
    /// **WHY AS-OF (D141).** This was a hand-written MAX(version) read straight against <c>db.Config</c> —
    /// `ResolveCurrent`'s body, inlined, which is why the P24 enumeration (a grep for that method name)
    /// could not see it. The value is not provenance or a guard: it is the accounts' OPENING CAPITAL, a
    /// SIMULATION INPUT upstream of every equity curve, every population comparison and every S6 band. It
    /// is normally masked because the accounts already exist, and `--reset` — which D139's confirmation-slice
    /// procedure mandates — deletes the replay accounts (`ReplayRunner.DeleteReplayGeneration`) and re-opens
    /// them through this read, so a version appended since the generation would silently re-simulate it at
    /// different capital. Measured before the change: the live `sp500` store holds exactly ONE version
    /// (v1, 2006-01-03, 100000), so as-of and latest-wins agree today and this binding is behaviourally
    /// free — it removes a future divergence, it does not correct a present one.</summary>
    public decimal ResolveStartingCash(string asOf, string watermark, decimal defaultCash)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(watermark);

        var raw = new ConfigReadService(db).ResolveAsOf(StartingCashConfigKey, watermark);
        if (raw is not null &&
            decimal.TryParse(raw, NumberStyles.Number, CultureInfo.InvariantCulture, out var existing))
        {
            return existing;
        }

        // FAIL CLOSED (rule 10) rather than write a second version 1. Reaching here with rows already in
        // the table means either the key is unreadable at this watermark (the accounts are being opened
        // before their capital was ever configured) or its value does not parse. The old code's next
        // statement was an unconditional INSERT of version 1, which under either condition collides with
        // the existing row's (key, version) primary key — a confusing failure in place of a stated one.
        if (db.Config.Any(c => c.Key == StartingCashConfigKey))
        {
            throw new InvalidOperationException(
                $"{StartingCashConfigKey} exists but does not resolve to a decimal as-of watermark {watermark} " +
                $"(asOf {asOf}). Opening accounts would have to invent their capital, so this refuses instead: " +
                "either the run's watermark precedes the row that configures it, or the stored value is " +
                "unparseable. Neither is a condition a starting balance may be defaulted through (D141, rule 10).");
        }

        db.Config.Add(new ConfigRow
        {
            Key = StartingCashConfigKey,
            ValueJson = defaultCash.ToString(CultureInfo.InvariantCulture),
            Version = 1,
            ChangedOn = asOf,
            Reason = "Phase-2 dummy roster: opening capital for the baseline + dummy accounts (finding K).",
        });
        db.SaveChanges();
        return defaultCash;
    }

    private void RegisterStrategy(IModel model, string family, string status, string asOf)
    {
        if (db.Strategies.Any(s => s.StrategyId == model.Id)) return; // frozen (D17) — never re-serialize over it

        db.Strategies.Add(new StrategyRow
        {
            StrategyId = model.Id,
            Family = family,
            // D152: through the D133 canonicalizer, not a raw typed serialize. Without it the stored bytes
            // carried insertion order and omitted frozen/frozen_sets/horizon, so Write(Read(stored)) did
            // not equal stored for ANY of the three rows that actually trade — the one property D133
            // exists to provide. FRESH ARENAS ONLY: :119 above returns early for an existing id (D17), so
            // no row already written is touched or rewritten.
            ConfigJson = StrategyConfigJson.Write(model.Config),
            // Serialize the DECLARED type (ExitPolicy) so the [JsonPolymorphic] "kind" discriminator
            // is written — exit_policy_json must round-trip the shape, not just its fields.
            ExitPolicyJson = JsonSerializer.Serialize<ExitPolicy>(model.Exits, AlphaLabJson.Options),
            HoldingHorizonDays = model.Horizon.Days_, // null for the two horizon shapes with no day count
            CreatedOn = asOf,
            Status = status,
        });
        db.SaveChanges();
    }

    private Account OpenAccountIfAbsent(string strategyId, decimal startingCash, string asOf, RunKind runKind)
    {
        var existing = ledger.GetAccounts(runKind).FirstOrDefault(a => a.StrategyId == strategyId);
        return existing ?? ledger.OpenAccount(
            new Account { StrategyId = strategyId, StartingCash = startingCash, RunKind = runKind },
            asOf);
    }
}
