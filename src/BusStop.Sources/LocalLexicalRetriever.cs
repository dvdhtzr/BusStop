using System.Text.RegularExpressions;
using BusStop.Domain;

namespace BusStop.Sources;

/// <summary>Inspectable, deterministic local lexical retrieval over a frozen
/// document snapshot. No embeddings or external API calls. One retrieval is
/// performed for each offered speaking turn; exploratory coverage prevents
/// repeatedly showing only the document's opening paragraphs.</summary>
public static partial class LocalLexicalRetriever
{
    public const string Version = "lexical-overlap-v1";
    public const int MaxPassagesPerTurn = 3;
    public const int MaxQueryCharacters = 1024;

    private static readonly HashSet<string> Stopwords = new(StringComparer.Ordinal)
    {
        "about", "after", "again", "also", "been", "before", "being",
        "between", "could", "does", "from", "have", "here", "into",
        "just", "more", "most", "only", "other", "over", "said",
        "same", "some", "such", "than", "that", "their", "them",
        "then", "there", "these", "they", "this", "those", "through",
        "very", "were", "what", "when", "where", "which", "while",
        "will", "with", "would", "your", "should", "right", "really"
    };

    public static LocalRetrievalResult Retrieve(
        EvidenceSnapshot snapshot,
        string query,
        IReadOnlyCollection<string> previouslyExposedIds,
        int maxPassages = MaxPassagesPerTurn)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(previouslyExposedIds);
        if (snapshot.Passages.Count == 0)
            throw new InvalidOperationException("Cannot retrieve from an empty snapshot.");
        if (maxPassages < 1 || maxPassages > MaxPassagesPerTurn)
            throw new ArgumentOutOfRangeException(nameof(maxPassages));
        if (query.Length > MaxQueryCharacters)
            query = query[..MaxQueryCharacters];

        var exposed = previouslyExposedIds.ToHashSet(StringComparer.Ordinal);
        var queryTerms = Tokens(query);
        var passageTerms = snapshot.Passages
            .Select(p => Tokens(p.Text))
            .ToArray();

        // Inverse document frequency discounts tokens scattered throughout
        // the whole source. Exact token matching is deliberate: the first
        // baseline is explainable and its limitations measurable.
        var frequencies = queryTerms.ToDictionary(
            word => word,
            word => passageTerms.Count(terms => terms.Contains(word)),
            StringComparer.Ordinal);

        var ranked = snapshot.Passages.Select((passage, index) =>
        {
            var score = queryTerms.Sum(term =>
                passageTerms[index].Contains(term)
                    ? Math.Log(1d + snapshot.Passages.Count / (1d + frequencies[term]))
                    : 0d);
            return (Passage: passage, Score: score);
        })
        .OrderByDescending(x => x.Score)
        .ThenBy(x => exposed.Contains(x.Passage.PassageId) ? 1 : 0)
        .ThenBy(x => x.Passage.Ordinal)
        .ToList();

        var chosen = ranked.Take(Math.Min(2, maxPassages)).ToList();
        if (chosen.Count < maxPassages)
        {
            // Spend one of three slots on a passage not exposed on earlier
            // turns, even if the characters keep talking about one detail.
            var novel = ranked.Where(x =>
                !exposed.Contains(x.Passage.PassageId) &&
                chosen.All(y => y.Passage.PassageId != x.Passage.PassageId))
                .OrderBy(x => x.Passage.Ordinal)
                .FirstOrDefault();

            if (novel.Passage is not null)
                chosen.Add(novel);
        }
        foreach (var result in ranked)
        {
            if (chosen.Count >= maxPassages) break;
            if (chosen.All(x => x.Passage.PassageId != result.Passage.PassageId))
                chosen.Add(result);
        }

        return new LocalRetrievalResult(
            Version,
            query,
            Array.AsReadOnly(chosen.Select(x => x.Passage).ToArray()),
            Array.AsReadOnly(chosen.Select(x => x.Score).ToArray()));
    }

    private static HashSet<string> Tokens(string text) =>
        Word().Matches(text.ToLowerInvariant())
            .Select(match => match.Value)
            .Where(token => token.Length >= 3 && !Stopwords.Contains(token))
            .ToHashSet(StringComparer.Ordinal);

    [GeneratedRegex(@"[\p{L}\p{Nd}]+", RegexOptions.CultureInvariant)]
    private static partial Regex Word();
}

/// <summary>The retrieved passages are the only source text to expose to a
/// model on this turn. Scores and query are recorded locally for audit.</summary>
public sealed record LocalRetrievalResult(
    string Version,
    string Query,
    IReadOnlyList<EvidencePassage> Passages,
    IReadOnlyList<double> Scores);
