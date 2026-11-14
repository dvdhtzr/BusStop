using BusStop.Domain;

namespace BusStop.Models;

/// <summary>What a participant is asked to decide: who they are and the fully constructed prompt the
/// context builder produced. The model client never sees anything the context builder did not include.</summary>
public sealed record ModelRequest(CharacterId Participant, string Prompt);

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
