using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using BusStop.Domain;

namespace BusStop.Context;

/// <summary>
/// Offline-only V7d-4a context selection. No ConversationEngine integration,
/// moderator authority, model calls, prompt edits, or persisted session policy.
/// </summary>
public enum V7d4aContextVariant
{
    LegacyRecent20,
    Recent8,
    Selective6Plus2
}

public sealed record V7d4aAnchor(long Sequence, string Reason);

/// <summary>
/// Private selection: contains original committed utterances by reference.
/// Never export Utterances in a safe aggregate.
/// </summary>
public sealed record V7d4aContextSelection(
    string ContractVersion,
    V7d4aContextVariant Variant,
    IReadOnlyList<Utterance> Utterances,
    IReadOnlyList<V7d4aAnchor> Anchors,
    bool ReplyReferenceVisible,
    string SelectionSha256);

public static class V7d4aOfflineContextSelector
{
    public const string ContractVersion = "busstop-v7d-4a-selection-v1";
    private const int RecentSelective = 6;
    private const int MaxAnchors = 2;
    private const int EarlierCandidateLookback = 14;

    private static readonly Regex Words = new(
        @"[\p{L}\p{N}]+", RegexOptions.Compiled |
        RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(250));

    public static V7d4aContextSelection Select(
        IReadOnlyList<Utterance> committedBeforeOpportunity,
        UtteranceId? replyReference,
        V7d4aContextVariant variant)
    {
        ArgumentNullException.ThrowIfNull(committedBeforeOpportunity);
        if (!Enum.IsDefined(variant) || committedBeforeOpportunity.Count > 2000)
            throw new InvalidDataException("Invalid V7d-4a selection boundary.");

        // The input is the full, committed, chronological session history at
        // the pre-opportunity boundary. No look-ahead or cross-session rows.
        for (int i = 0; i < committedBeforeOpportunity.Count; i++)
        {
            var current = committedBeforeOpportunity[i];
            if (current is null || current.Sequence != i + 1 ||
                current.Text is null || current.Text.Length > 20000 ||
                current.Session != committedBeforeOpportunity[0].Session ||
                committedBeforeOpportunity.Take(i).Any(x => x.Id == current.Id))
                throw new InvalidDataException(
                    "V7d-4a requires ordered, unique, single-session committed history.");
        }

        var reply = replyReference is null
            ? null
            : committedBeforeOpportunity.SingleOrDefault(x => x.Id == replyReference);
        if (replyReference is not null && reply is null)
            throw new InvalidDataException(
                "Reply reference is not visible at the committed boundary.");

        var recentLimit = variant switch
        {
            V7d4aContextVariant.LegacyRecent20 => 20,
            V7d4aContextVariant.Recent8 => 8,
            V7d4aContextVariant.Selective6Plus2 => RecentSelective,
            _ => throw new InvalidDataException("Unrecognized context variant.")
        };

        var recent = committedBeforeOpportunity.TakeLast(recentLimit).ToList();
        var chosen = new List<Utterance>(recent);
        var anchors = new List<V7d4aAnchor>();

        if (variant == V7d4aContextVariant.Selective6Plus2)
        {
            // Directed reply is first priority. It may be older than the
            // previous 20 lines; that exception must be traceable.
            if (reply is not null && chosen.All(x => x.Id != reply.Id))
            {
                chosen.Add(reply);
                anchors.Add(new V7d4aAnchor(
                    reply.Sequence, "RequiredCommittedReplyReference"));
            }

            var recentSpeakers = recent.Select(x => x.Speaker).ToHashSet();
            var anchorSpeakers = new HashSet<CharacterId>(
                anchors.Select(a => chosen.Single(x => x.Sequence == a.Sequence).Speaker));
            var recentSets = recent.Select(x => Tokenize(x.Text)).ToArray();

            // The elective pool is no older than the original 20-turn window.
            // A mandatory reply reference alone may cross that boundary.
            var earlierPool = committedBeforeOpportunity
                .Take(Math.Max(0, committedBeforeOpportunity.Count - RecentSelective))
                .TakeLast(EarlierCandidateLookback)
                .Where(x => chosen.All(c => c.Id != x.Id))
                .Select(x =>
                {
                    var tokens = Tokenize(x.Text);
                    var maxSimilarity = recentSets.Length == 0
                        ? 0.0 : recentSets.Max(y => Similarity(tokens, y));
                    return new
                    {
                        Turn = x,
                        TokenCount = tokens.Count,
                        Similarity = maxSimilarity
                    };
                })
                .Where(x => x.TokenCount >= 4 && x.Similarity < 0.80)
                .ToArray();

            while (anchors.Count < MaxAnchors)
            {
                var candidate = earlierPool
                    .Where(x => chosen.All(c => c.Id != x.Turn.Id))
                    .OrderByDescending(x =>
                        !recentSpeakers.Contains(x.Turn.Speaker))
                    .ThenByDescending(x =>
                        !anchorSpeakers.Contains(x.Turn.Speaker))
                    // Prefer a related but not near-duplicated statement.
                    .ThenByDescending(x => x.Similarity)
                    .ThenByDescending(x => x.Turn.Sequence)
                    .FirstOrDefault();

                if (candidate is null) break;

                var absent = !recentSpeakers.Contains(candidate.Turn.Speaker);
                chosen.Add(candidate.Turn);
                anchors.Add(new V7d4aAnchor(
                    candidate.Turn.Sequence,
                    absent ? "UnderrepresentedSpeaker" : "DistinctEarlierUtterance"));
                anchorSpeakers.Add(candidate.Turn.Speaker);
            }
        }

        chosen.Sort((a, b) => a.Sequence.CompareTo(b.Sequence));
        if (chosen.Count > (variant == V7d4aContextVariant.LegacyRecent20 ? 20 : 8) ||
            chosen.Select(x => x.Id).Distinct().Count() != chosen.Count ||
            chosen.Any(x => !committedBeforeOpportunity.Contains(x)) ||
            variant == V7d4aContextVariant.Selective6Plus2 &&
                recent.Any(x => chosen.All(c => c.Id != x.Id)))
            throw new InvalidDataException("Invalid selected subset.");

        var digestInput = new StringBuilder();
        foreach (var turn in chosen)
        {
            digestInput.Append(turn.Sequence.ToString(CultureInfo.InvariantCulture))
                .Append('|').Append(turn.Id).Append('|')
                .Append(Sha(turn.Text)).Append('\n');
        }

        return new V7d4aContextSelection(
            ContractVersion, variant, Array.AsReadOnly(chosen.ToArray()),
            Array.AsReadOnly(anchors.ToArray()),
            replyReference is null || chosen.Any(x => x.Id == replyReference),
            Sha(digestInput.ToString()));
    }

    /// <summary>
    /// Render a model-free comparison using the SAME existing builder,
    /// persona, briefing, recollections and competence. The caller must
    /// provide the builder configured for the historical session.
    /// </summary>
    public static BuiltContext RenderPreview(
        ContextInputs original, V7d4aContextSelection selection,
        ParticipantContextBuilder originalBuilder)
    {
        ArgumentNullException.ThrowIfNull(original);
        ArgumentNullException.ThrowIfNull(selection);
        ArgumentNullException.ThrowIfNull(originalBuilder);
        if (selection.ContractVersion != ContractVersion ||
            selection.Utterances.Any(x =>
                !original.CommittedHistory.Any(o =>
                    o.Id == x.Id && o.Sequence == x.Sequence &&
                    o.Text == x.Text && o.Session == x.Session)))
            throw new InvalidDataException("Selection is foreign to authorized context.");

        // Caller provides the original builder; the subset never exceeds
        // eight/twenty, so the existing recent-window semantics are retained.
        var built = originalBuilder.Build(original with
        {
            CommittedHistory = selection.Utterances
        });
        if (!selection.Utterances.All(x => built.VisibleUtteranceIds.Contains(x.Id)) ||
            built.VisibleUtteranceIds.Count != selection.Utterances.Count)
            throw new InvalidDataException(
                "Selected context was silently truncated by the supplied builder.");
        return built;
    }

    private static HashSet<string> Tokenize(string text) =>
        Words.Matches(text.ToLowerInvariant()).Cast<Match>()
            .Select(x => x.Value).ToHashSet(StringComparer.Ordinal);

    private static double Similarity(
        HashSet<string> left, HashSet<string> right)
    {
        if (left.Count == 0 && right.Count == 0) return 1;
        var common = left.Count(x => right.Contains(x));
        var total = left.Count + right.Count - common;
        return total == 0 ? 0 : (double)common / total;
    }

    private static string Sha(string text) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)))
            .ToLowerInvariant();
}
