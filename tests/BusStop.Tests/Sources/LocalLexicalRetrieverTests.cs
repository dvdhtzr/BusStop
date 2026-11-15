using BusStop.Domain;
using BusStop.Sources;

namespace BusStop.Tests.Sources;

public sealed class LocalLexicalRetrieverTests
{
    private static EvidenceSnapshot Source()
    {
        var sections = new[]
        {
            "Control towers rely on reliable communications and radar feeds.",
            "Airline passengers waited at departure gates during a nationwide ground stop.",
            "The maintenance contractor was installing fiber optic connections instead of copper.",
            "Network routers generated excessive connection attempts during installation.",
            "Controllers kept the aircraft already in the sky moving safely to landing.",
            "Passenger refunds and rebooking policies became a difficult practical question.",
            "Airline crews have strict duty time limits after schedule disruption.",
            "Congress debated infrastructure modernization and accountability for upgrades.",
            "Investigators had not established if hardware failure or human error caused the incident."
        };
        var text = string.Join(" ", sections.Select((s, i) => $"Section {i + 1}: {s} " + new string((char)('A' + i), 220)));
        return EvidenceSnapshotFactory.Capture(
            new EvidenceSourceDescriptor(EvidenceSourceKind.UserDocument,
                "FAA nationwide grounding", "Local test", null, null, "Synthetic unit-test fixture"),
            text, passageCharacters: 250, overlapCharacters: 20);
    }

    [Fact]
    public void Rare_query_terms_find_relevant_passages_beyond_the_first_four()
    {
        var snapshot = Source();
        Assert.True(snapshot.Passages.Count > 6);
        var result = LocalLexicalRetriever.Retrieve(
            snapshot, "What about passenger refunds and rebooking?", Array.Empty<string>());

        Assert.Contains("refunds", result.Passages[0].Text);
        Assert.True(result.Passages[0].Ordinal > 4);
        Assert.Equal(3, result.Passages.Count);
        Assert.Equal(3, result.Scores.Count);
        Assert.All(result.Scores, score => Assert.True(double.IsFinite(score) && score >= 0));
    }

    [Fact]
    public void Identical_inputs_are_deterministic_and_novelty_progresses_through_source()
    {
        var snapshot = Source();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var first = LocalLexicalRetriever.Retrieve(snapshot, "aircraft", seen);
        var repeated = LocalLexicalRetriever.Retrieve(snapshot, "aircraft", seen);
        Assert.Equal(first.Passages.Select(p => p.PassageId),
            repeated.Passages.Select(p => p.PassageId));

        // Even if the conversation doesn't change subject, at least one
        // previously unseen passage is offered on every turn until coverage.
        for (var i = 0; i < snapshot.Passages.Count; i++)
        {
            var retrieved = LocalLexicalRetriever.Retrieve(snapshot, "aircraft", seen);
            foreach (var passage in retrieved.Passages) seen.Add(passage.PassageId);
        }
        Assert.Equal(snapshot.Passages.Count, seen.Count);
    }

    [Fact]
    public void Retriever_enforces_a_strict_passage_budget()
    {
        var source = Source();
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            LocalLexicalRetriever.Retrieve(source, "test", Array.Empty<string>(), maxPassages: 4));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            LocalLexicalRetriever.Retrieve(source, "test", Array.Empty<string>(), maxPassages: 0));
    }
}
