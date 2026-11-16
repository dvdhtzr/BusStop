using System.Net.Http.Json;
using System.Text.Json;

namespace BusStop.Models;

/// <summary>An <see cref="IModelClient"/> backed by a local Ollama server. It requests JSON-constrained
/// output and maps transport problems to the right outcomes — a timeout, a cancellation, and a transport
/// error are distinct and none is ever a pass. Content validity is still decided by the validator.</summary>
public sealed class OllamaAdapter : IModelClient, IDisposable
{
    private readonly HttpClient _http;
    private readonly double _temperature;
    private readonly bool? _think;

    public OllamaAdapter(string model, Uri? endpoint = null, double temperature = 0.8, TimeSpan? timeout = null, bool? think = null)
    {
        ModelName = model;
        _temperature = temperature;
        _think = think;
        _http = new HttpClient
        {
            BaseAddress = endpoint ?? new Uri("http://localhost:11434"),
            Timeout = timeout ?? TimeSpan.FromSeconds(120)
        };
        ModelDigest = TryGetDigest(model) ?? "unknown";
    }

    public string ModelName { get; }
    public string ModelDigest { get; }

    /// <summary>True when an Ollama server is reachable at the endpoint, so the acceptance probe can skip
    /// cleanly when none is present.</summary>
    public static bool IsAvailable(Uri? endpoint = null)
    {
        try
        {
            using var http = new HttpClient { BaseAddress = endpoint ?? new Uri("http://localhost:11434"), Timeout = TimeSpan.FromSeconds(3) };
            return http.GetAsync("/api/tags").GetAwaiter().GetResult().IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }

    public ModelResponse Decide(ModelRequest request, CancellationToken cancellationToken = default)
    {
        if (request.MaxGeneratedTokens is < 1 or > 4096 ||
            request.ContextTokens is < 1024 or > 131072)
            throw new ArgumentOutOfRangeException(nameof(request), "Invalid generation limits.");

        var options = new Dictionary<string, object?>
        {
            ["temperature"] = _temperature
        };
        if (request.MaxGeneratedTokens is { } maxGenerated)
            options["num_predict"] = maxGenerated;
        if (request.ContextTokens is { } context)
            options["num_ctx"] = context;

        // Schema validation remains host-authoritative. Constrained decoding is
        // defense in depth, never a replacement for candidate/evidence validation.
        object format = "json";
        if (request.JsonSchema is { } schema)
        {
            using var parsed = JsonDocument.Parse(schema);
            if (parsed.RootElement.ValueKind != JsonValueKind.Object)
                throw new ArgumentException("Ollama JSON schema must be an object.", nameof(request));
            format = parsed.RootElement.Clone();
        }

        var body = new Dictionary<string, object?>
        {
            ["model"] = ModelName,
            ["prompt"] = request.Prompt,
            ["stream"] = false,
            ["format"] = format,
            ["options"] = options
        };
        // For reasoning models (e.g. qwen3), think:false skips the hidden reasoning pass, which is a
        // large speed-up for short conversational turns. Omitted entirely when not specified.
        if (_think is bool think)
            body["think"] = think;

        try
        {
            using var response = _http.PostAsJsonAsync("/api/generate", body, cancellationToken).GetAwaiter().GetResult();
            if (!response.IsSuccessStatusCode)
            {
                var detail = awaitlessRead(response, cancellationToken);
                return ModelResponse.TransportError() with
                {
                    TransportDetail = $"HTTP {(int)response.StatusCode}: " +
                        (detail.Length > 500 ? detail[..500] : detail)
                };
            }

            var payload = response.Content.ReadAsStringAsync(cancellationToken).GetAwaiter().GetResult();
            using var doc = JsonDocument.Parse(payload);
            var text = doc.RootElement.TryGetProperty("response", out var r) ? r.GetString() : null;
            if (text is null) return ModelResponse.TransportError();
            var root = doc.RootElement;
            static int? Count(JsonElement root, string key) =>
                root.TryGetProperty(key, out var value) && value.TryGetInt32(out var count)
                    ? count : null;
            static long? Duration(JsonElement root, string key) =>
                root.TryGetProperty(key, out var value) && value.TryGetInt64(out var duration)
                    ? duration : null;
            return ModelResponse.Responded(text) with
            {
                Generation = new ModelGenerationTelemetry(
                    root.TryGetProperty("done_reason", out var done) &&
                    done.ValueKind == JsonValueKind.String ? done.GetString() : null,
                    Count(root, "prompt_eval_count"),
                    Count(root, "eval_count"),
                    Duration(root, "total_duration"))
            };
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return ModelResponse.Cancelled();
        }
        catch (TaskCanceledException)
        {
            return ModelResponse.Timeout();
        }
        catch (HttpRequestException)
        {
            return ModelResponse.TransportError();
        }
        catch (JsonException)
        {
            return ModelResponse.TransportError();
        }
    }

    private static string awaitlessRead(HttpResponseMessage response,
        CancellationToken cancellationToken) =>
        response.Content.ReadAsStringAsync(cancellationToken).GetAwaiter().GetResult();

    private string? TryGetDigest(string model)
    {
        try
        {
            using var response = _http.GetAsync("/api/tags").GetAwaiter().GetResult();
            if (!response.IsSuccessStatusCode) return null;
            using var doc = JsonDocument.Parse(response.Content.ReadAsStringAsync().GetAwaiter().GetResult());
            if (!doc.RootElement.TryGetProperty("models", out var models)) return null;
            foreach (var m in models.EnumerateArray())
            {
                if (m.TryGetProperty("name", out var name) && name.GetString() == model &&
                    m.TryGetProperty("digest", out var digest))
                    return digest.GetString();
            }
        }
        catch
        {
            // Digest is best-effort metadata; the probe still runs without it.
        }

        return null;
    }

    public void Dispose() => _http.Dispose();
}
