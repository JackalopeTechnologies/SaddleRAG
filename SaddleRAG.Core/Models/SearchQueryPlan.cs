using SaddleRAG.Core.Enums;

namespace SaddleRAG.Core.Models;

/// <summary>
///     Structured query-planning output consumed by search_docs to bias
///     retrieval toward the intended page shapes without forcing a full
///     LLM rerank over every candidate.
/// </summary>
public sealed record SearchQueryPlan
{
    /// <summary>
    ///     Query text to embed and score with BM25 after planning.
    /// </summary>
    public required string ExpandedQuery { get; init; }

    /// <summary>
    ///     Optional preferred document category when the planner is highly
    ///     confident that the user's query targets a specific corpus slice.
    /// </summary>
    public DocCategory? PreferredCategory { get; init; }

    /// <summary>
    ///     Prefer class/interface/enum/type pages over member tables.
    /// </summary>
    public bool PreferTypePages { get; init; }

    /// <summary>
    ///     Penalize method/property/member list pages when the query is a
    ///     broad concept or type lookup.
    /// </summary>
    public bool PenalizeMemberPages { get; init; }

    /// <summary>
    ///     Penalize 3D pages unless the query explicitly asks for them.
    /// </summary>
    public bool Penalize3D { get; init; }

    /// <summary>
    ///     Short metadata terms that should lift matching titles or
    ///     qualified names.
    /// </summary>
    public IReadOnlyList<string> BoostTerms { get; init; } = [];

    /// <summary>
    ///     Short metadata terms that should demote matching titles or
    ///     qualified names.
    /// </summary>
    public IReadOnlyList<string> PenalizeTerms { get; init; } = [];

    /// <summary>
    ///     Self-reported planner confidence in [0, 1]. Used to scale how
    ///     aggressively the deterministic scorer trusts the hints.
    /// </summary>
    public float Confidence { get; init; }

    public static SearchQueryPlan Disabled(string query)
    {
        ArgumentException.ThrowIfNullOrEmpty(query);

        var result = new SearchQueryPlan
                         {
                             ExpandedQuery = query,
                             Confidence = 0f
                         };
        return result;
    }
}