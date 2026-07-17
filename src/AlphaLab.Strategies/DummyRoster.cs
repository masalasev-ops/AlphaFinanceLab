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
    /// Register the three dummies and open a live account for each, at the resolved starting cash.
    /// Returns the accounts (existing or newly opened), in seed order. Idempotent.
    /// </summary>
    public IReadOnlyList<Account> Seed(string asOf, decimal? startingCashOverride = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(asOf);

        var startingCash = ResolveStartingCash(asOf, startingCashOverride ?? DefaultStartingCash);

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
            accounts.Add(OpenAccountIfAbsent(model.Id, startingCash, asOf));
        }
        return accounts;
    }

    /// <summary>The starting cash for new accounts: MAX(version) of the config row, writing version 1 =
    /// <paramref name="defaultCash"/> on a fresh store (append-only; a re-resolve writes nothing).</summary>
    public decimal ResolveStartingCash(string asOf, decimal defaultCash)
    {
        var current = db.Config
            .Where(c => c.Key == StartingCashConfigKey)
            .AsEnumerable()
            .OrderByDescending(c => c.Version)
            .FirstOrDefault();

        if (current is not null &&
            decimal.TryParse(current.ValueJson, NumberStyles.Number, CultureInfo.InvariantCulture, out var existing))
        {
            return existing;
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
            ConfigJson = JsonSerializer.Serialize(model.Config, AlphaLabJson.Options),
            // Serialize the DECLARED type (ExitPolicy) so the [JsonPolymorphic] "kind" discriminator
            // is written — exit_policy_json must round-trip the shape, not just its fields.
            ExitPolicyJson = JsonSerializer.Serialize<ExitPolicy>(model.Exits, AlphaLabJson.Options),
            HoldingHorizonDays = model.Horizon.Days_, // null for the two horizon shapes with no day count
            CreatedOn = asOf,
            Status = status,
        });
        db.SaveChanges();
    }

    private Account OpenAccountIfAbsent(string strategyId, decimal startingCash, string asOf)
    {
        var existing = ledger.GetAccounts(RunKind.Live).FirstOrDefault(a => a.StrategyId == strategyId);
        return existing ?? ledger.OpenAccount(
            new Account { StrategyId = strategyId, StartingCash = startingCash, RunKind = RunKind.Live },
            asOf);
    }
}
