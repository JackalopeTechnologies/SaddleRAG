using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OllamaSharp;
using OllamaSharp.Models;
using SaddleRAG.Core.Enums;
using SaddleRAG.Core.Models;
using SaddleRAG.Ingestion.Embedding;

namespace SaddleRAG.Ingestion.Classification;

/// <summary>
///     One-shot local query planner. Produces compact structured hints used
///     to bias retrieval before any optional reranking takes place.
/// </summary>
public class LlmQueryPlanner
{
    public LlmQueryPlanner(IOptions<OllamaSettings> settings,
                           ILogger<LlmQueryPlanner> logger)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(logger);

        mSettings = settings.Value;
        mLogger = logger;
        mClient = new OllamaApiClient(new Uri(mSettings.Endpoint));
    }

    private readonly OllamaApiClient mClient;
    private readonly ILogger<LlmQueryPlanner> mLogger;
    private readonly OllamaSettings mSettings;

    public async Task<SearchQueryPlan> PlanAsync(string query,
                                                 string? library,
                                                 DocCategory? requestedCategory,
                                                 CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(query);

        var prompt = BuildPrompt(query, library, requestedCategory);
        var result = SearchQueryPlan.Disabled(query);

        try
        {
            var request = new GenerateRequest
                              {
                                  Model = mSettings.ClassificationModel,
                                  Prompt = prompt,
                                  Stream = true,
                                  Options = new RequestOptions { Temperature = 0f }
                              };

            var responseBuilder = new StringBuilder();
            await foreach(var token in mClient.GenerateAsync(request, ct))
            {
                if (responseBuilder.Length < MaxResponseChars)
                    responseBuilder.Append(token?.Response ?? string.Empty);
            }

            result = ParsePlanResponse(responseBuilder.ToString().Trim(), query);
            mLogger.LogDebug("Query planner produced confidence {Confidence:F2} for '{Query}'",
                             result.Confidence,
                             query
                            );
        }
        catch(Exception ex) when(ex is not OperationCanceledException)
        {
            mLogger.LogWarning(ex, "Local query planning failed for '{Query}'", query);
        }

        return result;
    }

    public static string BuildPrompt(string query, string? library, DocCategory? requestedCategory)
    {
        ArgumentException.ThrowIfNullOrEmpty(query);

        var libraryValue = string.IsNullOrWhiteSpace(library) ? NullLiteral : library;
        var categoryValue = requestedCategory?.ToString() ?? NullLiteral;
        var jsonExample =
            """{"expandedQuery":"...","preferredCategory":"ApiReference|null","preferTypePages":true,"penalizeMemberPages":true,"penalize3D":false,"boostTerms":["..."],"penalizeTerms":["..."],"confidence":0.0} """;

        var prompt = $$"""
                       You are a documentation search planner. Convert the user's natural-language query into conservative search hints. Respond with ONLY a JSON object:
                       {{jsonExample}}

                       Rules:
                       - expandedQuery must preserve the user's intent and may add a few short disambiguating terms only when helpful.
                       - preferredCategory must be one of Overview, HowTo, Sample, ApiReference, ChangeLog, Unclassified, or null.
                       - preferTypePages=true for class/interface/enum/type lookups.
                       - penalizeMemberPages=true when method/property/member-list pages are likely noise.
                       - penalize3D=true when the query does not ask for 3D and 3D pages are likely to distract retrieval.
                       - boostTerms and penalizeTerms must be short title or qualified-name cues, not sentences.
                       - confidence should reflect how safe it is to apply these hints.

                       Example:
                       Query: cursor modifiers
                       Library: scichart-wpf
                       RequestedCategory: null
                       Response:
                       {"expandedQuery":"cursor modifiers CursorModifier RolloverModifier ChartModifier","preferredCategory":"ApiReference","preferTypePages":true,"penalizeMemberPages":true,"penalize3D":true,"boostTerms":["Modifier","CursorModifier","RolloverModifier"],"penalizeTerms":["3D","Members","Properties","Methods","SetMouseCursor"],"confidence":0.88}

                       Query: {{query}}
                       Library: {{libraryValue}}
                       RequestedCategory: {{categoryValue}}
                       Response:
                       """;
        return prompt;
    }

    public static SearchQueryPlan ParsePlanResponse(string responseText, string originalQuery)
    {
        ArgumentNullException.ThrowIfNull(responseText);
        ArgumentException.ThrowIfNullOrEmpty(originalQuery);

        var cleaned = responseText
                      .Replace(JsonCodeFenceOpen, string.Empty)
                      .Replace(CodeFence, string.Empty)
                      .Trim();

        var result = SearchQueryPlan.Disabled(originalQuery);

        try
        {
            using var document = JsonDocument.Parse(cleaned);
            var root = document.RootElement;
            var expandedQuery = root.TryGetProperty(ExpandedQueryKey, out var expandedQueryProperty)
                ? expandedQueryProperty.GetString()
                : null;
            var preferredCategory = root.TryGetProperty(PreferredCategoryKey, out var preferredCategoryProperty)
                ? ParseCategory(preferredCategoryProperty)
                : null;
            var preferTypePages = root.TryGetProperty(PreferTypePagesKey, out var preferTypePagesProperty) &&
                                  preferTypePagesProperty.ValueKind == JsonValueKind.True;
            var penalizeMemberPages = root.TryGetProperty(PenalizeMemberPagesKey, out var penalizeMemberPagesProperty) &&
                                      penalizeMemberPagesProperty.ValueKind == JsonValueKind.True;
            var penalize3D = root.TryGetProperty(Penalize3DKey, out var penalize3DProperty) &&
                             penalize3DProperty.ValueKind == JsonValueKind.True;
            var boostTerms = root.TryGetProperty(BoostTermsKey, out var boostTermsProperty)
                ? ParseTerms(boostTermsProperty)
                : [];
            var penalizeTerms = root.TryGetProperty(PenalizeTermsKey, out var penalizeTermsProperty)
                ? ParseTerms(penalizeTermsProperty)
                : [];
            var confidence = root.TryGetProperty(ConfidenceKey, out var confidenceProperty)
                ? ParseConfidence(confidenceProperty)
                : 0f;

            result = new SearchQueryPlan
                         {
                             ExpandedQuery = string.IsNullOrWhiteSpace(expandedQuery) ? originalQuery : expandedQuery,
                             PreferredCategory = preferredCategory,
                             PreferTypePages = preferTypePages,
                             PenalizeMemberPages = penalizeMemberPages,
                             Penalize3D = penalize3D,
                             BoostTerms = boostTerms,
                             PenalizeTerms = penalizeTerms,
                             Confidence = confidence
                         };
            result = RefinePlan(originalQuery, result);
        }
        catch(JsonException)
        {
            result = SearchQueryPlan.Disabled(originalQuery);
        }

        return result;
    }

    private static DocCategory? ParseCategory(JsonElement property)
    {
        DocCategory? result = null;
        if (property.ValueKind == JsonValueKind.String)
        {
            var raw = property.GetString();
            if (!string.IsNullOrWhiteSpace(raw) && !raw.Equals(NullLiteral, StringComparison.OrdinalIgnoreCase) &&
                Enum.TryParse<DocCategory>(raw, ignoreCase: true, out var parsed))
            {
                result = parsed;
            }
        }

        return result;
    }

    private static IReadOnlyList<string> ParseTerms(JsonElement property)
    {
        var result = new List<string>();
        if (property.ValueKind == JsonValueKind.Array)
        {
            foreach(var item in property.EnumerateArray())
            {
                var normalizedTerm = NormalizeTerm(item);
                if (!string.IsNullOrWhiteSpace(normalizedTerm) && result.Count < MaxTerms)
                    result.Add(normalizedTerm);
            }
        }

        return result;
    }

    private static string? NormalizeTerm(JsonElement item)
    {
        string? result = null;
        if (item.ValueKind == JsonValueKind.String)
        {
            var term = item.GetString();
            if (!string.IsNullOrWhiteSpace(term))
                result = term.Trim();
        }

        return result;
    }

    private static float ParseConfidence(JsonElement property)
    {
        var result = 0f;
        if (property.ValueKind == JsonValueKind.Number)
            result = Math.Clamp((float) property.GetDouble(), MinConfidence, MaxConfidence);
        return result;
    }

    private static SearchQueryPlan RefinePlan(string originalQuery, SearchQueryPlan parsedPlan)
    {
        ArgumentException.ThrowIfNullOrEmpty(originalQuery);
        ArgumentNullException.ThrowIfNull(parsedPlan);

        var mentionsModifier = ContainsToken(originalQuery, ModifierSingular) || ContainsToken(originalQuery, ModifierPlural);
        var mentionsCursor = ContainsToken(originalQuery, CursorToken);
        var mentions3D = ContainsToken(originalQuery, ThreeDToken) || ContainsToken(originalQuery, ThreeDimensionalToken);
        var mentionsMemberIntent = ContainsAnyToken(originalQuery,
                                                    [
                                                        MethodSingular,
                                                        MethodPlural,
                                                        PropertySingular,
                                                        PropertyPlural,
                                                        MemberSingular,
                                                        MemberPlural,
                                                        EventSingular,
                                                        EventPlural
                                                    ]);
        var mentionsTypeIntent = ContainsAnyToken(originalQuery,
                                                  [
                                                      ClassSingular,
                                                      ClassPlural,
                                                      InterfaceSingular,
                                                      InterfacePlural,
                                                      TypeSingular,
                                                      TypePlural,
                                                      ModifierSingular,
                                                      ModifierPlural
                                                  ]);
        var preferTypePages = parsedPlan.PreferTypePages || (mentionsTypeIntent && !mentionsMemberIntent);
        var penalizeMemberPages = parsedPlan.PenalizeMemberPages || ((mentionsTypeIntent || mentionsModifier) && !mentionsMemberIntent);
        var penalize3D = parsedPlan.Penalize3D || ((mentionsModifier || mentionsTypeIntent) && !mentions3D);
        var boostTerms = MergeTerms(parsedPlan.BoostTerms,
                                    mentionsModifier ? [ModifierTypeTerm] : [],
                                    mentionsCursor && mentionsModifier ? [CursorModifierTypeTerm] : []);
        var penalizeTerms = MergeTerms(parsedPlan.PenalizeTerms,
                                       mentionsCursor && mentionsModifier ? [SetMouseCursorPenaltyTerm] : []);
        var result = parsedPlan with
                         {
                             PreferTypePages = preferTypePages,
                             PenalizeMemberPages = penalizeMemberPages,
                             Penalize3D = penalize3D,
                             BoostTerms = boostTerms,
                             PenalizeTerms = penalizeTerms
                         };
        return result;
    }

    private static IReadOnlyList<string> MergeTerms(IReadOnlyList<string> baseTerms, params IReadOnlyList<string>[] additions)
    {
        ArgumentNullException.ThrowIfNull(baseTerms);
        ArgumentNullException.ThrowIfNull(additions);

        var result = new List<string>();
        AddUniqueTerms(result, baseTerms);
        foreach(var addition in additions)
            AddUniqueTerms(result, addition);
        return result;
    }

    private static void AddUniqueTerms(List<string> destination, IReadOnlyList<string> source)
    {
        ArgumentNullException.ThrowIfNull(destination);
        ArgumentNullException.ThrowIfNull(source);

        foreach(var term in source)
        {
            var alreadyPresent = destination.Contains(term, StringComparer.OrdinalIgnoreCase);
            if (!string.IsNullOrWhiteSpace(term) && !alreadyPresent && destination.Count < MaxTerms)
                destination.Add(term);
        }
    }

    private static bool ContainsAnyToken(string text, IReadOnlyList<string> tokens)
    {
        ArgumentException.ThrowIfNullOrEmpty(text);
        ArgumentNullException.ThrowIfNull(tokens);

        var result = tokens.Any(token => ContainsToken(text, token));
        return result;
    }

    private static bool ContainsToken(string text, string token)
    {
        ArgumentException.ThrowIfNullOrEmpty(text);
        ArgumentException.ThrowIfNullOrEmpty(token);

        var result = text.Contains(token, StringComparison.OrdinalIgnoreCase);
        return result;
    }

    public const string PromptVersion = "v1";

    private const int MaxResponseChars = 4096;
    private const int MaxTerms = 6;
    private const float MinConfidence = 0f;
    private const float MaxConfidence = 1f;
    private const string JsonCodeFenceOpen = "```json";
    private const string CodeFence = "```";
    private const string NullLiteral = "null";
    private const string ExpandedQueryKey = "expandedQuery";
    private const string PreferredCategoryKey = "preferredCategory";
    private const string PreferTypePagesKey = "preferTypePages";
    private const string PenalizeMemberPagesKey = "penalizeMemberPages";
    private const string Penalize3DKey = "penalize3D";
    private const string BoostTermsKey = "boostTerms";
    private const string PenalizeTermsKey = "penalizeTerms";
    private const string ConfidenceKey = "confidence";
    private const string ModifierSingular = "modifier";
    private const string ModifierPlural = "modifiers";
    private const string CursorToken = "cursor";
    private const string ThreeDToken = "3d";
    private const string ThreeDimensionalToken = "three-dimensional";
    private const string MethodSingular = "method";
    private const string MethodPlural = "methods";
    private const string PropertySingular = "property";
    private const string PropertyPlural = "properties";
    private const string MemberSingular = "member";
    private const string MemberPlural = "members";
    private const string EventSingular = "event";
    private const string EventPlural = "events";
    private const string ClassSingular = "class";
    private const string ClassPlural = "classes";
    private const string InterfaceSingular = "interface";
    private const string InterfacePlural = "interfaces";
    private const string TypeSingular = "type";
    private const string TypePlural = "types";
    private const string ModifierTypeTerm = "Modifier";
    private const string CursorModifierTypeTerm = "CursorModifier";
    private const string SetMouseCursorPenaltyTerm = "SetMouseCursor";
}