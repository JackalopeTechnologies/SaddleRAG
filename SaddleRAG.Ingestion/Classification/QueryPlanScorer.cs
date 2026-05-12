using SaddleRAG.Core.Models;

namespace SaddleRAG.Ingestion.Classification;

/// <summary>
///     Deterministic scorer that turns planner hints into small score
///     adjustments on top of hybrid retrieval results.
/// </summary>
public static class QueryPlanScorer
{
    public static double ComputeAdjustment(DocChunk chunk, SearchQueryPlan plan)
    {
        ArgumentNullException.ThrowIfNull(chunk);
        ArgumentNullException.ThrowIfNull(plan);

        var confidence = Math.Clamp(plan.Confidence, MinConfidence, MaxConfidence);
        var metadataText = BuildMetadataText(chunk);
        var preferredCategoryBoost = plan.PreferredCategory.HasValue && chunk.Category == plan.PreferredCategory.Value
            ? PreferredCategoryBoost * confidence
            : 0.0;
        var typePageBoost = plan.PreferTypePages && IsTypeLikePage(chunk)
            ? TypePageBoost * confidence
            : 0.0;
        var memberPenalty = plan.PenalizeMemberPages && IsMemberLikePage(chunk)
            ? MemberPagePenalty * confidence
            : 0.0;
        var threeDPenalty = plan.Penalize3D && IsThreeDLikePage(chunk)
            ? ThreeDPenalty * confidence
            : 0.0;
        var boostTermCount = CountMatchingTerms(metadataText, plan.BoostTerms, MaxTermMatchesApplied);
        var penalizeTermCount = CountMatchingTerms(metadataText, plan.PenalizeTerms, MaxTermMatchesApplied);
        var boostTermAdjustment = boostTermCount * TermBoost * confidence;
        var penalizeTermAdjustment = penalizeTermCount * TermPenalty * confidence;
        var rawAdjustment = preferredCategoryBoost + typePageBoost + boostTermAdjustment - memberPenalty - threeDPenalty -
                            penalizeTermAdjustment;
        var result = Math.Clamp(rawAdjustment, MinimumAdjustment, MaximumAdjustment);
        return result;
    }

    private static string BuildMetadataText(DocChunk chunk)
    {
        var result = string.Join(SpaceSeparator,
                                 [
                                     chunk.PageTitle,
                                     chunk.SectionPath ?? string.Empty,
                                     chunk.QualifiedName ?? string.Empty
                                 ]);
        return result;
    }

    private static bool IsTypeLikePage(DocChunk chunk)
    {
        var title = chunk.PageTitle;
        var result = smTypePageMarkers.Any(marker => title.Contains(marker, StringComparison.OrdinalIgnoreCase));
        return result;
    }

    private static bool IsMemberLikePage(DocChunk chunk)
    {
        var title = chunk.PageTitle;
        var sectionPath = chunk.SectionPath ?? string.Empty;
        var haystack = string.Join(SpaceSeparator, [title, sectionPath]);
        var result = smMemberPageMarkers.Any(marker => haystack.Contains(marker, StringComparison.OrdinalIgnoreCase));
        return result;
    }

    private static bool IsThreeDLikePage(DocChunk chunk)
    {
        var qualifiedName = chunk.QualifiedName ?? string.Empty;
        var sectionPath = chunk.SectionPath ?? string.Empty;
        var haystack = string.Join(SpaceSeparator, [chunk.PageTitle, sectionPath, qualifiedName]);
        var result = smThreeDMarkers.Any(marker => haystack.Contains(marker, StringComparison.OrdinalIgnoreCase));
        return result;
    }

    private static int CountMatchingTerms(string haystack, IReadOnlyList<string> terms, int maxMatches)
    {
        var result = 0;
        foreach(var term in terms)
        {
            if (result >= maxMatches)
                break;

            if (!string.IsNullOrWhiteSpace(term) && haystack.Contains(term, StringComparison.OrdinalIgnoreCase))
                result++;
        }

        return result;
    }

    private const double PreferredCategoryBoost = 0.06;
    private const double TypePageBoost = 0.12;
    private const double MemberPagePenalty = 0.14;
    private const double ThreeDPenalty = 0.12;
    private const double TermBoost = 0.05;
    private const double TermPenalty = 0.05;
    private const double MinimumAdjustment = -0.2;
    private const double MaximumAdjustment = 0.2;
    private const float MinConfidence = 0f;
    private const float MaxConfidence = 1f;
    private const int MaxTermMatchesApplied = 4;
    private const string SpaceSeparator = " ";
    private const string TypePageMarkerClass = " Class";
    private const string TypePageMarkerInterface = " Interface";
    private const string TypePageMarkerEnumeration = " Enumeration";
    private const string TypePageMarkerEnum = " Enum";
    private const string TypePageMarkerStruct = " Struct";
    private const string MemberPageMarkerMethods = " Methods";
    private const string MemberPageMarkerMethod = " Method";
    private const string MemberPageMarkerProperties = " Properties";
    private const string MemberPageMarkerProperty = " Property";
    private const string MemberPageMarkerMembers = " Members";
    private const string MemberPageMarkerEvents = " Events";
    private const string MemberPageMarkerEvent = " Event";
    private const string MemberPageMarkerFields = " Fields";
    private const string MemberPageMarkerField = " Field";
    private const string ThreeDMarkerShort = "3D";
    private const string ThreeDMarkerNamespace = "Charting3D";

    private static readonly string[] smTypePageMarkers =
        [TypePageMarkerClass, TypePageMarkerInterface, TypePageMarkerEnumeration, TypePageMarkerEnum, TypePageMarkerStruct];

    private static readonly string[] smMemberPageMarkers =
        [
            MemberPageMarkerMethods,
            MemberPageMarkerMethod,
            MemberPageMarkerProperties,
            MemberPageMarkerProperty,
            MemberPageMarkerMembers,
            MemberPageMarkerEvents,
            MemberPageMarkerEvent,
            MemberPageMarkerFields,
            MemberPageMarkerField
        ];

    private static readonly string[] smThreeDMarkers = [ThreeDMarkerShort, ThreeDMarkerNamespace];
}