namespace BusStop.Domain;

/// <summary>Maps every <see cref="AttemptOutcome"/> to a category. The switch is exhaustive and
/// throws on an unhandled value, so adding a new outcome without deciding its category is a
/// compile-visible, test-caught error rather than a silent reclassification.</summary>
public static class OutcomeClassification
{
    public static OutcomeCategory Classify(AttemptOutcome outcome) => outcome switch
    {
        AttemptOutcome.ValidSpeak => OutcomeCategory.Spoke,
        AttemptOutcome.ValidPass => OutcomeCategory.Silent,
        AttemptOutcome.InvalidSchema => OutcomeCategory.Failed,
        AttemptOutcome.EmptySpeak => OutcomeCategory.Failed,
        AttemptOutcome.Timeout => OutcomeCategory.Failed,
        AttemptOutcome.TransportError => OutcomeCategory.Failed,
        AttemptOutcome.Cancelled => OutcomeCategory.Failed,
        AttemptOutcome.EchoedSelf => OutcomeCategory.Suppressed,
        _ => throw new ArgumentOutOfRangeException(
            nameof(outcome), outcome, "Unclassified AttemptOutcome; every outcome must be categorized explicitly.")
    };

    /// <summary>True only for a deliberate, valid silence. A failure is never a pass.</summary>
    public static bool IsVoluntarySilence(AttemptOutcome outcome) => Classify(outcome) == OutcomeCategory.Silent;

    /// <summary>True for any technical failure. Such an outcome must not be delivered as speech
    /// nor counted as a voluntary pass.</summary>
    public static bool IsFailure(AttemptOutcome outcome) => Classify(outcome) == OutcomeCategory.Failed;

    /// <summary>True only when the outcome yields a committable utterance.</summary>
    public static bool ProducesUtterance(AttemptOutcome outcome) => Classify(outcome) == OutcomeCategory.Spoke;
}
