using System.Text.Json.Serialization;

namespace AlphaLab.Core.Domain;

/// <summary>
/// Position-sizing mode (CONFIG Sizing.Mode; D32/D42).
///
/// PHASING — FR-11 is PARTIAL in Phase 2 (CHANGELOG finding 169). Only <see cref="Equal"/> is
/// executable; <see cref="InverseVol"/> and <see cref="Kelly"/> are declared (the enum mirrors
/// the CONFIG surface, which documents the designed end state) but the sizer REFUSES them.
///
/// Refusing rather than falling back to equal weight is the point: a silent fallback would size
/// every position by the wrong rule while the config claimed otherwise — a silent mispricing,
/// which hard rule 10 forbids. Fail closed with a reason instead.
///
/// CONFIG_REFERENCE's documented default stays "inverse_vol" (it documents the end state); the
/// Worker's appsettings ships Mode=equal until FR-11 full lands in Phase 6.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<SizingMode>))]
public enum SizingMode
{
    /// <summary>Equal weight across the selected names. The dummies' mode; the only Phase-2 mode.</summary>
    Equal,

    /// <summary>Inverse-volatility under a portfolio vol target, using Ledoit–Wolf covariance
    /// (D42). FR-11 full — Phase 6. Refused by the Phase-2 sizer.</summary>
    InverseVol,

    /// <summary>Fractional Kelly from a per-strategy calibration map. Phase 6+. Refused by the
    /// Phase-2 sizer.</summary>
    Kelly,
}
