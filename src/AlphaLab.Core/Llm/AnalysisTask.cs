namespace AlphaLab.Core.Llm;

/// <summary>
/// The canonical name for a unit of LLM work (FR-21/FR-22, D46). One vocabulary, resolved at checkpoint
/// 5.1 as finding 317 required.
///
/// **WHAT FINDING 317 FOUND.** Three vocabularies named one concept: <c>analysis_cache.task</c>
/// (<c>regime_brief|brief|skeptic|hypotheses</c>), the <c>Llm.Tasks</c> config keys
/// (<c>news_extraction|regime_brief|research_brief|skeptic</c>), and <c>jobs.kind</c>
/// (<c>analysis_brief|analysis_skeptic|analysis_hypotheses</c>). They overlapped without matching:
/// <c>brief</c> / <c>research_brief</c> / <c>analysis_brief</c> were one job under three names,
/// <c>news_extraction</c> existed only in config, <c>hypotheses</c> only outside it.
///
/// **HOW IT IS RESOLVED, AND WHY IT COSTS NO MIGRATION.** The finding anticipated a rename touching a
/// CHECK constraint, a config key and a cached-row key at once. It does not, because <c>analysis_cache</c>
/// **does not exist yet** — M7 creates it in this same checkpoint, so its vocabulary is chosen at
/// CREATE TABLE rather than migrated. So: <c>analysis_cache.task</c> adopts the <c>Llm.Tasks</c> config
/// vocabulary verbatim (this enum's <see cref="Wire"/> strings), because that is the vocabulary the code
/// must already read per-task models from — aligning the other way would have meant a config-key rename,
/// which is not free.
///
/// **`jobs.kind` IS DELIBERATELY LEFT ALONE, and that is not a compromise.** A job is not a task: it is a
/// queued unit of Worker work with a status and a lifetime, and one job may run several model calls.
/// `analysis_brief` names *the job that produces a brief*; `research_brief` names *the model call that
/// produces it*. Two objects, two names, correctly. Collapsing them would have been the wrong repair —
/// and it would have cost the migration the finding feared, on an enum CHECK that already ships
/// (finding 121's rule). Three vocabularies become two, and the two that remain describe different things.
/// </summary>
public enum AnalysisTask
{
    /// <summary>Structured extraction over the day's admitted news (D46). The cheap tier.</summary>
    NewsExtraction,

    /// <summary>The daily market-level regime brief (D46; §7). Forward-only, batched.</summary>
    RegimeBrief,

    /// <summary>An on-demand bull/bear research brief on a surfaced name (FR-23).</summary>
    ResearchBrief,

    /// <summary>The skeptic: "what leakage or overfitting story explains this?" (FR-23).</summary>
    Skeptic,

    /// <summary>The researcher seat's hypothesis proposal (D82, FR-23; built at checkpoint 5.6).</summary>
    Hypotheses,
}

/// <summary>Wire/storage names for <see cref="AnalysisTask"/> — the single mapping point.</summary>
public static class AnalysisTaskNames
{
    public const string NewsExtraction = "news_extraction";
    public const string RegimeBrief = "regime_brief";
    public const string ResearchBrief = "research_brief";
    public const string Skeptic = "skeptic";
    public const string Hypotheses = "hypotheses";

    /// <summary>The value stored in <c>analysis_cache.task</c> and used as the <c>Llm.Tasks</c> key.
    /// These are the SAME string by construction — that is the point of the type.</summary>
    public static string Wire(this AnalysisTask task) => task switch
    {
        AnalysisTask.NewsExtraction => NewsExtraction,
        AnalysisTask.RegimeBrief => RegimeBrief,
        AnalysisTask.ResearchBrief => ResearchBrief,
        AnalysisTask.Skeptic => Skeptic,
        AnalysisTask.Hypotheses => Hypotheses,
        _ => throw new ArgumentOutOfRangeException(nameof(task), task, "Unmapped AnalysisTask."),
    };

    /// <summary>Every wire name, in enum order — the source the M7 CHECK constraint is written from, so
    /// the constraint and the type cannot drift (finding 319).</summary>
    public static readonly IReadOnlyList<string> All =
    [
        NewsExtraction, RegimeBrief, ResearchBrief, Skeptic, Hypotheses,
    ];

    /// <summary>Parse a stored/configured name. Unknown ⇒ null; callers fail closed rather than
    /// defaulting to a task (rule 10 — nothing is ever silently defaulted).</summary>
    public static AnalysisTask? Parse(string? wire) => wire switch
    {
        NewsExtraction => AnalysisTask.NewsExtraction,
        RegimeBrief => AnalysisTask.RegimeBrief,
        ResearchBrief => AnalysisTask.ResearchBrief,
        Skeptic => AnalysisTask.Skeptic,
        Hypotheses => AnalysisTask.Hypotheses,
        _ => null,
    };
}
