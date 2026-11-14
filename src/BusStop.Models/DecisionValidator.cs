using System.Text.Json;
using BusStop.Domain;

namespace BusStop.Models;

/// <summary>What the validator needs to judge a decision: the participants it may address (by the
/// handle the prompt presented, normally their name), who is deciding, and the committed utterances it
/// may reply to.</summary>
public sealed record ValidationContext(
    IReadOnlyDictionary<string, CharacterId> ParticipantsByHandle,
    CharacterId Self,
    IReadOnlySet<UtteranceId> CommittedUtteranceIds);

/// <summary>A decision after validation: its classified outcome and, when ValidSpeak, the resolved
/// fields. Every non-speak result carries a null payload.</summary>
public sealed record ValidatedDecision(
    AttemptOutcome Outcome,
    DecisionAction? Action,
    CharacterId? Addressee,
    UtteranceId? RepliesTo,
    string? Text);

/// <summary>Turns a <see cref="ModelResponse"/> into a classified <see cref="ValidatedDecision"/>. All
/// judgement lives here, in code, never in the model. A transport failure, a schema violation, and an
/// empty speak are each their own outcome and are never collapsed into a pass.</summary>
public static class DecisionValidator
{
    public static ValidatedDecision Validate(ModelResponse response, ValidationContext context)
    {
        switch (response.Transport)
        {
            case ModelTransport.Timeout:
                return Failure(AttemptOutcome.Timeout);
            case ModelTransport.TransportError:
                return Failure(AttemptOutcome.TransportError);
            case ModelTransport.Cancelled:
                return Failure(AttemptOutcome.Cancelled);
            case ModelTransport.Responded:
                break;
            default:
                return Failure(AttemptOutcome.InvalidSchema);
        }

        if (response.RawJson is null)
            return Failure(AttemptOutcome.InvalidSchema);

        JsonElement root;
        try
        {
            using var doc = JsonDocument.Parse(response.RawJson);
            root = doc.RootElement.Clone();
        }
        catch (JsonException)
        {
            return Failure(AttemptOutcome.InvalidSchema);
        }

        if (root.ValueKind != JsonValueKind.Object)
            return Failure(AttemptOutcome.InvalidSchema);

        var action = GetString(root, "action");
        if (action is null)
            return Failure(AttemptOutcome.InvalidSchema);

        var addressee = GetString(root, "addressee");
        var repliesTo = GetString(root, "replies_to");
        var text = GetString(root, "text");

        if (string.Equals(action, "pass", StringComparison.OrdinalIgnoreCase))
        {
            // A pass must carry no payload; a pass with content is a schema violation, not a pass.
            if (addressee is not null || repliesTo is not null || !string.IsNullOrEmpty(text))
                return Failure(AttemptOutcome.InvalidSchema);
            return new ValidatedDecision(AttemptOutcome.ValidPass, DecisionAction.Pass, null, null, null);
        }

        if (!string.Equals(action, "speak", StringComparison.OrdinalIgnoreCase))
            return Failure(AttemptOutcome.InvalidSchema);

        if (string.IsNullOrWhiteSpace(text))
            return Failure(AttemptOutcome.EmptySpeak);

        CharacterId? resolvedAddressee = null;
        if (addressee is not null)
        {
            if (!context.ParticipantsByHandle.TryGetValue(addressee, out var id))
                return Failure(AttemptOutcome.InvalidSchema);
            if (id == context.Self)
                return Failure(AttemptOutcome.InvalidSchema);
            resolvedAddressee = id;
        }

        UtteranceId? resolvedReply = null;
        if (repliesTo is not null)
        {
            if (!Guid.TryParse(repliesTo, out var guid))
                return Failure(AttemptOutcome.InvalidSchema);
            var uid = new UtteranceId(guid);
            if (!context.CommittedUtteranceIds.Contains(uid))
                return Failure(AttemptOutcome.InvalidSchema);
            resolvedReply = uid;
        }

        return new ValidatedDecision(AttemptOutcome.ValidSpeak, DecisionAction.Speak, resolvedAddressee, resolvedReply, text);
    }

    private static ValidatedDecision Failure(AttemptOutcome outcome) =>
        new(outcome, null, null, null, null);

    private static string? GetString(JsonElement obj, string name)
    {
        if (!obj.TryGetProperty(name, out var value)) return null;
        return value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    }
}
