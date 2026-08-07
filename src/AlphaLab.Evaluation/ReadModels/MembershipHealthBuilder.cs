using System.Globalization;
using AlphaLab.Core.ReadModels;
using AlphaLab.Data;

namespace AlphaLab.Evaluation.ReadModels;

/// <summary>
/// Resolves <see cref="MembershipHealthReadModel"/> from `index_membership_log` (6.4).
///
/// Lives in AlphaLab.Evaluation, not in the API, because rule 17 forbids the API from holding threshold
/// or verdict logic: the staleness budget and the three-state resolution are honesty rules, so they
/// belong where the other read-model builders live and are tested framework-agnostically (rule 18 / D58).
/// </summary>
public static class MembershipHealthBuilder
{
    /// <summary>
    /// How old a fetch may be before the feed reads stale, in days.
    ///
    /// The refresh runs once per LAUNCH, and an OnDemand arena may legitimately sit idle over a weekend
    /// or a holiday, so a budget shorter than a long weekend would cry wolf every Monday — finding 310's
    /// lesson is that a guard which cries wolf gets switched off. Four calendar days clears the longest
    /// ordinary market closure while still catching a feed that has genuinely stopped.
    /// </summary>
    public const int StalenessBudgetDays = 4;

    public static MembershipHealthReadModel Build(AlphaLabDbContext db, string todayIso)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentException.ThrowIfNullOrWhiteSpace(todayIso);

        // The latest row by log_id — append-only, so the highest id IS the most recent reconcile.
        var last = db.IndexMembershipLog.OrderByDescending(r => r.LogId).FirstOrDefault();
        if (last is null) return MembershipHealthReadModel.NoReconcileYet;

        var agreed = last.Agreed == 1;

        // A HOLD is reported regardless of freshness: a just-fetched divergence is the loudest case
        // there is, and letting a recent fetch mask it would invert the signal.
        if (!agreed)
        {
            return new MembershipHealthReadModel
            {
                State = MembershipHealthState.StaleOrDiverging,
                FetchedAt = last.ObservedAt,
                LastReconcileAsOf = last.AsOf,
                Source = last.Source,
                LastValidationAgreed = false,
                HeldReason = last.Note,
                Reason = "The last membership reconcile HELD: the two sources did not agree, or a count "
                       + "landed outside the fail-closed band. The stored roster stands unchanged — "
                       + "nothing was applied on unverified data — and it is what the funnel is trading.",
            };
        }

        // Agreed, but with no fetch instant to judge: UNKNOWN, never stale. A pre-M11 row is
        // unprovenanced, not old, and rendering it as stale would send an operator chasing a phantom.
        if (string.IsNullOrWhiteSpace(last.ObservedAt))
        {
            return new MembershipHealthReadModel
            {
                State = MembershipHealthState.Unknown,
                FetchedAt = null,
                LastReconcileAsOf = last.AsOf,
                Source = last.Source,
                LastValidationAgreed = true,
                Reason = $"The last reconcile ({last.AsOf}) agreed, but it recorded no fetch instant, so "
                       + "how current the roster is cannot be judged. Rows written before the provenance "
                       + "columns existed were deliberately NOT backfilled — the only value available was "
                       + "the run date, which is the one number this reading must not be confused with.",
            };
        }

        var age = AgeInDays(last.ObservedAt!, todayIso);
        if (age is null)
        {
            return new MembershipHealthReadModel
            {
                State = MembershipHealthState.Unknown,
                FetchedAt = last.ObservedAt,
                LastReconcileAsOf = last.AsOf,
                Source = last.Source,
                LastValidationAgreed = true,
                Reason = $"The recorded fetch instant ('{last.ObservedAt}') could not be read as a date, "
                       + "so freshness cannot be judged. Reported as unknown rather than guessed.",
            };
        }

        return age > StalenessBudgetDays
            ? new MembershipHealthReadModel
            {
                State = MembershipHealthState.StaleOrDiverging,
                FetchedAt = last.ObservedAt,
                LastReconcileAsOf = last.AsOf,
                Source = last.Source,
                LastValidationAgreed = true,
                Reason = $"The roster last agreed with its cross-check {age} days ago, past the "
                       + $"{StalenessBudgetDays}-day budget. The sources agreed when they were last "
                       + "compared; what is unverified is whether the index has changed since.",
            }
            : new MembershipHealthReadModel
            {
                State = MembershipHealthState.FreshAndAgreeing,
                FetchedAt = last.ObservedAt,
                LastReconcileAsOf = last.AsOf,
                Source = last.Source,
                LastValidationAgreed = true,
                Reason = $"Fetched {age} day(s) ago from {last.Source ?? "an unrecorded source"} and "
                       + "agreeing with its cross-check.",
            };
    }

    /// <summary>Whole days between the fetch instant's DATE and <paramref name="todayIso"/>. Null when
    /// either cannot be parsed — reported as unknown rather than defaulted, because a defaulted age
    /// would silently become a freshness verdict.</summary>
    private static int? AgeInDays(string observedAt, string todayIso)
    {
        var datePart = observedAt.Length >= 10 ? observedAt[..10] : observedAt;
        if (!DateOnly.TryParseExact(datePart, "yyyy-MM-dd", CultureInfo.InvariantCulture,
                DateTimeStyles.None, out var fetched)) return null;
        if (!DateOnly.TryParseExact(todayIso, "yyyy-MM-dd", CultureInfo.InvariantCulture,
                DateTimeStyles.None, out var today)) return null;
        return today.DayNumber - fetched.DayNumber;
    }
}
