namespace SaddleRAG.Core.Enums;

/// <summary>
///     Selects which query-planning strategy search_docs uses before
///     embedding and hybrid retrieval. Default is Off so current callers
///     keep the existing behavior until the planner is explicitly enabled.
/// </summary>
public enum QueryPlannerStrategy
{
    /// <summary>
    ///     No planning. The caller's query and optional category filter are
    ///     passed straight through to retrieval.
    /// </summary>
    Off,

    /// <summary>
    ///     One local LLM call produces structured search hints such as a
    ///     conservative query rewrite, preferred category, and page-shape
    ///     penalties/boosts used during hybrid scoring.
    /// </summary>
    Llm
}