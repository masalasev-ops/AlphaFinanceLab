using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using AlphaLab.Core.Regime;
using AlphaLab.Data.Entities;

namespace AlphaLab.Data.Services;

/// <summary>The outcome of a regime-label computation. When <see cref="Computed"/> is false NOTHING was
/// written — the reason is logged (fail closed, rule 10). Pure data.</summary>
public sealed record RegimeLabelResult(bool Computed, RegimeLabelPoint? Label, string? InputsHash, string? Reason);

/// <summary>
/// The Data adapter for the D50/FR-26 regime label (§20.1). It supplies the pure
/// <see cref="RegimeLabeler"/> with the proxy's watermarked series and persists the result:
///
///  1. resolve <c>Regime.ProxySecurityId</c> from the VERSIONED config row (MAX(version)) — the DB row
///     is the authoritative runtime value (FR-38 wrote it), NOT appsettings;
///  2. fail closed via <see cref="IRegimeProxyReadiness"/> below the ≈3.8-year warm-up (never fabricate
///     a label on a short series — that would silently mis-calibrate the D56 curves);
///  3. read the proxy's adj_close series ≤ asOf at the run's watermark via <see cref="IBarReadService"/>
///     — leak-freedom is inherited from the versioned bar read, so F-LEAK is a test of THIS seam;
///  4. compute the label at asOf (the labeler recomputes the whole trajectory — it never reads a prior
///     persisted label, which would import a different watermark's state);
///  5. stamp inputs_hash = hash(proxy id, parameter set, watermark) and upsert regime_labels;
///  6. maintain regime_episodes — the maximal-trend-run chain (D45), extended forward one session at a
///     time so a confirmed flip closes the current episode and opens the next.
///
/// The label carries NO run_id, but since D93/M5 it DOES carry run_kind in its key: the regime is a
/// market-level fact, yet a replay recomputes it from a different watermark over its own window, and
/// with as_of alone the recompute would overwrite the forward label (P6). Each run kind maintains its
/// own label rows AND its own episode chain, quarantined. Wiring into the D53 staged pipeline (inside
/// the Stage-2 transaction, after membership) is checkpoint 2.10.
/// </summary>
public interface IRegimeLabelService
{
    /// <summary>Compute and persist the regime label for <paramref name="asOf"/> at
    /// <paramref name="watermark"/> under <paramref name="runKind"/> ('live' | 'replay', D93). Fails
    /// closed (writes nothing, returns a reason) when the proxy is unresolved, below warm-up, or has
    /// no bar on asOf.</summary>
    RegimeLabelResult ComputeAndSave(string asOf, string watermark, string runKind = "live");
}

public sealed class RegimeLabelService(
    AlphaLabDbContext db,
    IBarReadService bars,
    IRegimeProxyReadiness readiness,
    RegimeOptions options) : IRegimeLabelService
{
    /// <summary>Sessions/year used to size the vol lookback in sessions. MUST match
    /// <see cref="RegimeProxyReadiness"/>'s constant so the labeler's warm-up never exceeds what
    /// readiness certifies (readiness requires TrendSmaDays + VolLookbackYears×252 sessions; the labeler
    /// needs VolWindowDays + VolLookbackYears×252 − 1, which is strictly fewer — always covered).</summary>
    private const int TradingDaysPerYear = 252;

    // Any real ISO date is ≥ this, so GetSeries(from: Epoch, to: asOf) returns the full history ≤ asOf.
    private const string Epoch = "0001-01-01";

    public RegimeLabelResult ComputeAndSave(string asOf, string watermark, string runKind = "live")
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(asOf);
        ArgumentException.ThrowIfNullOrWhiteSpace(watermark);
        ArgumentException.ThrowIfNullOrWhiteSpace(runKind);

        // 1) Proxy id from the versioned config row, AS-OF the run's watermark (D96) — never appsettings.
        var proxyId = ResolveProxySecurityId(watermark);
        if (proxyId is null)
        {
            return NotComputed(
                $"Regime.ProxySecurityId is not resolved (no '{RegimeProxyIngestion.ProxyConfigKey}' config row) — " +
                "run the FR-38 proxy ingestion before labeling (fail closed).");
        }

        // 2) Warm-up guard (fail closed below ≈3.8y of proxy history).
        var ready = readiness.CheckReadiness(proxyId.Value, asOf);
        if (!ready.IsReady) return NotComputed(ready.Reason);

        // 3) Proxy adj_close series ≤ asOf at the watermark (versioned read ⇒ leak-proof by construction).
        var rows = bars.GetSeries(proxyId.Value, Epoch, asOf, watermark);
        if (rows.Count == 0 || string.CompareOrdinal(rows[^1].Date, asOf) != 0)
        {
            return NotComputed(
                $"regime proxy has no bar on {asOf} at watermark {watermark} — cannot label asOf on a stale " +
                "prior session (fail closed).");
        }

        var series = new List<ProxyClose>(rows.Count);
        foreach (var b in rows)
        {
            if (b.AdjClose is not { } adj)
            {
                return NotComputed(
                    $"regime proxy bar {b.Date} has a NULL adj_close at watermark {watermark} — cannot compute " +
                    "the total-return series (fail closed).");
            }
            series.Add(new ProxyClose(b.Date, adj));
        }

        // 4) The label at asOf — the last entry of the full trajectory (path-dependent via hysteresis).
        var prms = BuildParams();
        var trajectory = RegimeLabeler.LabelSeries(series, prms);
        if (trajectory.Count == 0 || string.CompareOrdinal(trajectory[^1].Date, asOf) != 0)
        {
            // Should not happen once readiness certifies the warm-up; defensive (rule 10).
            return NotComputed(
                $"regime labeler produced no label for {asOf} despite readiness — warm-up/data inconsistency (fail closed).");
        }
        var label = trajectory[^1];

        // 5) Provenance + persist the label row.
        var inputsHash = InputsHash(proxyId.Value, prms, watermark);
        UpsertLabel(label, inputsHash, runKind);

        // 6) Episode chain (maximal trend runs, forward-only) — per run kind (D93).
        var priorSessionDate = trajectory.Count >= 2 ? trajectory[^2].Date : null;
        MaintainEpisode(asOf, label.TrendToken, priorSessionDate, runKind);

        db.SaveChanges();
        return new RegimeLabelResult(true, label, inputsHash, null);
    }

    // ---- proxy id from the append-only versioned config row, as-of the watermark (D96) ----
    private long? ResolveProxySecurityId(string watermark) =>
        new ConfigReadService(db).ResolveLongAsOf(RegimeProxyIngestion.ProxyConfigKey, watermark);

    private RegimeLabelParams BuildParams() => new(
        trendSmaDays: options.TrendSmaDays,
        trendHysteresisPct: options.TrendHysteresisPct,
        confirmDays: options.TrendConfirmDays,
        volWindowDays: options.VolWindowDays,
        volPercentile: options.VolPercentile,
        volLookbackSessions: options.VolLookbackYears * TradingDaysPerYear);

    private void UpsertLabel(RegimeLabelPoint label, string inputsHash, string runKind)
    {
        // regime_labels is a DERIVED table (PK (as_of, run_kind), D93), not append-only bars — a
        // recompute at the same watermark reproduces the row exactly, and a legitimate re-label
        // replaces it WITHIN its own run kind (a replay can never touch the forward row — the key
        // forbids it). rule 3's never-UPDATE applies to bars/corporate_actions, not here.
        var existing = db.RegimeLabels.FirstOrDefault(x => x.AsOf == label.Date && x.RunKind == runKind);
        if (existing is null)
        {
            db.RegimeLabels.Add(new RegimeLabelRow
            {
                AsOf = label.Date,
                Trend = label.TrendToken,
                Vol = label.VolToken,
                Label = label.Label,
                InputsHash = inputsHash,
                RunKind = runKind
            });
        }
        else
        {
            existing.Trend = label.TrendToken;
            existing.Vol = label.VolToken;
            existing.Label = label.Label;
            existing.InputsHash = inputsHash;
        }
    }

    // Maintain the maximal-trend-run chain (D45), PER RUN KIND (D93): a replay's chain over its
    // historical window never touches the forward chain. Forward-only within a kind: asOf is at or
    // after every recorded episode's start. Same trend as the current episode ⇒ it extends (no write);
    // a genuine flip closes the current episode at the prior session and opens a new one. Idempotent
    // on a same-asOf re-run (an extend is a no-op, and a re-open is blocked because the current
    // episode already covers asOf).
    private void MaintainEpisode(string asOf, string trendToken, string? priorSessionDate, string runKind)
    {
        var latest = db.RegimeEpisodes
            .Where(x => x.RunKind == runKind)
            .AsEnumerable()
            .OrderByDescending(x => x.StartDate, StringComparer.Ordinal)
            .FirstOrDefault();

        if (latest is null)
        {
            db.RegimeEpisodes.Add(new RegimeEpisodeRow { Label = trendToken, StartDate = asOf, EndDate = null, RunKind = runKind });
            return;
        }

        if (latest.Label == trendToken) return;                          // same trend ⇒ extend (no write)
        if (string.CompareOrdinal(asOf, latest.StartDate) <= 0) return;  // not strictly after ⇒ nothing to flip

        // Genuine confirmed flip: close the current episode at the last session of the old trend, open the new.
        latest.EndDate = priorSessionDate ?? latest.StartDate;
        db.RegimeEpisodes.Add(new RegimeEpisodeRow { Label = trendToken, StartDate = asOf, EndDate = null, RunKind = runKind });
    }

    // hash(proxy security_id, parameter set, watermark) — §20.1 provenance. SHA-256 over a canonical
    // string; the watermark is IN the hash, so the same asOf at a different watermark is a distinct
    // provenance even when the label token is unchanged.
    private static string InputsHash(long proxyId, RegimeLabelParams p, string watermark)
    {
        var canonical = string.Create(CultureInfo.InvariantCulture,
            $"proxy={proxyId}|sma={p.TrendSmaDays}|hyst={p.TrendHysteresisPct}|confirm={p.ConfirmDays}|" +
            $"volwin={p.VolWindowDays}|volpct={p.VolPercentile}|vollookback={p.VolLookbackSessions}|wm={watermark}");
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(canonical));
        return Convert.ToHexStringLower(bytes);
    }

    private static RegimeLabelResult NotComputed(string? reason) => new(false, null, null, reason);
}
