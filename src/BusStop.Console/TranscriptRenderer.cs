using System.Text;
using System.Text.Json;
using BusStop.Domain;
using BusStop.Engine;
using BusStop.Persistence;

namespace BusStop.Console;

/// <summary>Renders the two observer artifacts from durable state: a readable Markdown transcript and a
/// JSON diagnostics sidecar. The transcript is the centerpiece; the diagnostics report technical
/// integrity, kept separate so one is never read as the other.</summary>
public static class TranscriptRenderer
{
    public static string RenderTranscript(ConversationStore store, SessionId session, IReadOnlyList<CharacterProfile> cast)
    {
        var nameById = cast.ToDictionary(c => c.Id, c => c.Name);
        var sb = new StringBuilder();

        sb.AppendLine("# BusStop transcript");
        sb.AppendLine();
        sb.AppendLine($"**Story:** {Laboratory.Story.Title} — {Laboratory.Story.Source}  ");
        sb.AppendLine($"**Source:** {Laboratory.Story.Url}  ");
        sb.AppendLine($"**Participants:** {string.Join(", ", cast.Select(c => c.Name))}");
        sb.AppendLine();
        sb.AppendLine("---");
        sb.AppendLine();

        var committed = store.GetCommittedUtterances(session);
        if (committed.Count == 0)
        {
            sb.AppendLine("_No one spoke._");
        }
        else
        {
            foreach (var u in committed)
            {
                var speaker = nameById.GetValueOrDefault(u.Speaker, "Someone");
                var to = u.Addressee is { } a && nameById.TryGetValue(a, out var an) ? $" _(to {an})_" : string.Empty;
                sb.AppendLine($"**{speaker}**{to}: {u.Text}");
                sb.AppendLine();
            }
        }

        return sb.ToString();
    }

    public static string RenderDiagnostics(ConversationStore store, SessionId session, RunResult result, string modelName, string modelDigest)
    {
        var sessionRecord = store.GetSession(session);
        var committed = store.GetCommittedUtterances(session);
        var attempts = store.GetAttempts(session);

        var outcomeCounts = attempts
            .GroupBy(a => a.Outcome)
            .ToDictionary(g => g.Key.ToString(), g => g.Count());

        var sequences = committed.Select(u => u.Sequence).ToArray();
        var contiguous = sequences.Select((s, i) => s == i + 1).All(x => x);

        var model = new
        {
            session = session.ToString(),
            termination = result.Reason.ToString(),
            opportunities = result.Opportunities,
            modelCalls = result.ModelCalls,
            committedUtterances = committed.Count,
            sequenceContiguous = contiguous,
            outcomes = outcomeCounts,
            config = new
            {
                model = modelName,
                digest = modelDigest,
                mode = sessionRecord?.Mode.ToString(),
                protocolVersion = sessionRecord?.ProtocolVersion,
                contextVersion = sessionRecord?.ContextVersion
            }
        };

        return JsonSerializer.Serialize(model, new JsonSerializerOptions { WriteIndented = true });
    }
}
