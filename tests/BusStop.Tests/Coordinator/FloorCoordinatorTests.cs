using BusStop.Coordinator;
using BusStop.Domain;

namespace BusStop.Tests.Coordinator;

public class FloorCoordinatorTests
{
    private static readonly CharacterId A = CharacterId.New();
    private static readonly CharacterId B = CharacterId.New();
    private static readonly CharacterId C = CharacterId.New();
    private static readonly IReadOnlyList<CharacterId> Three = new[] { A, B, C };

    private static Offer AssertOffer(CoordinatorStep step)
    {
        var offer = Assert.IsType<Offer>(step);
        return offer;
    }

    [Fact]
    public void Opening_offers_each_participant_once_in_order()
    {
        var coord = new FloorCoordinator(Three);
        Assert.Equal(A, AssertOffer(coord.Next()).Participant);
        Assert.Equal(B, AssertOffer(coord.Next()).Participant);
        Assert.Equal(C, AssertOffer(coord.Next()).Participant);
        Assert.All(new[] { coord.Next() }, s => Assert.Equal(OpportunityReason.FairOrder, AssertOffer(s).Reason));
    }

    [Fact]
    public void Directed_reply_gets_priority_after_opening()
    {
        var coord = new FloorCoordinator(Three);

        // Opening: A, B speak with no addressee; C addresses A.
        AssertOffer(coord.Next()); coord.RecordSpoke(UtteranceId.New(), A, null);
        AssertOffer(coord.Next()); coord.RecordSpoke(UtteranceId.New(), B, null);
        AssertOffer(coord.Next());
        var cToA = UtteranceId.New();
        coord.RecordSpoke(cToA, C, addressee: A);

        var next = AssertOffer(coord.Next());
        Assert.Equal(A, next.Participant);
        Assert.Equal(OpportunityReason.DirectedReply, next.Reason);
        Assert.Equal(cToA, next.ReplyTo);
    }

    [Fact]
    public void Addressee_may_decline_and_floor_moves_on()
    {
        var coord = new FloorCoordinator(Three);

        // Opening: everyone speaks without addressing anyone, so no pending reply survives opening.
        foreach (var _ in Three) { var o = AssertOffer(coord.Next()); coord.RecordSpoke(UtteranceId.New(), o.Participant, null); }

        // Post-opening: the first fair-order speaker addresses C.
        var first = AssertOffer(coord.Next());
        coord.RecordSpoke(UtteranceId.New(), first.Participant, addressee: C);

        // C gets the directed reply and declines.
        var directed = AssertOffer(coord.Next());
        Assert.Equal(C, directed.Participant);
        Assert.Equal(OpportunityReason.DirectedReply, directed.Reason);
        coord.RecordPassed(C);

        // Floor moves on by fair order without fabricating a reply for C.
        var after = AssertOffer(coord.Next());
        Assert.Equal(OpportunityReason.FairOrder, after.Reason);
    }

    [Fact]
    public void Fairness_override_breaks_two_party_pingpong()
    {
        var coord = new FloorCoordinator(Three, new CoordinatorOptions(PingPongWindow: 4));

        // Opening: everyone speaks without addressing anyone.
        foreach (var p in Three)
        {
            var o = AssertOffer(coord.Next());
            coord.RecordSpoke(UtteranceId.New(), o.Participant, null);
        }

        // Now drive the conversation: whoever is offered speaks, addressing the other of A/B.
        // C should be shut out until the override gives it the floor.
        var offeredC = false;
        for (var i = 0; i < 8 && !offeredC; i++)
        {
            var o = AssertOffer(coord.Next());
            if (o.Participant == C)
            {
                offeredC = true;
                break;
            }

            var target = o.Participant == A ? B : A;
            coord.RecordSpoke(UtteranceId.New(), o.Participant, addressee: target);
        }

        Assert.True(offeredC, "Fairness override should give C the floor despite the A/B exchange.");
    }

    [Fact]
    public void No_one_engaging_ends_without_looping()
    {
        var coord = new FloorCoordinator(Three);

        var offers = 0;
        CoordinatorStep step;
        while ((step = coord.Next()) is Offer offer)
        {
            offers++;
            coord.RecordPassed(offer.Participant);
            Assert.True(offers < 50, "Coordinator looped instead of ending.");
        }

        // No one ever spoke: the conversation never started, ended after a bounded number of offers.
        Assert.Equal(6, offers);
        var ended = Assert.IsType<ConversationEnded>(step);
        Assert.Equal(EndReason.NoEngagement, ended.Reason);
    }

    [Fact]
    public void Natural_end_fires_only_after_the_conversation_has_started()
    {
        var coord = new FloorCoordinator(Three);

        // Someone speaks during opening, so the conversation has started.
        var o1 = AssertOffer(coord.Next()); coord.RecordSpoke(UtteranceId.New(), o1.Participant, null);
        var o2 = AssertOffer(coord.Next()); coord.RecordPassed(o2.Participant);
        var o3 = AssertOffer(coord.Next()); coord.RecordPassed(o3.Participant);
        var o4 = AssertOffer(coord.Next()); coord.RecordPassed(o4.Participant);

        // One full round of consecutive passes after a start is a natural end.
        var ended = Assert.IsType<ConversationEnded>(coord.Next());
        Assert.Equal(EndReason.NaturalEnd, ended.Reason);
    }

    [Fact]
    public void A_speak_resets_the_quiet_streak_so_only_consecutive_passes_end_it()
    {
        var coord = new FloorCoordinator(Three);

        // Two passes, then a speak during the opening: the speak resets the streak.
        var o1 = AssertOffer(coord.Next()); coord.RecordPassed(o1.Participant);
        var o2 = AssertOffer(coord.Next()); coord.RecordPassed(o2.Participant);
        var o3 = AssertOffer(coord.Next()); coord.RecordSpoke(UtteranceId.New(), o3.Participant, null);

        // Not ended — the conversation is still live.
        var o4 = AssertOffer(coord.Next()); coord.RecordPassed(o4.Participant);
        var o5 = AssertOffer(coord.Next()); coord.RecordPassed(o5.Participant);
        var o6 = AssertOffer(coord.Next()); coord.RecordPassed(o6.Participant);

        // Three consecutive passes after the reset end it.
        Assert.IsType<ConversationEnded>(coord.Next());
    }
}
