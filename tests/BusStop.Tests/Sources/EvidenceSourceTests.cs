using System.Text;
using BusStop.Domain;
using BusStop.Sources;

namespace BusStop.Tests.Sources;

public sealed class EvidenceSourceTests : IDisposable
{
    private readonly string _folder = Path.Combine(
        Path.GetTempPath(), "busstop-source-" + Guid.NewGuid().ToString("N"));

    private static readonly EvidenceSourceDescriptor News = new(
        EvidenceSourceKind.NewsArticle,
        "Council approves bridge replacement",
        "Test News Outlet",
        "https://example.org/news/bridge",
        new DateTimeOffset(2026, 11, 15, 8, 0, 0, TimeSpan.Zero),
        "Licensed test fixture");

    [Fact]
    public void News_and_document_share_a_contract_but_not_a_source_identity()
    {
        const string text = "The council approved a bridge replacement today.";
        var news = EvidenceSnapshotFactory.Capture(News, text);
        var document = EvidenceSnapshotFactory.Capture(
            new EvidenceSourceDescriptor(
                EvidenceSourceKind.UserDocument, "Notes", "User-provided local document",
                null, null, "User submitted for local use"),
            text);

        Assert.Equal(EvidenceSourceKind.NewsArticle, news.Descriptor.Kind);
        Assert.Equal(EvidenceSourceKind.UserDocument, document.Descriptor.Kind);
        Assert.NotEqual(news.SnapshotId, document.SnapshotId);
        Assert.Equal(news.TextSha256, document.TextSha256);
        Assert.Equal(text, news.SnapshotText);
        Assert.Single(news.Passages);
        Assert.Equal($"{news.SnapshotId}:p00001", news.Passages[0].PassageId);
        Assert.Equal(text, news.Passages[0].Text);
        Assert.Equal(0, news.Passages[0].StartOffset);
    }

    [Fact]
    public void Same_source_text_and_metadata_have_stable_citations_across_capture_times()
    {
        var one = EvidenceSnapshotFactory.Capture(
            News, "The bridge will be closed for two years.",
            DateTimeOffset.Parse("2026-11-15T10:00:00Z"));
        var two = EvidenceSnapshotFactory.Capture(
            News, "The bridge will be closed for two years.",
            DateTimeOffset.Parse("2026-11-16T10:00:00Z"));
        var changed = EvidenceSnapshotFactory.Capture(
            News, "The bridge may be closed for two years.");

        Assert.Equal(one.SnapshotId, two.SnapshotId);
        Assert.Equal(one.TextSha256, two.TextSha256);
        Assert.Equal(one.Passages.Select(x => x.PassageId),
            two.Passages.Select(x => x.PassageId));
        Assert.NotEqual(one.SnapshotId, changed.SnapshotId);
        Assert.NotEqual(one.Passages[0].PassageId, changed.Passages[0].PassageId);
    }

    [Fact]
    public void Passage_boundaries_preserve_exact_utf16_text_and_full_document_coverage()
    {
        var text = string.Concat(Enumerable.Repeat(
            "Workers 🧰 discussed bridge repairs, costs and access. ", 60));
        var snapshot = EvidenceSnapshotFactory.Capture(
            News, text, passageCharacters: 128, overlapCharacters: 20);

        Assert.True(snapshot.Passages.Count > 3);
        Assert.Equal(0, snapshot.Passages[0].StartOffset);
        Assert.Equal(text.Length, snapshot.Passages[^1].EndOffset);

        var previousEnd = 0;
        foreach (var (p, index) in snapshot.Passages.Select((p, i) => (p, i)))
        {
            Assert.Equal(index + 1, p.Ordinal);
            Assert.Equal($"{snapshot.SnapshotId}:p{index + 1:D5}", p.PassageId);
            Assert.InRange(p.Length, 1, 128);
            Assert.Equal(text.Substring(p.StartOffset, p.Length), p.Text);
            if (index > 0)
            {
                Assert.True(p.StartOffset <= previousEnd, "No text gaps allowed.");
                Assert.True(p.StartOffset < previousEnd, "Expected overlapping passages.");
            }
            Assert.False(char.IsLowSurrogate(text[p.StartOffset]));
            Assert.False(char.IsHighSurrogate(text[p.EndOffset - 1]));
            previousEnd = p.EndOffset;
        }
    }

    [Fact]
    public void News_capture_requires_explicit_public_provenance()
    {
        Assert.Throws<ArgumentException>(() => EvidenceSnapshotFactory.Capture(
            News with { SourceUrl = "http://example.org/news/bridge" }, "Text"));
        Assert.Throws<ArgumentException>(() => EvidenceSnapshotFactory.Capture(
            News with { PublishedAt = null }, "Text"));
        Assert.Throws<ArgumentException>(() => EvidenceSnapshotFactory.Capture(
            News with { RightsBasis = " " }, "Text"));
        Assert.Throws<ArgumentException>(() =>
            EvidenceSnapshotFactory.Capture(News, " "));
    }

    [Fact]
    public void Local_text_document_ingests_without_exposing_its_absolute_path()
    {
        Directory.CreateDirectory(_folder);
        var path = Path.Combine(_folder, "bridge-notes.md");
        const string contents = "# Bridge Notes\r\nThe closure is planned. 🧰\r\n";
        File.WriteAllText(path, contents, new UTF8Encoding(false));

        var snapshot = new LocalTextDocumentImporter().Import(path);

        Assert.Equal(EvidenceSourceKind.UserDocument, snapshot.Descriptor.Kind);
        Assert.Equal("bridge-notes", snapshot.Descriptor.Title);
        Assert.Null(snapshot.Descriptor.SourceUrl);
        Assert.Null(snapshot.Descriptor.PublishedAt);
        Assert.DoesNotContain(_folder, snapshot.ToString());
        Assert.Equal(contents, snapshot.SnapshotText);
        Assert.Equal(contents, snapshot.Passages[0].Text);
    }

    [Fact]
    public void Local_import_rejects_unsupported_binary_blank_and_oversize_files()
    {
        Directory.CreateDirectory(_folder);
        var importer = new LocalTextDocumentImporter();

        var pdf = Path.Combine(_folder, "paper.pdf");
        File.WriteAllText(pdf, "Not a real PDF.");
        Assert.Throws<NotSupportedException>(() => importer.Import(pdf));

        var badUtf8 = Path.Combine(_folder, "bad.txt");
        File.WriteAllBytes(badUtf8, new byte[] { 0xC3, 0x28 });
        Assert.Throws<InvalidDataException>(() => importer.Import(badUtf8));

        var binary = Path.Combine(_folder, "binary.txt");
        File.WriteAllBytes(binary, new byte[] { 65, 0, 66 });
        Assert.Throws<InvalidDataException>(() => importer.Import(binary));

        var blank = Path.Combine(_folder, "blank.txt");
        File.WriteAllText(blank, "  \r\n\t  ");
        Assert.Throws<InvalidDataException>(() => importer.Import(blank));

        var huge = Path.Combine(_folder, "huge.txt");
        File.WriteAllBytes(huge, new byte[(int)LocalTextDocumentImporter.MaxDocumentBytes + 1]);
        Assert.Throws<InvalidDataException>(() => importer.Import(huge));
    }

    public void Dispose()
    {
        if (Directory.Exists(_folder))
            Directory.Delete(_folder, recursive: true);
    }
}
