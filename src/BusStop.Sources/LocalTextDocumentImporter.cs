using System.Text;
using BusStop.Domain;

namespace BusStop.Sources;

/// <summary>Local, bounded first input path for user-provided plain-text and Markdown
/// documents. The content is never transmitted or uploaded by this importer.
/// PDF/DOCX extraction, embeddings, and conversational RAG are later milestones.</summary>
public sealed class LocalTextDocumentImporter
{
    public const long MaxDocumentBytes = 1_048_576;

    public EvidenceSnapshot Import(string path, string? title = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var file = new FileInfo(path);

        if (!file.Exists)
            throw new FileNotFoundException("The supplied document does not exist.", path);

        var extension = file.Extension.ToLowerInvariant();
        if (extension is not (".txt" or ".md"))
            throw new NotSupportedException("Only .txt and .md are supported in R1a.");

        if (file.LinkTarget is not null)
            throw new InvalidDataException("Linked documents are not accepted.");

        if (file.Length is <= 0 or > MaxDocumentBytes)
            throw new InvalidDataException("Document must be between 1 byte and 1 MiB.");

        // Read bounded bytes, check again against file replacement/racing writes,
        // and reject invalid UTF-8/binary content instead of silently corrupting it.
        var bytes = File.ReadAllBytes(file.FullName);
        if (bytes.LongLength is <= 0 or > MaxDocumentBytes)
            throw new InvalidDataException("Document size changed or exceeds 1 MiB.");

        string decoded;
        try
        {
            decoded = new UTF8Encoding(false, true).GetString(bytes);
        }
        catch (DecoderFallbackException ex)
        {
            throw new InvalidDataException("Document must be valid UTF-8.", ex);
        }

        // UTF-8 BOM is an encoding marker, not conversational evidence.
        var text = decoded.TrimStart('\uFEFF');
        if (text.Contains('\0'))
            throw new InvalidDataException("Binary/NUL-containing documents are unsupported.");
        if (string.IsNullOrWhiteSpace(text))
            throw new InvalidDataException("Document contains no readable text.");

        var displayTitle = title ?? Path.GetFileNameWithoutExtension(file.Name);
        return EvidenceSnapshotFactory.Capture(
            new EvidenceSourceDescriptor(
                EvidenceSourceKind.UserDocument,
                displayTitle,
                "User-provided local document",
                SourceUrl: null,
                PublishedAt: null,
                RightsBasis: "User submitted for private local discussion"),
            text);
    }
}
