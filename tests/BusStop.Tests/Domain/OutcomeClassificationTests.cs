using BusStop.Domain;

namespace BusStop.Tests.Domain;

public class OutcomeClassificationTests
{
    [Fact]
    public void Every_outcome_is_classified_without_throwing()
    {
        foreach (AttemptOutcome outcome in Enum.GetValues<AttemptOutcome>())
        {
            // Must not throw: an unclassified outcome is a bug the switch surfaces here.
            _ = OutcomeClassification.Classify(outcome);
        }
    }

    [Fact]
    public void ValidPass_is_silent_and_never_a_failure()
    {
        Assert.Equal(OutcomeCategory.Silent, OutcomeClassification.Classify(AttemptOutcome.ValidPass));
        Assert.True(OutcomeClassification.IsVoluntarySilence(AttemptOutcome.ValidPass));
        Assert.False(OutcomeClassification.IsFailure(AttemptOutcome.ValidPass));
    }

    [Theory]
    [InlineData(AttemptOutcome.InvalidSchema)]
    [InlineData(AttemptOutcome.EmptySpeak)]
    [InlineData(AttemptOutcome.Timeout)]
    [InlineData(AttemptOutcome.TransportError)]
    [InlineData(AttemptOutcome.Cancelled)]
    public void Failure_outcomes_are_never_silence_or_speech(AttemptOutcome outcome)
    {
        Assert.Equal(OutcomeCategory.Failed, OutcomeClassification.Classify(outcome));
        Assert.True(OutcomeClassification.IsFailure(outcome));
        Assert.False(OutcomeClassification.IsVoluntarySilence(outcome));
        Assert.False(OutcomeClassification.ProducesUtterance(outcome));
    }

    [Fact]
    public void EchoedSelf_is_suppressed_and_neither_speech_silence_nor_failure()
    {
        Assert.Equal(OutcomeCategory.Suppressed, OutcomeClassification.Classify(AttemptOutcome.EchoedSelf));
        Assert.False(OutcomeClassification.ProducesUtterance(AttemptOutcome.EchoedSelf));
        Assert.False(OutcomeClassification.IsVoluntarySilence(AttemptOutcome.EchoedSelf));
        Assert.False(OutcomeClassification.IsFailure(AttemptOutcome.EchoedSelf));
    }

    [Fact]
    public void Only_valid_speak_produces_an_utterance()
    {
        Assert.True(OutcomeClassification.ProducesUtterance(AttemptOutcome.ValidSpeak));

        var producing = Enum.GetValues<AttemptOutcome>()
            .Where(OutcomeClassification.ProducesUtterance)
            .ToArray();

        Assert.Equal(new[] { AttemptOutcome.ValidSpeak }, producing);
    }

    [Fact]
    public void Exactly_one_outcome_is_silent()
    {
        var silent = Enum.GetValues<AttemptOutcome>()
            .Where(o => OutcomeClassification.Classify(o) == OutcomeCategory.Silent)
            .ToArray();

        Assert.Equal(new[] { AttemptOutcome.ValidPass }, silent);
    }
}
