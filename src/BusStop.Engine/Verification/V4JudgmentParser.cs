using System.Text.Json;
using BusStop.Domain;
using BusStop.Engine.Verification;
using BusStop.Moderation.Verification;

namespace BusStop.Engine.Verification;

/// <summary>
/// Schema-constrained independent claim judgments. Schema validation is not
/// semantic correctness: the host also checks identity, scope, authority and
/// rhetorical lane, while V6/V7 must calibrate substantive relevance.
/// </summary>
public static class V4JudgmentParser
{
    public const string JsonSchema = """
        {
          "type": "object",
          "additionalProperties": false,
          "required": ["jobId", "judgments"],
          "properties": {
            "jobId": {"type": "string"},
            "judgments": {
              "type": "array",
              "minItems": 1,
              "maxItems": 4,
              "items": {
                "type": "object",
                "additionalProperties": false,
                "required": ["claimId", "packetId", "packetSha256", "outcome",
                             "supportingReferences", "opposingReferences",
                             "problematicText", "explanation"],
                "properties": {
                  "claimId": {"type": "string"},
                  "packetId": {"type": "string"},
                  "packetSha256": {"type": "string"},
                  "outcome": {
                    "type": "string",
                    "enum": ["ConfirmedContradiction", "NoMaterialIssue",
                             "InsufficientEvidence", "ConflictingEvidence"]
                  },
                  "supportingReferences": {
                    "type": "array", "maxItems": 8, "items": {"type": "string"}
                  },
                  "opposingReferences": {
                    "type": "array", "maxItems": 8, "items": {"type": "string"}
                  },
                  "problematicText": {"type": "string", "maxLength": 4000},
                  "explanation": {"type": "string", "minLength": 1, "maxLength": 512}
                }
              }
            }
          }
        }
        """;

    public static IReadOnlyList<ClaimJudgment> Parse(
        string? raw,
        VerificationJobIdentity job,
        Utterance observed,
        IReadOnlyList<V3PackagedClaim> expected)
    {
        EvidenceVerificationValidator.ValidateJob(job, observed);
        if (string.IsNullOrWhiteSpace(raw) || raw.Length > 32_000 ||
            expected is null || expected.Count is < 1 or > EvidenceVerifierV4.MaxClaimsPerBatch)
            throw new InvalidDataException("Invalid V4 claim-verification response or expected batch.");

        var byId = new Dictionary<Guid, V3PackagedClaim>();
        foreach (var package in expected)
        {
            if (package?.Claim is null || package.Packet is null ||
                !byId.TryAdd(package.Claim.ClaimId, package))
                throw new InvalidDataException("Duplicate or missing expected V4 claim.");
        }

        using var parsed = JsonDocument.Parse(raw, new JsonDocumentOptions
        {
            MaxDepth = 16,
            AllowTrailingCommas = false,
            CommentHandling = JsonCommentHandling.Disallow
        });
        var root = parsed.RootElement;
        RequireProperties(root, "jobId", "judgments");
        if (!Guid.TryParse(root.GetProperty("jobId").GetString(), out var returnedJob) ||
            returnedJob != job.JobId)
            throw new InvalidDataException("Verifier returned a different job ID.");

        var entries = root.GetProperty("judgments");
        if (entries.ValueKind != JsonValueKind.Array ||
            entries.GetArrayLength() != expected.Count)
            throw new InvalidDataException("A verdict for every independent claim is required.");

        var results = new Dictionary<Guid, ClaimJudgment>();
        foreach (var entry in entries.EnumerateArray())
        {
            RequireProperties(entry, "claimId", "packetId", "packetSha256",
                "outcome", "supportingReferences", "opposingReferences",
                "problematicText", "explanation");

            if (!Guid.TryParse(entry.GetProperty("claimId").GetString(), out var claimId) ||
                !byId.TryGetValue(claimId, out var package) ||
                results.ContainsKey(claimId))
                throw new InvalidDataException("Foreign or repeated verifier claim ID.");

            if (!Guid.TryParse(entry.GetProperty("packetId").GetString(), out var packetId) ||
                packetId != package.Packet.PacketId)
                throw new InvalidDataException("Verifier packet ID mismatch.");

            var hash = entry.GetProperty("packetSha256").GetString()
                ?? throw new InvalidDataException("Missing packet hash.");
            if (!string.Equals(hash, package.Packet.Sha256, StringComparison.Ordinal))
                throw new InvalidDataException("Verifier packet content hash mismatch.");

            var outcomeText = entry.GetProperty("outcome").GetString();
            if (!Enum.TryParse<ClaimVerificationOutcome>(outcomeText, false, out var outcome) ||
                !Enum.IsDefined(typeof(ClaimVerificationOutcome), outcome))
                throw new InvalidDataException("Unsupported V4 epistemic outcome.");

            var judgment = new ClaimJudgment(
                claimId, packetId, hash, outcome,
                ReadReferences(entry.GetProperty("supportingReferences")),
                ReadReferences(entry.GetProperty("opposingReferences")),
                entry.GetProperty("problematicText").GetString()
                    ?? throw new InvalidDataException("Missing problematic text."),
                entry.GetProperty("explanation").GetString()
                    ?? throw new InvalidDataException("Missing explanation."));

            EvidenceVerificationValidator.ValidateJudgment(
                judgment, package.Packet, package.Claim, job, observed, package.HostScope);
            ValidateLaneAuthority(judgment, package);
            results.Add(claimId, judgment);
        }

        return Array.AsReadOnly(expected.Select(x => results[x.Claim.ClaimId]).ToArray());
    }

    public static void ValidateLaneAuthority(
        ClaimJudgment judgment, V3PackagedClaim package)
    {
        var claim = package.Claim;
        var packet = package.Packet;
        var items = packet.Content.Items.ToDictionary(i => i.ReferenceId,
            StringComparer.Ordinal);

        // Questions, speculation, humor and quoted matter cannot become
        // direct material contradictions merely from a model assertion.
        if (claim.RhetoricalMode != ClaimRhetoricalMode.Asserted &&
            judgment.Outcome == ClaimVerificationOutcome.ConfirmedContradiction)
            throw new InvalidDataException(
                "Rhetorical or quoted material cannot be a direct V4 contradiction.");

        if (judgment.Outcome == ClaimVerificationOutcome.ConfirmedContradiction)
        {
            if (claim.Category == ClaimCategory.Other ||
                judgment.OpposingReferences.Count == 0 ||
                judgment.OpposingReferences.Any(reference =>
                    !items.TryGetValue(reference, out var item) ||
                    !AuthoritativeForContradiction(item, claim.Category)))
                throw new InvalidDataException(
                    "A material contradiction requires category-appropriate positive opposing evidence.");
        }

        if (judgment.Outcome == ClaimVerificationOutcome.ConflictingEvidence)
        {
            // Two people disagreeing or two provisional recollections do not
            // automatically produce conflicting authoritative evidence.
            if (judgment.SupportingReferences.Concat(judgment.OpposingReferences)
                    .Any(reference => !items.TryGetValue(reference, out var item) ||
                                      item.IsSuperseded ||
                                      !AuthoritativeForConflict(item, claim.Category)))
                throw new InvalidDataException(
                    "Conflicting evidence requires non-superseded, lane-relevant sources.");
        }
    }

    private static bool AuthoritativeForContradiction(
        EvidenceItem item, ClaimCategory category)
    {
        if (item.IsSuperseded) return false;
        return category switch
        {
            ClaimCategory.SourceFact =>
                item.Kind is EvidenceItemKind.Briefing or EvidenceItemKind.Passage &&
                item.AuthorityTier == EvidenceAuthorityTier.SourceSnapshot,
            ClaimCategory.CharacterIdentity =>
                item.Kind == EvidenceItemKind.BioAnchor &&
                item.AuthorityTier == EvidenceAuthorityTier.BindingAnchor,
            ClaimCategory.PersonalHistory =>
                (item.Kind == EvidenceItemKind.BioAnchor &&
                 item.AuthorityTier == EvidenceAuthorityTier.BindingAnchor) ||
                (item.Kind == EvidenceItemKind.History &&
                 item.AuthorityTier == EvidenceAuthorityTier.EstablishedRecollection),
            ClaimCategory.DialogueAttribution =>
                item.Kind == EvidenceItemKind.Utterance &&
                item.AuthorityTier == EvidenceAuthorityTier.PreviouslyHeardUtterance,
            _ => false
        };
    }

    private static bool AuthoritativeForConflict(
        EvidenceItem item, ClaimCategory category)
    {
        if (item.IsSuperseded) return false;
        return category switch
        {
            ClaimCategory.SourceFact =>
                item.Kind is EvidenceItemKind.Briefing or EvidenceItemKind.Passage &&
                item.AuthorityTier == EvidenceAuthorityTier.SourceSnapshot,
            ClaimCategory.CharacterIdentity =>
                item.Kind == EvidenceItemKind.BioAnchor &&
                item.AuthorityTier == EvidenceAuthorityTier.BindingAnchor,
            ClaimCategory.PersonalHistory =>
                item.AuthorityTier is EvidenceAuthorityTier.BindingAnchor or
                    EvidenceAuthorityTier.EstablishedRecollection,
            _ => false // Dialogue is testimony, not two mutually binding sources.
        };
    }

    private static IReadOnlyList<string> ReadReferences(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Array || value.GetArrayLength() > 8)
            throw new InvalidDataException("Invalid list of evidence references.");
        var references = new List<string>();
        foreach (var v in value.EnumerateArray())
        {
            var text = v.GetString();
            if (string.IsNullOrWhiteSpace(text) || text.Length > 512)
                throw new InvalidDataException("Empty or excessive evidence reference.");
            references.Add(text);
        }
        return references.AsReadOnly();
    }

    private static void RequireProperties(JsonElement obj, params string[] names)
    {
        if (obj.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException("Expected a V4 structured JSON object.");
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var p in obj.EnumerateObject())
        {
            if (!seen.Add(p.Name) || !names.Contains(p.Name, StringComparer.Ordinal))
                throw new InvalidDataException("Unexpected or duplicate V4 JSON field.");
        }
        if (seen.Count != names.Length)
            throw new InvalidDataException("Missing required V4 JSON field.");
    }
}
