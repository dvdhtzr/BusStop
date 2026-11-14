namespace BusStop.Models;

/// <summary>Detects when a proposed utterance merely repeats the speaker's own recent line. Uses a
/// deterministic word-set Jaccard similarity so the check is inspectable and reproducible. The threshold
/// is deliberately high: it targets verbatim and near-verbatim self-repeats, not ordinary on-topic
/// overlap (two different turns about the same subject share far fewer words than a repeated line).</summary>
public static class EchoDetector
{
    public static bool IsEcho(string proposed, IEnumerable<string> priorBySameSpeaker, double threshold = 0.8)
    {
        var p = Tokenize(proposed);
        if (p.Count == 0) return false;

        foreach (var prior in priorBySameSpeaker)
        {
            var q = Tokenize(prior);
            if (q.Count == 0) continue;
            if (Jaccard(p, q) >= threshold) return true;
        }

        return false;
    }

    public static double Jaccard(IReadOnlySet<string> a, IReadOnlySet<string> b)
    {
        if (a.Count == 0 && b.Count == 0) return 1.0;
        var intersection = a.Count(b.Contains);
        var union = a.Count + b.Count - intersection;
        return union == 0 ? 0.0 : (double)intersection / union;
    }

    private static HashSet<string> Tokenize(string text)
    {
        var set = new HashSet<string>();
        var current = new System.Text.StringBuilder();
        foreach (var ch in text)
        {
            if (char.IsLetterOrDigit(ch))
            {
                current.Append(char.ToLowerInvariant(ch));
            }
            else if (current.Length > 0)
            {
                set.Add(current.ToString());
                current.Clear();
            }
        }
        if (current.Length > 0) set.Add(current.ToString());
        return set;
    }
}
