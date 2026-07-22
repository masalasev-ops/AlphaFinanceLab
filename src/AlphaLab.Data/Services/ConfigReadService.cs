using System.Globalization;

namespace AlphaLab.Data.Services;

/// <summary>
/// The versioned-config read rules (D96, resolving P14a — landed BEFORE the first calibration config
/// write, deliberately). Two resolutions:
///
/// <see cref="ResolveCurrent"/> — MAX(version) per key: what "the current value" has always meant
/// (finding 108). For OPERATIONAL reads outside any run's provenance.
///
/// <see cref="ResolveAsOf"/> — MAX(version) among rows with changed_on ≤ the run's watermark: what a
/// RUN-SCOPED read must use, because a config change appended AFTER a session was committed must be
/// invisible to a re-run of that session (reproduce-day, replay). Without this, the Phase-4 calibration
/// writing the D56 curve rows would silently change what every earlier replay day "read" — the exact
/// gap P14a recorded. Forward behaviour is unchanged: a forward run's watermark is at/after every
/// row it could see, so as-of-resolution ≡ MAX(version) there.
///
/// The comparison is ordinal over ISO-8601 strings (a bare changed_on date sorts before that day's
/// T22:00:00Z watermark — a same-day write is visible to its own day, the intended semantics).
/// </summary>
public sealed class ConfigReadService(AlphaLabDbContext db)
{
    public string? ResolveCurrent(string key)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        return db.Config.Where(c => c.Key == key).AsEnumerable()
            .OrderByDescending(c => c.Version)
            .FirstOrDefault()?.ValueJson;
    }

    public string? ResolveAsOf(string key, string watermark)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentException.ThrowIfNullOrWhiteSpace(watermark);
        return db.Config.Where(c => c.Key == key).AsEnumerable()
            .Where(c => string.CompareOrdinal(c.ChangedOn, watermark) <= 0)
            .OrderByDescending(c => c.Version)
            .FirstOrDefault()?.ValueJson;
    }

    /// <summary>As-of resolution parsed as a long (the security-id-pointer key shape), null when the
    /// key is absent at the watermark or unparseable.</summary>
    public long? ResolveLongAsOf(string key, string watermark)
    {
        var raw = ResolveAsOf(key, watermark);
        return raw is not null && long.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v)
            ? v
            : null;
    }
}
