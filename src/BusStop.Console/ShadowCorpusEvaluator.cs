using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using BusStop.Moderation;

namespace BusStop.Console;

/// <summary>Evaluate a PRIVATE original audit against the versioned provisional
/// fingerprint manifest. No model call and no data uploaded.</summary>
public static class ShadowCorpusEvaluator
{
    public static object Evaluate(string auditJson, string labelsJson)
    {
        using var audit = JsonDocument.Parse(auditJson);
        using var labels = JsonDocument.Parse(labelsJson);
        var a = audit.RootElement;
        var l = labels.RootElement;
        var session = a.GetProperty("session").GetString()
            ?? throw new InvalidDataException("Missing audit session.");
        if (session != l.GetProperty("sourceSession").GetString())
            throw new InvalidDataException("Audit session does not match the label corpus.");
        if (l.GetProperty("corpusVersion").GetString() != "faa-shadow-v1")
            throw new InvalidDataException("Unknown corpus version.");
        var cases = l.GetProperty("labels").EnumerateArray().ToArray();
        var reviews = a.GetProperty("reviews").EnumerateArray().ToArray();
        if (cases.Length != 33 || reviews.Length != cases.Length)
            throw new InvalidDataException("FAA corpus must contain all 33 historical reviews.");

        int reviewed = 0, invalid = 0, incompleteJson = 0;
        int allowed = 0, flaggedAllow = 0, revisionAllow = 0;
        int uncertain = 0, exact = 0, exactDetected = 0;
        for (var i = 0; i < reviews.Length; i++)
        {
            var review = reviews[i];
            var item = cases[i];
            if (item.GetProperty("ordinal").GetInt32() != i + 1)
                throw new InvalidDataException($"Corpus ordinal mismatch at {i + 1}.");
            var text = review.GetProperty("Candidate").GetProperty("Text").GetString()
                ?? throw new InvalidDataException("Missing candidate text.");
            var digest = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)))
                .ToLowerInvariant();
            if (digest != item.GetProperty("candidateTextSha256").GetString())
                throw new InvalidDataException($"Candidate fingerprint mismatch at {i + 1}.");

            var status = review.GetProperty("Status").GetInt32();
            if (!Enum.IsDefined(typeof(ShadowReviewStatus), status))
                throw new InvalidDataException("Unknown historical shadow status.");
            var valid = status == (int)ShadowReviewStatus.Reviewed;
            if (valid) reviewed++;
            else invalid++;
            var failure = review.GetProperty("FailureDetail");
            var reason = failure.ValueKind == JsonValueKind.String
                ? failure.GetString() ?? string.Empty : string.Empty;
            if (reason.StartsWith("Expected end of string", StringComparison.Ordinal) ||
                reason.StartsWith("Expected depth to be zero", StringComparison.Ordinal))
                incompleteJson++;

            var judgment = review.GetProperty("Judgment");
            var hasFinding = judgment.ValueKind == JsonValueKind.Object &&
                judgment.GetProperty("Finding").ValueKind == JsonValueKind.Object;
            var isRevision = judgment.ValueKind == JsonValueKind.Object &&
                judgment.GetProperty("Disposition").GetInt32() ==
                    (int)ModerationDisposition.RequestRevision;

            switch (item.GetProperty("expected").GetString())
            {
                case "Allow":
                    allowed++;
                    if (valid && hasFinding) flaggedAllow++;
                    if (valid && isRevision) revisionAllow++;
                    break;
                case "NeedsHumanReview":
                    uncertain++;
                    break;
                case "ExactCopyOther":
                case "ExactCopySelf":
                    exact++;
                    if (valid && hasFinding &&
                        judgment.GetProperty("Finding").GetProperty("Kind").GetInt32() ==
                            (int)ModerationIssueKind.ExactDuplicate)
                        exactDetected++;
                    break;
                default:
                    throw new InvalidDataException("Unknown provisional corpus label.");
            }
        }

        return new
        {
            corpusVersion = "faa-shadow-v1",
            labelStatus = l.GetProperty("status").GetString(),
            historicalOnly = true,
            sourceSession = session,
            reviewCount = reviews.Length,
            structuredValid = reviewed,
            structuredInvalid = invalid,
            incompleteJson,
            provisionalAllowCases = allowed,
            falseFindingsOnProvisionalAllow = flaggedAllow,
            falseRevisionsOnProvisionalAllow = revisionAllow,
            needsHumanReview = uncertain,
            exactCopyCases = exact,
            exactCopyRecognizedInHistoricalAudit = exactDetected,
            warning = "Only historical behavior is scored. Labels await owner review; new M1a behavior is not evaluated."
        };
    }
}
