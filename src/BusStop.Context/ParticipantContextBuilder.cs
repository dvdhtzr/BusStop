using System.Text;
using BusStop.Domain;

namespace BusStop.Context;

/// <summary>Everything the builder is allowed to use for one participant's turn. By construction this
/// is the entire input surface: the participant's own profile, the public handles of everyone in the
/// room, the frozen briefing, the committed history, and the current opportunity. There is no field
/// for another participant's private identity, for uncommitted proposals, or for observer data, so
/// isolation is a property of the type, not of discipline.</summary>
public sealed record ContextInputs(
    CharacterProfile Self,
    IReadOnlyList<ParticipantHandle> AllParticipants,
    Briefing Briefing,
    IReadOnlyList<Utterance> CommittedHistory,
    SpeakingOpportunity Opportunity,
    IReadOnlyList<Recollection>? Recollections = null);

/// <summary>The result of building a context: the prompt to send, the participants this speaker may
/// address, the committed utterances it may reference, and who it is. The engine turns this into a
/// model request and a validation context, so the same authorization governs prompting and validation.</summary>
public sealed record BuiltContext(
    CharacterId Self,
    string Prompt,
    IReadOnlyList<ParticipantHandle> AddressableParticipants,
    IReadOnlySet<UtteranceId> VisibleUtteranceIds);

/// <summary>Builds a participant-specific prompt from authorized inputs only. It renders the committed
/// history by speaker name, bounded to a recent window, and never has access to anything outside
/// <see cref="ContextInputs"/>.</summary>
public sealed class ParticipantContextBuilder
{
    public const string DefaultCompetence =
        "Speak as this person, in their own voice. Pay attention to what was just said and to anyone who " +
        "addressed you. Do not restate a point you have already made: if your view has not changed and you " +
        "have nothing new to add, say so in a few words or stay silent. You can concede when someone is " +
        "right, build on what they said, bring in a specific memory or example, ask a genuine question, " +
        "change the subject, or simply pass — silence is a fine choice. Keep it short, usually a sentence " +
        "or two; you do not have to fill the air. If the conversation has clearly run its course — your " +
        "point is made, the others are agreeing, or there is nothing new to add — let it rest and pass " +
        "rather than restating. Do not announce your personality; let it show in what you say.";

    private readonly int _recentWindow;
    private readonly string _competence;

    public ParticipantContextBuilder(int recentWindow = 20, string? competence = null)
    {
        if (recentWindow < 1) throw new ArgumentOutOfRangeException(nameof(recentWindow));
        _recentWindow = recentWindow;
        _competence = competence ?? DefaultCompetence;
    }

    public BuiltContext Build(ContextInputs inputs)
    {
        var nameById = inputs.AllParticipants.ToDictionary(p => p.Id, p => p.Name);
        var others = inputs.AllParticipants.Where(p => p.Id != inputs.Self.Id).ToList();

        var recent = inputs.CommittedHistory
            .OrderBy(u => u.Sequence)
            .TakeLast(_recentWindow)
            .ToList();

        var sb = new StringBuilder();

        sb.AppendLine("# Who you are");
        sb.AppendLine($"{inputs.Self.Name}. {inputs.Self.Persona}");
        if (inputs.Self.Traits.Count > 0)
            sb.AppendLine($"Traits: {string.Join(", ", inputs.Self.Traits)}");
        sb.AppendLine();

        sb.AppendLine("# How to converse");
        sb.AppendLine(_competence);
        sb.AppendLine();

        sb.AppendLine("# The story you are discussing");
        sb.AppendLine($"{inputs.Briefing.Title} — {inputs.Briefing.Source}");
        sb.AppendLine(inputs.Briefing.Body);
        sb.AppendLine();

        var recollections = inputs.Recollections ?? Array.Empty<Recollection>();
        if (recollections.Count > 0)
        {
            sb.AppendLine("# When you last talked with them");
            sb.AppendLine("You have met these people before. Some of what was said then — recall it only if it is worth bringing up, do not simply repeat it:");
            foreach (var rec in recollections)
            {
                var who = nameById.TryGetValue(rec.Speaker, out var rn) ? rn : "Someone";
                sb.AppendLine($"- {who} said: {rec.Content}");
            }
            sb.AppendLine();
        }

        sb.AppendLine("# The conversation so far");
        if (recent.Count == 0)
        {
            sb.AppendLine("No one has spoken yet.");
        }
        else
        {
            foreach (var u in recent)
            {
                var speaker = nameById.TryGetValue(u.Speaker, out var n) ? n : "Someone";
                var addressed = u.Addressee is { } a && nameById.TryGetValue(a, out var an)
                    ? $" (to {an})"
                    : string.Empty;
                sb.AppendLine($"{speaker}{addressed}: {u.Text}");
            }
        }
        sb.AppendLine();

        sb.AppendLine("# Your turn");
        if (inputs.Opportunity.Reason == OpportunityReason.DirectedReply)
            sb.AppendLine("Someone has addressed you. You may answer, challenge the question, or decline — your choice.");
        else if (inputs.Opportunity.Reason == OpportunityReason.Opening)
            sb.AppendLine("You have just heard the story. React if you wish, or stay silent.");
        else
            sb.AppendLine("You have the floor if you want it.");

        var addressable = others.Count > 0 ? string.Join(", ", others.Select(o => o.Name)) : "no one";
        sb.AppendLine($"People you can address: {addressable}.");
        sb.AppendLine(
            "Reply with a single JSON object and nothing else: " +
            "{\"action\": \"speak\" or \"pass\", \"addressee\": a name from the list or null, \"text\": what you say or null}. " +
            "For a pass, use {\"action\": \"pass\"} with no other fields.");

        return new BuiltContext(
            inputs.Self.Id,
            sb.ToString(),
            others,
            recent.Select(u => u.Id).ToHashSet());
    }
}
