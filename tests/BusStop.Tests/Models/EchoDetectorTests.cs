using BusStop.Models;

namespace BusStop.Tests.Models;

public class EchoDetectorTests
{
    [Fact]
    public void Verbatim_repeat_is_an_echo()
    {
        var line = "Rent eats everything when the line stops; I've seen folks lose their houses.";
        Assert.True(EchoDetector.IsEcho(line, new[] { line }));
    }

    [Fact]
    public void Near_verbatim_repeat_is_an_echo()
    {
        // Differs only in the opening phrase — the back half is identical (from the baseline).
        var prior = "That's the point; they'll call it 'responsibility,' but when the knee swells and the worker is gone, the new owner doesn't owe him a single penny.";
        var proposed = "Exactly; they'll call it 'responsibility,' but when the knee swells and the worker is gone, the new owner doesn't owe him a single penny.";
        Assert.True(EchoDetector.IsEcho(proposed, new[] { prior }));
    }

    [Fact]
    public void Different_on_topic_lines_are_not_an_echo()
    {
        var prior = "Hope's cheap until it costs something real.";
        var proposed = "That medicine bill is the real test of any new line.";
        Assert.False(EchoDetector.IsEcho(proposed, new[] { prior }));
    }

    [Fact]
    public void No_prior_lines_means_no_echo()
    {
        Assert.False(EchoDetector.IsEcho("Anything at all.", Array.Empty<string>()));
    }

    [Fact]
    public void Jaccard_is_one_for_identical_word_sets_and_zero_for_disjoint()
    {
        var a = new HashSet<string> { "a", "b", "c" };
        var b = new HashSet<string> { "c", "b", "a" };
        var c = new HashSet<string> { "x", "y" };
        Assert.Equal(1.0, EchoDetector.Jaccard(a, b));
        Assert.Equal(0.0, EchoDetector.Jaccard(a, c));
    }
}
