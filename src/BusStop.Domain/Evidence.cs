namespace BusStop.Domain;

/// <summary>Two input channels, one contract. These labels describe the origin of
/// source evidence, not the character's private memories or invented anecdotes.</summary>
public enum EvidenceSourceKind
{
    NewsArticle,
    UserDocument
}

/// <summary>Required identity and provenance for material supplied to a conversation.
/// A URL belongs only to public news; never persist the user's absolute document path.
/// RightsBasis records a caller's explicit rights assessment; it does not grant rights.</summary>
public sealed record EvidenceSourceDescriptor(
    EvidenceSourceKind Kind,
    string Title,
    string SourceName,
    string? SourceUrl,
    DateTimeOffset? PublishedAt,
    string RightsBasis);

/// <summary>A stable, attributable portion of a frozen source. Offsets and length
/// refer to UTF-16 positions in SnapshotText, with an exclusive end boundary.
/// PassageId is unique to the source snapshot and ordinal, not a guessed citation.</summary>
public sealed record EvidencePassage(
    string PassageId,
    int Ordinal,
    int StartOffset,
    int Length,
    string Text)
{
    public int EndOffset => checked(StartOffset + Length);
}

/// <summary>Versioned immutable-by-convention evidence captured before a conversation.
/// SnapshotId and TextSha256 identify what was captured; CapturedAt is provenance,
/// not part of the content fingerprint. No executable instructions are derived from text.
/// Passage collections must be read-only when constructing this record.</summary>
public sealed record EvidenceSnapshot(
    string SnapshotId,
    EvidenceSourceDescriptor Descriptor,
    string TextSha256,
    DateTimeOffset CapturedAt,
    string SnapshotText,
    IReadOnlyList<EvidencePassage> Passages);
