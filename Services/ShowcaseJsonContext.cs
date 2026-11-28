using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace Glacier.Showcase.Services;

public sealed class FilterPropertiesArgs
{
    [JsonPropertyName("maxPrice")]
    public double MaxPrice { get; set; } = 150.0;

    [JsonPropertyName("minReviews")]
    public int MinReviews { get; set; } = 10;

    [JsonPropertyName("neighborhood")]
    public string? Neighborhood { get; set; }
}

public sealed class SemanticSearchArgs
{
    [JsonPropertyName("vibe")]
    public string Vibe { get; set; } = "cozy";

    [JsonPropertyName("neighborhood")]
    public string? Neighborhood { get; set; }
}

public sealed class AuditNeighborhoodArgs
{
    [JsonPropertyName("neighborhood")]
    public string Neighborhood { get; set; } = "Westminster";
}

public sealed class GenerateReportArgs
{
    [JsonPropertyName("rationale")]
    public string Rationale { get; set; } = string.Empty;
}

public sealed class ExtractedSearchParameters
{
    [JsonPropertyName("maxPrice")]
    public double MaxPrice { get; set; } = 150.0;

    [JsonPropertyName("minReviews")]
    public int MinReviews { get; set; } = 10;

    [JsonPropertyName("vibe")]
    public string Vibe { get; set; } = "cozy";

    [JsonPropertyName("neighborhood")]
    public string? Neighborhood { get; set; }
}

public sealed class NeighborhoodAuditResult
{
    public NeighborhoodProfile? Profile { get; set; }
    public TransitRoute? Route { get; set; }
}

public sealed class ShowcaseRunReport
{
    public DateTime Timestamp { get; set; }
    public string ExecutionPhase { get; set; } = "Finished";
    public string? CurrentPhase { get; set; }
    public int FinalMatchesCount { get; set; }
    public string? ProspectusPdfPath { get; set; }
    public string? PdfPath { get; set; }
    public List<PropertyMatch> FilteredProperties { get; set; } = new();
    public List<PropertyMatch> FinalMatches { get; set; } = new();
    public List<PropertyUnderwriting> UnderwrittenProperties { get; set; } = new();
    public NeighborhoodProfile? ActiveNeighborhoodProfile { get; set; }
    public TransitRoute? ActiveTransitRoute { get; set; }
    public List<ChatMessage> ChatHistory { get; set; } = new();
    public List<AgentLogEntry> ExecutionLogs { get; set; } = new();
    public Dictionary<string, List<AgentLogEntry>> AgentTelemetry { get; set; } = new();
}

public sealed class TelemetryLogLine
{
    [JsonPropertyName("step")]
    public int Step { get; set; }

    [JsonPropertyName("loss")]
    public float Loss { get; set; }

    [JsonPropertyName("lr")]
    public float Lr { get; set; } = 2e-4f;

    [JsonPropertyName("tok_sec")]
    public double? TokSec { get; set; }
}

public sealed class GeminiEmbedRequest
{
    [JsonPropertyName("model")]
    public string Model { get; set; } = "models/gemini-embedding-001";

    [JsonPropertyName("content")]
    public GeminiEmbedContent Content { get; set; } = new();
}

public sealed class GeminiEmbedContent
{
    [JsonPropertyName("parts")]
    public GeminiEmbedPart[] Parts { get; set; } = Array.Empty<GeminiEmbedPart>();
}

public sealed class GeminiEmbedPart
{
    [JsonPropertyName("text")]
    public string Text { get; set; } = string.Empty;
}

public sealed class GeminiEmbedResponse
{
    [JsonPropertyName("embedding")]
    public GeminiEmbeddingData? Embedding { get; set; }
}

public sealed class GeminiEmbeddingData
{
    [JsonPropertyName("values")]
    public float[]? Values { get; set; }
}

[JsonSourceGenerationOptions(
    PropertyNameCaseInsensitive = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    WriteIndented = false)]
[JsonSerializable(typeof(PropertyMatch))]
[JsonSerializable(typeof(List<PropertyMatch>))]
[JsonSerializable(typeof(NeighborhoodProfile))]
[JsonSerializable(typeof(TransitRoute))]
[JsonSerializable(typeof(PropertyUnderwriting))]
[JsonSerializable(typeof(List<PropertyUnderwriting>))]
[JsonSerializable(typeof(ChatMessage))]
[JsonSerializable(typeof(List<ChatMessage>))]
[JsonSerializable(typeof(AgentLogEntry))]
[JsonSerializable(typeof(List<AgentLogEntry>))]
[JsonSerializable(typeof(Dictionary<string, List<AgentLogEntry>>))]
[JsonSerializable(typeof(FilterPropertiesArgs))]
[JsonSerializable(typeof(SemanticSearchArgs))]
[JsonSerializable(typeof(AuditNeighborhoodArgs))]
[JsonSerializable(typeof(GenerateReportArgs))]
[JsonSerializable(typeof(ExtractedSearchParameters))]
[JsonSerializable(typeof(NeighborhoodAuditResult))]
[JsonSerializable(typeof(ShowcaseRunReport))]
[JsonSerializable(typeof(TelemetryLogLine))]
[JsonSerializable(typeof(GeminiEmbedRequest))]
[JsonSerializable(typeof(GeminiEmbedContent))]
[JsonSerializable(typeof(GeminiEmbedPart))]
[JsonSerializable(typeof(GeminiEmbedResponse))]
[JsonSerializable(typeof(GeminiEmbeddingData))]
public partial class ShowcaseJsonContext : JsonSerializerContext
{
}

[JsonSourceGenerationOptions(
    PropertyNameCaseInsensitive = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    WriteIndented = true)]
[JsonSerializable(typeof(NeighborhoodAuditResult))]
[JsonSerializable(typeof(ShowcaseRunReport))]
[JsonSerializable(typeof(PropertyMatch))]
[JsonSerializable(typeof(List<PropertyMatch>))]
public partial class ShowcaseIndentedJsonContext : JsonSerializerContext
{
}
