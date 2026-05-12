using SaddleRAG.Core.Enums;
using SaddleRAG.Ingestion.Classification;

namespace SaddleRAG.Tests.Classification;

public sealed class LlmQueryPlannerTests
{
    [Fact]
    public void ParsePlanResponseReadsStructuredFields()
    {
        const string response =
            """{"expandedQuery":"cursor modifiers CursorModifier RolloverModifier","preferredCategory":"ApiReference","preferTypePages":true,"penalizeMemberPages":true,"penalize3D":true,"boostTerms":["Modifier","CursorModifier"],"penalizeTerms":["3D","Members"],"confidence":0.82} """;

        var result = LlmQueryPlanner.ParsePlanResponse(response, "cursor modifiers");

        Assert.Equal("cursor modifiers CursorModifier RolloverModifier", result.ExpandedQuery);
        Assert.Equal(DocCategory.ApiReference, result.PreferredCategory);
        Assert.True(result.PreferTypePages);
        Assert.True(result.PenalizeMemberPages);
        Assert.True(result.Penalize3D);
        Assert.Equal(2, result.BoostTerms.Count);
        Assert.Equal(3, result.PenalizeTerms.Count);
        Assert.Contains("SetMouseCursor", result.PenalizeTerms);
        Assert.Equal(0.82f, result.Confidence, precision: 2);
    }

    [Fact]
    public void ParsePlanResponseFallsBackToDisabledPlanOnInvalidJson()
    {
        var result = LlmQueryPlanner.ParsePlanResponse("not json", "cursor modifiers");

        Assert.Equal("cursor modifiers", result.ExpandedQuery);
        Assert.Equal(0f, result.Confidence);
        Assert.Null(result.PreferredCategory);
    }

    [Fact]
    public void ParsePlanResponseRefinesModifierQueriesTowardTypePages()
    {
        const string response =
            """{"expandedQuery":"cursor modifications WPF Cursor Modifiers","preferredCategory":null,"preferTypePages":false,"penalizeMemberPages":true,"penalize3D":false,"boostTerms":["WPF","Cursor Modifiers"],"penalizeTerms":[],"confidence":0.75} """;

        var result = LlmQueryPlanner.ParsePlanResponse(response, "cursor modifiers");

        Assert.True(result.PreferTypePages);
        Assert.True(result.PenalizeMemberPages);
        Assert.True(result.Penalize3D);
        Assert.Contains("Modifier", result.BoostTerms);
        Assert.Contains("CursorModifier", result.BoostTerms);
        Assert.Contains("SetMouseCursor", result.PenalizeTerms);
    }
}