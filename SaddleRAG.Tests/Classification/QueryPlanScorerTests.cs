using SaddleRAG.Core.Enums;
using SaddleRAG.Core.Models;
using SaddleRAG.Ingestion.Classification;

namespace SaddleRAG.Tests.Classification;

public sealed class QueryPlanScorerTests
{
    [Fact]
    public void RewardsTypeLikeModifierPages()
    {
        var chunk = new DocChunk
                        {
                            Id = "1",
                            LibraryId = "scichart-wpf",
                            Version = "current",
                            PageUrl = "https://example.test/cursor",
                            PageTitle = "CursorModifier Class",
                            Category = DocCategory.ApiReference,
                            Content = "Cursor modifier for charts.",
                            SectionPath = "2D Chart Modifiers",
                            QualifiedName = "SciChart.Charting.ChartModifiers.CursorModifier"
                        };
        var plan = new SearchQueryPlan
                       {
                           ExpandedQuery = "cursor modifiers CursorModifier",
                           PreferredCategory = DocCategory.ApiReference,
                           PreferTypePages = true,
                           PenalizeMemberPages = true,
                           Penalize3D = true,
                           BoostTerms = ["Modifier", "CursorModifier"],
                           PenalizeTerms = ["3D", "Members"],
                           Confidence = 0.9f
                       };

        var result = QueryPlanScorer.ComputeAdjustment(chunk, plan);

        Assert.True(result > 0.1d);
    }

    [Fact]
    public void PenalizesThreeDMemberPages()
    {
        var chunk = new DocChunk
                        {
                            Id = "2",
                            LibraryId = "scichart-wpf",
                            Version = "current",
                            PageUrl = "https://example.test/members",
                            PageTitle = "PinchZoomModifier3D Class Members",
                            Category = DocCategory.ApiReference,
                            Content = "Members list for PinchZoomModifier3D.",
                            SectionPath = "3D Modifier Members",
                            QualifiedName = "SciChart.Charting3D.Modifiers.PinchZoomModifier3D"
                        };
        var plan = new SearchQueryPlan
                       {
                           ExpandedQuery = "cursor modifiers CursorModifier",
                           PreferredCategory = DocCategory.ApiReference,
                           PreferTypePages = true,
                           PenalizeMemberPages = true,
                           Penalize3D = true,
                           BoostTerms = ["Modifier"],
                           PenalizeTerms = ["3D", "Members"],
                           Confidence = 0.9f
                       };

        var result = QueryPlanScorer.ComputeAdjustment(chunk, plan);

        Assert.True(result < 0d);
    }
}