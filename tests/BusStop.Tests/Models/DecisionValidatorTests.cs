using BusStop.Domain;
using BusStop.Models;

namespace BusStop.Tests.Models;

public class DecisionValidatorTests
{
    private static readonly CharacterId Arthur = CharacterId.New();
    private static readonly CharacterId Maya = CharacterId.New();
    private static readonly CharacterId Daniel = CharacterId.New();

    private static ValidationContext Context(params UtteranceId[] committed) => new(
        new Dictionary<string, CharacterId> { ["Arthur"] = Arthur, ["Maya"] = Maya, ["Daniel"] = Daniel },
        Self: Arthur,
        committed.ToHashSet());

    [Fact]
    public void Scripted_pass_records_ValidPass()
    {
        var result = DecisionValidator.Validate(StubResponses.Pass(), Context());
        Assert.Equal(AttemptOutcome.ValidPass, result.Outcome);
        Assert.Equal(DecisionAction.Pass, result.Action);
        Assert.Null(result.Text);
        Assert.Null(result.Addressee);
    }

    [Fact]
    public void Malformed_response_records_InvalidSchema_not_pass()
    {
        var result = DecisionValidator.Validate(StubResponses.Malformed(), Context());
        Assert.Equal(AttemptOutcome.InvalidSchema, result.Outcome);
        Assert.NotEqual(AttemptOutcome.ValidPass, result.Outcome);
        Assert.True(OutcomeClassification.IsFailure(result.Outcome));
    }

    [Fact]
    public void Empty_speak_records_EmptySpeak_not_pass()
    {
        var result = DecisionValidator.Validate(StubResponses.EmptySpeak(), Context());
        Assert.Equal(AttemptOutcome.EmptySpeak, result.Outcome);
        Assert.True(OutcomeClassification.IsFailure(result.Outcome));
        Assert.False(OutcomeClassification.IsVoluntarySilence(result.Outcome));
    }

    [Fact]
    public void Timeout_records_Timeout_not_pass()
    {
        var result = DecisionValidator.Validate(StubResponses.Timeout(), Context());
        Assert.Equal(AttemptOutcome.Timeout, result.Outcome);
        Assert.True(OutcomeClassification.IsFailure(result.Outcome));
    }

    [Fact]
    public void Transport_error_records_TransportError_not_pass()
    {
        var result = DecisionValidator.Validate(StubResponses.TransportError(), Context());
        Assert.Equal(AttemptOutcome.TransportError, result.Outcome);
        Assert.True(OutcomeClassification.IsFailure(result.Outcome));
    }

    [Fact]
    public void Cancelled_records_Cancelled_not_pass()
    {
        var result = DecisionValidator.Validate(StubResponses.Cancelled(), Context());
        Assert.Equal(AttemptOutcome.Cancelled, result.Outcome);
        Assert.True(OutcomeClassification.IsFailure(result.Outcome));
    }

    [Fact]
    public void Valid_speak_with_addressee_and_reply_is_parsed()
    {
        var earlier = UtteranceId.New();
        var result = DecisionValidator.Validate(
            StubResponses.Speak("I'd put it differently.", addressee: "Maya", repliesTo: earlier.ToString()),
            Context(earlier));

        Assert.Equal(AttemptOutcome.ValidSpeak, result.Outcome);
        Assert.Equal(DecisionAction.Speak, result.Action);
        Assert.Equal(Maya, result.Addressee);
        Assert.Equal(earlier, result.RepliesTo);
        Assert.Equal("I'd put it differently.", result.Text);
    }

    [Fact]
    public void Valid_speak_without_addressee_is_allowed()
    {
        var result = DecisionValidator.Validate(StubResponses.Speak("Just thinking aloud."), Context());
        Assert.Equal(AttemptOutcome.ValidSpeak, result.Outcome);
        Assert.Null(result.Addressee);
        Assert.Null(result.RepliesTo);
    }

    [Fact]
    public void Unknown_addressee_is_invalid_schema()
    {
        var result = DecisionValidator.Validate(StubResponses.Speak("Hi", addressee: "Nobody"), Context());
        Assert.Equal(AttemptOutcome.InvalidSchema, result.Outcome);
    }

    [Fact]
    public void Addressing_self_is_invalid_schema()
    {
        // Self is Arthur in the context.
        var result = DecisionValidator.Validate(StubResponses.Speak("Hi", addressee: "Arthur"), Context());
        Assert.Equal(AttemptOutcome.InvalidSchema, result.Outcome);
    }

    [Fact]
    public void Reply_to_unknown_utterance_is_invalid_schema()
    {
        var result = DecisionValidator.Validate(
            StubResponses.Speak("As you said", addressee: "Maya", repliesTo: Guid.NewGuid().ToString()),
            Context()); // no committed utterances
        Assert.Equal(AttemptOutcome.InvalidSchema, result.Outcome);
    }

    [Fact]
    public void Pass_carrying_text_is_invalid_schema()
    {
        var sneaky = ModelResponse.Responded("{\"action\":\"pass\",\"text\":\"actually...\"}");
        var result = DecisionValidator.Validate(sneaky, Context());
        Assert.Equal(AttemptOutcome.InvalidSchema, result.Outcome);
    }

    [Fact]
    public void Stub_client_returns_scripted_responses_in_order_then_refuses()
    {
        var client = new StubModelClient(new[] { StubResponses.Pass(), StubResponses.Speak("hi") });
        var r1 = client.Decide(new ModelRequest(Arthur, "prompt"));
        var r2 = client.Decide(new ModelRequest(Arthur, "prompt"));
        Assert.Equal(ModelTransport.Responded, r1.Transport);
        Assert.Equal(ModelTransport.Responded, r2.Transport);
        Assert.Equal(0, client.Remaining);
        Assert.Throws<InvalidOperationException>(() => client.Decide(new ModelRequest(Arthur, "prompt")));
    }
}
