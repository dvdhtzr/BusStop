using System.Text.Json;

namespace BusStop.Models;

/// <summary>A deterministic model for tests and offline runs. It returns scripted responses in order,
/// so the coordinator, context, persistence, and commit path can be exercised with no real model,
/// including scripted failure modes.</summary>
public sealed class StubModelClient : IModelClient
{
    private readonly Queue<ModelResponse> _scripted;

    public StubModelClient(IEnumerable<ModelResponse> scripted, string modelName = "stub", string modelDigest = "stub-digest")
    {
        _scripted = new Queue<ModelResponse>(scripted);
        ModelName = modelName;
        ModelDigest = modelDigest;
    }

    public string ModelName { get; }
    public string ModelDigest { get; }

    /// <summary>Responses remaining in the script. Useful for asserting the engine made exactly the
    /// expected number of model calls.</summary>
    public int Remaining => _scripted.Count;

    public ModelResponse Decide(ModelRequest request, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_scripted.Count == 0)
            throw new InvalidOperationException("StubModelClient script exhausted: the engine made more calls than scripted.");
        return _scripted.Dequeue();
    }
}

/// <summary>Builders for the raw responses a stub returns, so tests read declaratively.</summary>
public static class StubResponses
{
    public static ModelResponse Speak(string text, string? addressee = null, string? repliesTo = null)
    {
        var payload = new Dictionary<string, object?>
        {
            ["action"] = "speak",
            ["text"] = text,
            ["addressee"] = addressee,
            ["replies_to"] = repliesTo
        };
        return ModelResponse.Responded(JsonSerializer.Serialize(payload));
    }

    public static ModelResponse Pass() =>
        ModelResponse.Responded("{\"action\":\"pass\"}");

    public static ModelResponse EmptySpeak() =>
        ModelResponse.Responded("{\"action\":\"speak\",\"text\":\"   \"}");

    public static ModelResponse Malformed() =>
        ModelResponse.Responded("this is not json");

    public static ModelResponse Timeout() => ModelResponse.Timeout();
    public static ModelResponse TransportError() => ModelResponse.TransportError();
    public static ModelResponse Cancelled() => ModelResponse.Cancelled();
}
