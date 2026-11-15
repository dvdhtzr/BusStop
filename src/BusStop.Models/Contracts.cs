using BusStop.Domain;

namespace BusStop.Models;

/// <summary>What a participant is asked to decide: who they are and the fully constructed prompt the
/// context builder produced. The model client never sees anything the context builder did not include.</summary>
/// <summary>Schema and generation limits are opt-in per request. Character requests
/// retain the original JSON mode and default Ollama settings.</summary>
public sealed record ModelRequest(
    CharacterId Participant, string Prompt,
    string? JsonSchema = null,
    int? MaxGeneratedTokens = null,
    int? ContextTokens = null);

/// <summary>Ollama generation telemetry, separate from the model's judgment text.</summary>
public sealed record ModelGenerationTelemetry(
    string? DoneReason,
    int? PromptEvalCount,
    int? EvalCount,
    long? TotalDurationNs);

/// <summary>Transport-level result of a model call, independent of the content's validity.</summary>
public enum ModelTransport
{
    Responded,
    Timeout,
    TransportError,
    Cancelled
}

/// <summary>A model call's raw result. When <see cref="Transport"/> is Responded, <see cref="RawJson"/>
/// holds the model's unparsed output; otherwise it is null. Content validity is decided later by the
/// validator, never here.</summary>
public sealed record ModelResponse(ModelTransport Transport, string? RawJson)
{
    public ModelGenerationTelemetry? Generation { get; init; }

    public static ModelResponse Responded(string rawJson) => new(ModelTransport.Responded, rawJson);
    public static ModelResponse Timeout() => new(ModelTransport.Timeout, null);
    public static ModelResponse TransportError() => new(ModelTransport.TransportError, null);
    public static ModelResponse Cancelled() => new(ModelTransport.Cancelled, null);
}

/// <summary>A local model behind one interface. The engine depends only on this, so the whole system
/// runs deterministically against a stub and Ollama exists only as one implementation.</summary>
public interface IModelClient
{
    string ModelName { get; }
    string ModelDigest { get; }
    ModelResponse Decide(ModelRequest request, CancellationToken cancellationToken = default);
}
