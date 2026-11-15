using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using BusStop.Domain;

namespace BusStop.Sources;

/// <summary>Captures already-obtained text with a source-neutral, reproducible
/// fingerprint and stable passage citations. Does not fetch news, execute document
/// instructions, summarize, embed, or perform retrieval.</summary>
public static class EvidenceSnapshotFactory
{
    public const int DefaultPassageCharacters = 1400;
    public const int DefaultOverlapCharacters = 180;

    public static EvidenceSnapshot Capture(
        EvidenceSourceDescriptor descriptor,
        string text,
        DateTimeOffset? capturedAt = null,
        int passageCharacters = DefaultPassageCharacters,
        int overlapCharacters = DefaultOverlapCharacters)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        ArgumentNullException.ThrowIfNull(text);

        if (!Enum.IsDefined(descriptor.Kind))
            throw new ArgumentException("Unknown evidence source kind.", nameof(descriptor));
        RequireText(descriptor.Title, 256, "title");
        RequireText(descriptor.SourceName, 256, "source name");
        RequireText(descriptor.RightsBasis, 256, "rights basis");

        if (descriptor.Kind == EvidenceSourceKind.NewsArticle)
        {
            if (descriptor.PublishedAt is null ||
                !Uri.TryCreate(descriptor.SourceUrl, UriKind.Absolute, out var url) ||
                url.Scheme != Uri.UriSchemeHttps || !string.IsNullOrEmpty(url.UserInfo))
                throw new ArgumentException(
                    "News requires a published date and an HTTPS canonical URL.", nameof(descriptor));
        }
        else if (descriptor.SourceUrl is not null)
        {
            throw new ArgumentException(
                "Local document metadata must not include a URL or local file path.", nameof(descriptor));
        }

        if (string.IsNullOrWhiteSpace(text))
            throw new ArgumentException("Cannot capture empty source evidence.", nameof(text));

        if (passageCharacters < 32 || passageCharacters > 8192 ||
            overlapCharacters < 0 || overlapCharacters >= passageCharacters / 2)
            throw new ArgumentOutOfRangeException(nameof(passageCharacters),
                "Passage size must be 32–8192 characters with overlap less than half its size.");

        var contentHash = HexSha256(Encoding.UTF8.GetBytes(text));

        // Serialize stable metadata with deterministic property order.
        // CapturedAt is intentionally excluded, so repeated capture of identical
        // source, rights declaration, and text yields the same citation IDs.
        var identity = JsonSerializer.Serialize(new
        {
            Kind = descriptor.Kind.ToString(),
            descriptor.Title,
            descriptor.SourceName,
            descriptor.SourceUrl,
            PublishedAt = descriptor.PublishedAt?.ToUniversalTime().ToString("O"),
            descriptor.RightsBasis,
            TextSha256 = contentHash
        });
        var snapshotId = HexSha256(Encoding.UTF8.GetBytes(identity));
        var passages = BuildPassages(snapshotId, text, passageCharacters, overlapCharacters);

        return new EvidenceSnapshot(
            snapshotId, descriptor, contentHash,
            (capturedAt ?? DateTimeOffset.UtcNow).ToUniversalTime(),
            text, Array.AsReadOnly(passages.ToArray()));
    }

    private static IEnumerable<EvidencePassage> BuildPassages(
        string snapshotId, string text, int maxChars, int overlap)
    {
        var start = 0;
        var ordinal = 1;

        while (start < text.Length)
        {
            var end = Math.Min(text.Length, checked(start + maxChars));
            if (end < text.Length)
            {
                // Prefer a natural break without losing any character.
                var floor = start + maxChars / 2;
                for (var i = end - 1; i >= floor; i--)
                {
                    if (!char.IsWhiteSpace(text[i])) continue;
                    end = i + 1;
                    break;
                }

                // UTF-16 offsets must not split a surrogate pair.
                if (char.IsHighSurrogate(text[end - 1]) && char.IsLowSurrogate(text[end]))
                    end--;
            }

            var length = end - start;
            yield return new EvidencePassage(
                $"{snapshotId}:p{ordinal:D5}",
                ordinal, start, length, text.Substring(start, length));

            if (end == text.Length) yield break;
            var next = end - overlap;
            if (next <= start) next = end;
            if (next > 0 && next < text.Length &&
                char.IsLowSurrogate(text[next]) && char.IsHighSurrogate(text[next - 1]))
                next--;
            start = next;
            ordinal++;
        }
    }

    private static void RequireText(string? value, int maxLength, string name)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > maxLength)
            throw new ArgumentException($"Evidence {name} must contain 1–{maxLength} characters.");
    }

    private static string HexSha256(byte[] bytes) =>
        Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
}
