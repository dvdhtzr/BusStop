using System.Text;
using System.Text.RegularExpressions;
using BusStop.Domain;
using BusStop.Moderation.Verification;
using BusStop.Persistence;

namespace BusStop.Engine.Verification;

/// <summary>
/// Conservative V3 packet ceilings. These are experimental selection limits,
/// NOT a reliable Ollama tokenizer; UTF-8 byte and UTF-16 caps are enforced.
/// </summary>
public sealed record V3PacketLimits(
    int MaxClaimsPerJob = 12,
    int MaxItemsPerClaim = 12,
    int MaxEvidenceUtf16CharsPerClaim = 10_000,
    int MaxPacketUtf8Bytes = 20_000,
    int MaxTotalPacketUtf8Bytes = 80_000);

public sealed record V3PackagedClaim(
    ClaimCandidate Claim,
    EvidencePacket Packet,
    VerificationEvidenceScope HostScope);

public sealed record V3PacketAssembly(
    IReadOnlyList<V3PackagedClaim> Packets,
    IReadOnlyList<Guid> OmittedClaimIds,
    EvidenceCompletenessStatus JobCoverage,
    int TotalPacketUtf8Bytes);

/// <summary>
/// READ-ONLY and model-free. Builds V1 packets only from V2 host-scoped evidence.
/// No attempt to fetch, repair, reinterpret or upgrade evidence is allowed.
/// </summary>
public sealed class V3EvidencePacketAssembler
{
    public const string RetrievalSelectorVersion = "host-packet-selector-v3-1";
    public const string PacketSchemaVersion = "frozen-verifier-packet-v3-1";

    private readonly ConversationStore _store;

    public V3EvidencePacketAssembler(ConversationStore store) =>
        _store = store ?? throw new ArgumentNullException(nameof(store));

    public V3PacketAssembly Assemble(
        VerificationJobIdentity job, Utterance observed,
        V3ExtractionResult extraction, V3PacketLimits? limits = null)
    {
        EvidenceVerificationValidator.ValidateJob(job, observed);
        if (extraction is null || extraction.Claims is null ||
            !Enum.IsDefined(typeof(V3ExtractionCoverage), extraction.Coverage) ||
            extraction.ModelAttempts is < 0 or > 1 ||
            extraction.ScannedUtf16Characters < 0 ||
            extraction.ScannedUtf16Characters > observed.Text.Length)
            throw new InvalidDataException("Invalid V3 extraction result.");

        var config = limits ?? new V3PacketLimits();
        ValidateLimits(config);
        EvidenceVerificationValidator.ValidateClaims(extraction.Claims, job, observed);
        if (extraction.Claims.Count > V3ClaimDetector.MaxCandidates)
            throw new InvalidDataException("Extractor claim set exceeded the V3 safety cap.");

        var packages = new List<V3PackagedClaim>();
        var omitted = new List<Guid>();
        var partial = !extraction.SelectionComplete ||
            extraction.Coverage is V3ExtractionCoverage.ModelTransportFailure or
                V3ExtractionCoverage.ModelInvalidResponse or
                V3ExtractionCoverage.InputTruncated or
                V3ExtractionCoverage.ModelSkippedBySampling or
                V3ExtractionCoverage.ModelSkippedForBudget;
        var unknown = false;
        var totalBytes = 0;

        foreach (var claim in extraction.Claims)
        {
            if (packages.Count >= config.MaxClaimsPerJob)
            {
                omitted.Add(claim.ClaimId);
                partial = true;
                continue;
            }

            // Never pretend that an ambiguous third-person identity claim is
            // about the speaker. Host subject resolution is intentionally
            // conservative until an explicit subject-mapping index exists.
            var retrievalCategory = claim.Category;
            if ((claim.Category is ClaimCategory.PersonalHistory or
                ClaimCategory.CharacterIdentity) &&
                !IsFirstPerson(claim.ClaimText))
                retrievalCategory = ClaimCategory.Other;

            var evidence = _store.SelectVerifierEvidence(
                job, observed, retrievalCategory,
                maxItems: Math.Min(32, config.MaxItemsPerClaim));
            if (evidence.Completeness.Status == EvidenceCompletenessStatus.Unknown)
                unknown = true;

            var ordered = evidence.Scope.AuthorizedItems.Values
                .OrderBy(x => x.Kind).ThenBy(x => x.ReferenceId, StringComparer.Ordinal)
                .ToArray();

            var picked = new List<EvidenceItem>();
            var characters = 0;
            var skippedItems = false;

            foreach (var item in ordered)
            {
                if (picked.Count >= config.MaxItemsPerClaim ||
                    item.Excerpt.Length > config.MaxEvidenceUtf16CharsPerClaim - characters)
                {
                    skippedItems = true;
                    continue;
                }
                picked.Add(item);
                characters += item.Excerpt.Length;
            }

            var baseCompleteness = evidence.Completeness;
            if (skippedItems) partial = true;
            if (baseCompleteness.Status == EvidenceCompletenessStatus.Partial)
                partial = true;

            // Retain the V2 completeness declaration, but never call a
            // truncated packet complete even for a narrow search universe.
            EvidenceCompleteness Completeness(bool cut) => baseCompleteness with
            {
                Status = baseCompleteness.Status == EvidenceCompletenessStatus.Unknown
                    ? EvidenceCompletenessStatus.Unknown
                    : cut || baseCompleteness.Status == EvidenceCompletenessStatus.Partial
                        ? EvidenceCompletenessStatus.Partial
                        : EvidenceCompletenessStatus.CompleteForDeclaredScope,
                ExcludedRanges = cut
                    ? TruncatedExclusion(baseCompleteness.ExcludedRanges)
                    : baseCompleteness.ExcludedRanges
            };

            var packetId = Guid.NewGuid();
            EvidencePacket PacketFor(IReadOnlyList<EvidenceItem> items, bool cut) =>
                EvidencePacketFactory.Freeze(packetId, new EvidencePacketContent(
                    EvidenceVerifierV1.ContractVersion,
                    EvidenceVerifierV1.PacketSerializerVersion,
                    job.JobId, claim.ClaimId, job.CommittedBoundary,
                    RetrievalSelectorVersion, V3ModelClaimExtractor.PromptVersion,
                    PacketSchemaVersion, Completeness(cut), items));

            var packet = PacketFor(picked, skippedItems);

            // V3's byte limit is stricter than V1's upper contract limit.
            // Discard whole items, rather than modifying excerpts and thereby
            // breaking exact host-scope authorization/content identity.
            while (Encoding.UTF8.GetByteCount(packet.CanonicalJson) > config.MaxPacketUtf8Bytes &&
                   picked.Count > 0)
            {
                picked.RemoveAt(picked.Count - 1);
                skippedItems = true;
                partial = true;
                packet = PacketFor(picked, true);
            }

            var bytes = Encoding.UTF8.GetByteCount(packet.CanonicalJson);
            if (bytes > config.MaxPacketUtf8Bytes ||
                bytes > config.MaxTotalPacketUtf8Bytes - totalBytes)
            {
                omitted.Add(claim.ClaimId);
                partial = true;
                continue;
            }

            EvidenceVerificationValidator.ValidatePacket(
                packet, claim, job, observed, evidence.Scope);
            packages.Add(new V3PackagedClaim(claim, packet, evidence.Scope));
            totalBytes += bytes;
        }

        var coverage = partial || omitted.Count > 0
            ? EvidenceCompletenessStatus.Partial
            : unknown ? EvidenceCompletenessStatus.Unknown
            // Even fully selected deterministic cues cannot certify that ALL
            // semantic claims were discovered; this is packet coverage only.
            : EvidenceCompletenessStatus.CompleteForDeclaredScope;

        return new V3PacketAssembly(
            Array.AsReadOnly(packages.ToArray()),
            Array.AsReadOnly(omitted.ToArray()), coverage, totalBytes);
    }

    private static string TruncatedExclusion(string? prior)
    {
        var detail = "Some authorized evidence omitted by V3 packet limits" +
            (prior is null ? "" : "; " + prior);
        return detail.Length <= 1_024 ? detail : detail[..1_024];
    }

    private static bool IsFirstPerson(string text) =>
        Regex.IsMatch(text,
            @"^\s*(?:I|I've|I'm|my|mine|we|our)\b",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static void ValidateLimits(V3PacketLimits limits)
    {
        if (limits.MaxClaimsPerJob is < 1 or > V3ClaimDetector.MaxCandidates ||
            limits.MaxItemsPerClaim is < 1 or > 32 ||
            limits.MaxEvidenceUtf16CharsPerClaim is < 128 or > 131_072 ||
            limits.MaxPacketUtf8Bytes is < 1_024 or > 131_072 ||
            limits.MaxTotalPacketUtf8Bytes < limits.MaxPacketUtf8Bytes ||
            limits.MaxTotalPacketUtf8Bytes > 1_048_576)
            throw new InvalidDataException("V3 packet limits must be positive and bounded.");
    }
}
