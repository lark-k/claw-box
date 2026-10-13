using System.Text;
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

public sealed class WorkspaceToolsTests
{
    private static JsonElement Run(TestWorkspace temp, string name, object args)
    {
        PathPolicy policy = new(temp.Options);
        return new ToolRegistry(policy, new(policy), new(temp.Options), temp.Options)
            .Execute(new(Guid.NewGuid(), name, JsonSerializer.SerializeToElement(args)), CancellationToken.None);
    }
    [Fact]
    public void Directory_creation_handles_parents_conflicts_and_outside_paths()
    {
        using TestWorkspace temp = new(); string directory = temp.FilePath("reports/2026");
        Assert.True(Run(temp, "directory.create", new { path = directory }).GetProperty("created").GetBoolean());
        Assert.False(Run(temp, "directory.create", new { path = directory }).GetProperty("created").GetBoolean());
        Assert.Throws<ExecutorException>(() => Run(temp, "directory.create", new { path = Path.Combine(temp.Root, "outside") }));
        File.WriteAllText(temp.FilePath("occupied"), "file");
        Assert.Equal("path_type_conflict", Assert.Throws<ExecutorException>(() => Run(temp, "directory.create", new { path = temp.FilePath("occupied") })).Code);
        string canceled = temp.FilePath("never-created");
        PathPolicy policy = new(temp.Options);
        Assert.Throws<OperationCanceledException>(() => new WorkspaceFiles(policy, new(policy), temp.Options).CreateDirectory(canceled, new(true)));
        Assert.False(Directory.Exists(canceled));
    }
    [Fact]
    public void Directory_browsing_includes_office_files_but_not_private_names()
    {
        using TestWorkspace temp = new();
        File.WriteAllBytes(temp.FilePath("report.docx"), WordDocuments.Create("报告"));
        File.WriteAllText(temp.FilePath(".env"), "private"); File.WriteAllText(temp.FilePath("tokens.secrets.json"), "private");
        Directory.CreateDirectory(temp.FilePath("docs"));
        var names = Run(temp, "directory.list", new { path = temp.Workspace }).GetProperty("entries").EnumerateArray().Select(e => e.GetProperty("name").GetString()).ToArray();
        Assert.Contains("report.docx", names); Assert.Contains("docs", names);
        Assert.DoesNotContain(".env", names); Assert.DoesNotContain("tokens.secrets.json", names);
    }
    [Fact]
    public void Directory_creation_cannot_follow_a_link_outside_workspace()
    {
        using TestWorkspace temp = new(); string outside = Path.Combine(temp.Root, "outside"); Directory.CreateDirectory(outside);
        string link = temp.FilePath("linked");
        if (OperatingSystem.IsWindows())
        {
            using var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("cmd.exe")
            { ArgumentList = { "/c", "mklink", "/J", link, outside }, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true })!;
            process.WaitForExit(); Assert.Equal(0, process.ExitCode);
        }
        else Directory.CreateSymbolicLink(link, outside);
        try
        {
            Assert.Equal("path_denied", Assert.Throws<ExecutorException>(() => Run(temp, "directory.create", new { path = Path.Combine(link, "new") })).Code);
            Assert.False(Directory.Exists(Path.Combine(outside, "new")));
            Assert.Equal("link", Run(temp, "directory.list", new { path = temp.Workspace }).GetProperty("entries")[0].GetProperty("kind").GetString());
        }
        finally { Directory.Delete(link); }
    }
    [Fact]
    public void Search_returns_bounded_literal_utf8_lines_and_not_secrets()
    {
        using TestWorkspace temp = new();
        File.WriteAllText(temp.FilePath("note.txt"), string.Join('\n', Enumerable.Repeat("prefix 中文 hello.* suffix", 110)));
        File.WriteAllText(temp.FilePath(".env"), "hello.* SECRET");
        JsonElement result = Run(temp, "file.search", new { path = temp.Workspace, query = "hello.*" });
        Assert.Equal(100, result.GetProperty("matches").GetArrayLength()); Assert.True(result.GetProperty("truncated").GetBoolean());
        Assert.Equal(1, result.GetProperty("matches")[0].GetProperty("line").GetInt32());
        Assert.DoesNotContain("SECRET", result.GetRawText());
        Assert.Empty(Run(temp, "file.search", new { path = temp.Workspace, query = "HELLO" }).GetProperty("matches").EnumerateArray());
        Assert.Throws<ExecutorException>(() => Run(temp, "file.search", new { path = temp.Workspace, query = "" }));
    }
    [Fact]
    public void Binary_copy_and_rename_require_current_hash_and_do_not_overwrite()
    {
        using TestWorkspace temp = new(); byte[] bytes = WordDocuments.Create("文档正文");
        string path = temp.FilePath("source.docx"), copy = temp.FilePath("copy.docx"), moved = temp.FilePath("renamed.docx"); File.WriteAllBytes(path, bytes);
        string expectedSha256 = Run(temp, "file.info", new { path }).GetProperty("sha256").GetString()!;
        Assert.Throws<ExecutorException>(() => Run(temp, "file.copy", new { path, outputPath = copy, expectedSha256 = new string('A', 64) }));
        Assert.False(File.Exists(copy));
        Run(temp, "file.copy", new { path, outputPath = copy, expectedSha256 });
        Assert.Equal(bytes, File.ReadAllBytes(copy)); Assert.Equal(bytes, File.ReadAllBytes(path));
        Assert.Throws<ExecutorException>(() => Run(temp, "file.move", new { path, outputPath = copy, expectedSha256 }));
        Run(temp, "file.move", new { path = copy, outputPath = moved, expectedSha256 });
        Assert.False(File.Exists(copy)); Assert.Equal(bytes, File.ReadAllBytes(moved));
    }
    [Fact]
    public void Literal_replace_updates_original_and_rejects_stale_empty_or_missing_search()
    {
        using TestWorkspace temp = new(); string path = temp.FilePath("source.txt"); File.WriteAllText(path, "你好 .+ .+");
        string hash = AtomicFiles.Hash(File.ReadAllBytes(path));
        Assert.Throws<ExecutorException>(() => Run(temp, "file.replace", new { path, search = "", replacement = "x", expectedSha256 = hash }));
        Assert.Equal("search_not_found", Assert.Throws<ExecutorException>(() => Run(temp, "file.replace", new { path, search = "missing", replacement = "x", expectedSha256 = hash })).Code);
        JsonElement result = Run(temp, "file.replace", new { path, search = ".+", replacement = "完成", expectedSha256 = hash });
        Assert.Equal(2, result.GetProperty("replacements").GetInt32()); Assert.Equal("你好 完成 完成", File.ReadAllText(path));
        Assert.Throws<ExecutorException>(() => Run(temp, "file.replace", new { path, search = "完成", replacement = "x", expectedSha256 = hash }));
        Assert.Single(Directory.GetFiles(temp.Workspace));
    }
    [Fact]
    public void Word_format_modifies_original_title_and_preserves_other_paragraphs()
    {
        using TestWorkspace temp = new(); string path = temp.FilePath("report.docx"); byte[] original = WordDocuments.Create("项目报告\n原来的正文"); File.WriteAllBytes(path, original);
        string hash = AtomicFiles.Hash(original);
        Run(temp, "word.format", new { path, expectedSha256 = hash, scope = "paragraph", text = "项目报告", bold = "true", alignment = "center", fontName = "宋体", fontSizePt = "18", style = "Heading1" });
        byte[] formatted = File.ReadAllBytes(path);
        using WordprocessingDocument doc = WordprocessingDocument.Open(new MemoryStream(formatted), false);
        Paragraph[] paragraphs = doc.MainDocumentPart!.Document!.Body!.Elements<Paragraph>().ToArray();
        Assert.Equal("项目报告", paragraphs[0].InnerText); Assert.Equal(JustificationValues.Center, paragraphs[0].ParagraphProperties!.Justification!.Val!.Value);
        Assert.Equal("Heading1", paragraphs[0].ParagraphProperties!.ParagraphStyleId!.Val!.Value);
        RunProperties run = paragraphs[0].GetFirstChild<Run>()!.RunProperties!;
        Assert.True(run.Bold!.Val!.Value); Assert.Equal("宋体", run.RunFonts!.EastAsia!.Value); Assert.Equal("36", run.FontSize!.Val!.Value);
        Assert.Null(paragraphs[1].ParagraphProperties); Assert.Equal("原来的正文", paragraphs[1].InnerText);
        Assert.Empty(new OpenXmlValidator().Validate(doc)); Assert.Single(Directory.GetFiles(temp.Workspace));
        Assert.Throws<ExecutorException>(() => Run(temp, "word.format", new { path, expectedSha256 = hash, scope = "document", bold = "false" }));
    }
    [Fact]
    public void Word_format_can_explicitly_copy_and_missing_paragraph_leaves_original_unchanged()
    {
        using TestWorkspace temp = new(); string path = temp.FilePath("source.docx"), output = temp.FilePath("formatted.docx"); byte[] bytes = WordDocuments.Create("正文"); File.WriteAllBytes(path, bytes); string hash = AtomicFiles.Hash(bytes);
        Run(temp, "word.format", new { path, outputPath = output, expectedSha256 = hash, scope = "document", bold = "false", fontSizePt = "12.5", alignment = "justify" });
        Assert.Equal(bytes, File.ReadAllBytes(path)); Assert.True(File.Exists(output));
        Assert.Equal("paragraph_not_found", Assert.Throws<ExecutorException>(() => Run(temp, "word.format", new { path, expectedSha256 = hash, scope = "paragraph", text = "不存在", alignment = "center" })).Code);
        Assert.Equal(bytes, File.ReadAllBytes(path));
        Assert.Throws<ExecutorException>(() => Run(temp, "word.format", new { path, expectedSha256 = hash, scope = "document", fontSizePt = "12.3" }));
        Assert.Throws<ExecutorException>(() => Run(temp, "word.format", new { path, expectedSha256 = hash, scope = "document" }));
    }
    [Fact]
    public void Word_text_replacement_defaults_to_original_but_needs_its_hash()
    {
        using TestWorkspace temp = new(); string path = temp.FilePath("report.docx"); File.WriteAllBytes(path, WordDocuments.Create("需要修改的文字"));
        string expectedSha256 = AtomicFiles.Hash(File.ReadAllBytes(path));
        Assert.Throws<ExecutorException>(() => Run(temp, "word.replace", new { path, search = "修改", replacement = "更新" }));
        Run(temp, "word.replace", new { path, search = "修改", replacement = "更新", expectedSha256 });
        Assert.Equal("需要更新的文字", new WordDocuments(temp.Options).Read(File.ReadAllBytes(path))); Assert.Single(Directory.GetFiles(temp.Workspace));
    }
    [Fact]
    public void Replacement_expansion_limit_preserves_original()
    {
        using TestWorkspace temp = new(); temp.Options.MaxInputBytes = 16; string path = temp.FilePath("a.txt"); File.WriteAllText(path, "aaa");
        Assert.Equal("output_too_large", Assert.Throws<ExecutorException>(() => Run(temp, "file.replace", new { path, search = "a", replacement = "12345678", expectedSha256 = AtomicFiles.Hash(File.ReadAllBytes(path)) })).Code);
        Assert.Equal("aaa", File.ReadAllText(path));
    }
}
