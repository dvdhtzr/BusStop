using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using BusStop.Console;

namespace BusStop.Tests.Moderation;

public sealed class ShadowCorpusEvaluatorTests
{
    private static (string Audit, string Labels) Fixture(bool tamper = false)
    {
        const string session = "32da92b9-bf9c-4216-8789-962ea6075fe9";
        var labels = Enumerable.Range(1, 33).Select(n =>
        {
            var text = $"synthetic-{n}";
            var hash = Convert.ToHexString(
                SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();
            return new
            {
                ordinal = n,
                candidateTextSha256 = hash,
                expected = n == 11 ? "ExactCopyOther" : "Allow"
            };
        }).ToArray();
        var reviews = Enumerable.Range(1, 33).Select(n => new
        {
            Candidate = new { Text = tamper && n == 13 ? "tampered" : $"synthetic-{n}" },
            Status = n <= 11 ? 0 : 1,
            FailureDetail = n > 11 ? "Invalid JSON" : null,
            Judgment = n <= 11 ? (object)new
            {
                Disposition = 0,
                Finding = (object?)null
            } : null
        }).ToArray();
        return (
            JsonSerializer.Serialize(new { session, reviews }),
            JsonSerializer.Serialize(new
            {
                corpusVersion = "faa-shadow-v1",
                status = "provisional",
                sourceSession = session,
                labels
            }));
    }

    [Fact]
    public void Offline_evaluation_accepts_matching_private_audit_and_never_calls_Ollama()
    {
        var (audit, labels) = Fixture();
        using var result = JsonDocument.Parse(
            JsonSerializer.Serialize(ShadowCorpusEvaluator.Evaluate(audit, labels)));
        Assert.Equal(33, result.RootElement.GetProperty("reviewCount").GetInt32());
        Assert.Equal(11, result.RootElement.GetProperty("structuredValid").GetInt32());
        Assert.Equal(22, result.RootElement.GetProperty("structuredInvalid").GetInt32());
        Assert.True(result.RootElement.GetProperty("historicalOnly").GetBoolean());
    }

    [Fact]
    public void Offline_evaluation_rejects_mutated_candidate_even_with_matching_session()
    {
        var (audit, labels) = Fixture(tamper: true);
        Assert.Throws<InvalidDataException>(() =>
            ShadowCorpusEvaluator.Evaluate(audit, labels));
    }
}
