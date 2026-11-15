using System.Text;
using System.Text.Json;
using BusStop.Domain;
using BusStop.Models;
using BusStop.Persistence;

namespace BusStop.Memory;

/// <summary>Forms and retrieves subjective impressions. After a conversation ends, each participant makes
/// one private, bounded model call reflecting on what they now make of the others — an interpretation,
/// evidence-linked to that session. Impressions are private to their owner. Loading them is a plain query;
/// only forming them needs a model.</summary>
public sealed class ImpressionService
{
    private readonly ConversationStore _store;

    public ImpressionService(ConversationStore store) => _store = store;

    /// <summary>Generates and stores each participant's impressions of the others from a finished session.
    /// A reflection that fails to parse stores nothing for that participant and is otherwise harmless.</summary>
    public void Reflect(
        SessionId session, IModelClient model, IReadOnlyList<CharacterProfile> participants, Func<DateTimeOffset>? clock = null)
    {
        var now = (clock ?? (() => DateTimeOffset.UtcNow))();
        var sessionRecord = _store.GetSession(session);
        var briefing = sessionRecord is not null ? _store.GetBriefing(sessionRecord.Briefing) : null;
        var utterances = _store.GetCommittedUtterances(session);
        if (utterances.Count == 0) return;

        var nameById = participants.ToDictionary(p => p.Id, p => p.Name);
        var idByName = participants.ToDictionary(p => p.Name, p => p.Id);

        foreach (var self in participants)
        {
            var prompt = BuildReflectionPrompt(self, participants, briefing, utterances, nameById);
            var response = model.Decide(new ModelRequest(self.Id, prompt));
            if (response.Transport != ModelTransport.Responded || response.RawJson is null) continue;

            foreach (var (name, content) in ParseImpressions(response.RawJson))
            {
                if (!idByName.TryGetValue(name, out var subjectId)) continue;   // unknown name
                if (subjectId == self.Id) continue;                             // never about oneself
                if (string.IsNullOrWhiteSpace(content)) continue;
                _store.SaveImpression(new Impression(self.Id, subjectId, session, content.Trim(), now));
            }
        }
    }

    /// <summary>The owner's most recent impression of each of the given subjects — private to the owner.</summary>
    public IReadOnlyList<Impression> ImpressionsFor(CharacterId owner, IReadOnlyList<CharacterId> subjects)
    {
        var seen = new HashSet<CharacterId>();
        var result = new List<Impression>();
        foreach (var imp in _store.GetImpressions(owner)) // most recent first
        {
            if (imp.Subject == owner) continue;
            if (!subjects.Contains(imp.Subject)) continue;
            if (seen.Add(imp.Subject)) result.Add(imp);
        }

        return result;
    }

    private string BuildReflectionPrompt(
        CharacterProfile self, IReadOnlyList<CharacterProfile> participants, Briefing? briefing,
        IReadOnlyList<Utterance> utterances, IReadOnlyDictionary<CharacterId, string> nameById)
    {
        var others = participants.Where(p => p.Id != self.Id).Select(p => p.Name).ToList();
        var sb = new StringBuilder();
        sb.AppendLine($"You are {self.Name}. {self.Persona}");
        sb.AppendLine();
        sb.AppendLine($"You have just talked with {string.Join(" and ", others)}" +
                      (briefing is null ? "." : $" about \"{briefing.Title}\".") + " Here is what was said:");
        foreach (var u in utterances.OrderBy(u => u.Sequence))
        {
            var who = nameById.TryGetValue(u.Speaker, out var n) ? n : "Someone";
            sb.AppendLine($"{who}: {u.Text}");
        }
        sb.AppendLine();
        sb.AppendLine(
            "In one sentence each, what do you now make of the others — what kind of person does each seem " +
            "to be, and where do they stand? Reply only with a JSON object and nothing else: " +
            "{\"impressions\": [{\"of\": \"a name from the conversation\", \"impression\": \"one sentence\"}]}. " +
            "Include only the others, never yourself.");
        return sb.ToString();
    }

    private static IEnumerable<(string Name, string Content)> ParseImpressions(string rawJson)
    {
        JsonElement root;
        try
        {
            using var doc = JsonDocument.Parse(rawJson);
            root = doc.RootElement.Clone();
        }
        catch (JsonException)
        {
            yield break;
        }

        if (root.ValueKind != JsonValueKind.Object ||
            !root.TryGetProperty("impressions", out var arr) ||
            arr.ValueKind != JsonValueKind.Array)
            yield break;

        foreach (var item in arr.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object) continue;
            var name = item.TryGetProperty("of", out var ofEl) && ofEl.ValueKind == JsonValueKind.String ? ofEl.GetString() : null;
            var content = item.TryGetProperty("impression", out var impEl) && impEl.ValueKind == JsonValueKind.String ? impEl.GetString() : null;
            if (name is not null && content is not null)
                yield return (name, content);
        }
    }
}
