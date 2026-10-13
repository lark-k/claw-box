using System.Text.Json;
using ClawBox.Contracts;
using ClawBox.Executor.Api;
using ClawBox.Executor.Files;
using ClawBox.Executor.Tools;
using ClawBox.Executor.Word;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Validation;
using DocumentFormat.OpenXml.Wordprocessing;

namespace ClawBox.Executor.Tests;

public sealed class WordAndFileTests
{
    [Fact]
    public void Replacement_spans_runs_preserves_formatting_and_produces_valid_docx()
    {
        using TestWorkspace temp = new();
        byte[] bytes;
        using (MemoryStream stream = new())
        {
            using (WordprocessingDocument document = WordprocessingDocument.Create(stream, DocumentFormat.OpenXml.WordprocessingDocumentType.Document))
            {
                MainDocumentPart part = document.AddMainDocumentPart();
                part.Document = new Document(new Body(new Paragraph(
                    new Run(new RunProperties(new Bold()), new Text("Hello wor")),
                    new Run(new RunProperties(new Italic()), new Text("ld! world!")))));
                part.Document.Save();
            }
            bytes = stream.ToArray();
        }
        WordDocuments word = new(temp.Options);
        (byte[] result, int count) = word.Replace(bytes, "world", "ClawBox");
        Assert.Equal(2, count);
        Assert.Equal("Hello ClawBox! ClawBox!", word.Read(result));
        Assert.Equal("Hello world! world!", word.Read(bytes));
        using WordprocessingDocument read = WordprocessingDocument.Open(new MemoryStream(result), false);
        Run[] runs = read.MainDocumentPart!.Document!.Descendants<Run>().ToArray();
        Assert.NotNull(runs[0].RunProperties!.GetFirstChild<Bold>());
        Assert.NotNull(runs[1].RunProperties!.GetFirstChild<Italic>());
        Assert.Empty(new OpenXmlValidator().Validate(read));
    }

    [Fact]
    public void Write_requires_hash_to_replace_and_refuses_stale_content()
    {
        using TestWorkspace temp = new();
        AtomicFiles files = new(new PathPolicy(temp.Options));
        string path = temp.FilePath("test.txt");
        files.Write(path, "old"u8.ToArray());
        Assert.Throws<ExecutorException>(() => files.Write(path, "new"u8.ToArray()));
        Assert.Throws<ExecutorException>(() => files.Write(path, "new"u8.ToArray(), "stale"));
        files.Write(path, "new"u8.ToArray(), AtomicFiles.Hash("old"u8.ToArray()));
        Assert.Equal("new", File.ReadAllText(path));
    }

    [Fact]
    public void Tool_validation_rejects_unknown_fields_and_unregistered_commands()
    {
        using TestWorkspace temp = new();
        PathPolicy policy = new(temp.Options);
        ToolRegistry tools = new(policy, new(policy), new(temp.Options), temp.Options);
        Assert.Throws<ExecutorException>(() => tools.Validate(new ToolCall(Guid.NewGuid(), "exec", JsonSerializer.SerializeToElement(new { command = "whoami" }))));
        Assert.Throws<ExecutorException>(() => tools.Validate(new ToolCall(Guid.NewGuid(), "file.read", JsonSerializer.SerializeToElement(new { path = temp.FilePath("a"), extra = "bad" }))));
    }

    [Fact]
    public void Oversized_expanded_packages_are_rejected_before_xml_parsing()
    {
        using TestWorkspace temp = new();
        temp.Options.MaxExpandedDocumentBytes = 100;
        using MemoryStream stream = new();
        using (System.IO.Compression.ZipArchive zip = new(stream, System.IO.Compression.ZipArchiveMode.Create, leaveOpen: true))
        using (Stream entry = zip.CreateEntry("word/document.xml").Open()) entry.Write(new byte[1000]);
        ExecutorException error = Assert.Throws<ExecutorException>(() => new WordDocuments(temp.Options).Read(stream.ToArray()));
        Assert.Equal("document_too_large", error.Code);
    }

    [Fact]
    public void Replacement_expansion_is_bounded_and_cannot_modify_source()
    {
        using TestWorkspace temp = new();
        temp.Options.MaxInputBytes = 16;
        byte[] source = WordDocuments.Create("aaaa");
        ExecutorException error = Assert.Throws<ExecutorException>(() => new WordDocuments(temp.Options).Replace(source, "a", new string('x', 20)));
        Assert.Equal("output_too_large", error.Code);
        temp.Options.MaxInputBytes = 1024;
        Assert.Equal("aaaa", new WordDocuments(temp.Options).Read(source));
    }

    [Fact]
    public void Plain_text_docx_round_trip_preserves_blank_paragraphs()
    {
        using TestWorkspace temp = new();
        string text = "\n你好\n\n第二行\n";
        Assert.Equal(text.Replace("\n", Environment.NewLine, StringComparison.Ordinal),
            new WordDocuments(temp.Options).Read(WordDocuments.Create(text)));
    }
}
