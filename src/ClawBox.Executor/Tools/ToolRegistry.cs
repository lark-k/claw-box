using System.Text;
using System.Text.Json;
using ClawBox.Contracts;
using ClawBox.Executor.Api;
using ClawBox.Executor.Configuration;
using ClawBox.Executor.Files;
using ClawBox.Executor.Word;
using ClawBox.Executor.Projects;

namespace ClawBox.Executor.Tools;

public sealed class ToolRegistry(PathPolicy policy, AtomicFiles files, WordDocuments word, ExecutorOptions options)
{
    private static readonly UTF8Encoding Utf8 = new(false, true);
    public static IReadOnlyList<ToolDescription> Catalog { get; } = [
        Describe("file.read", "Read UTF-8 text and SHA-256.", false, new { path = "string" }, ["path"]),
        Describe("file.list", "List project source files and an immutable snapshot SHA-256.", false, new { path = "string" }, ["path"]),
        Describe("directory.create", "Create a directory and missing parents within the approved workspace. Existing directories are unchanged.", true, new { path = "string" }, ["path"]),
        Describe("directory.list", "List direct child files and folders, including office documents. Does not follow links.", false, new { path = "string" }, ["path"]),
        Describe("file.info", "Read file size, timestamp and SHA-256, including binary files.", false, new { path = "string" }, ["path"]),
        Describe("file.search", "Search literal case-sensitive text in bounded UTF-8 source/text files under a directory. Returns lines and hashes.", false, new { path = "string", query = "string" }, ["path", "query"]),
        Describe("file.copy", "Copy a file to a new destination; source hash is required, no overwrite.", true, new { path = "string", outputPath = "string", expectedSha256 = "string" }, ["path", "outputPath", "expectedSha256"]),
        Describe("file.move", "Move or rename one file on the same volume; source hash is required, no overwrite.", true, new { path = "string", outputPath = "string", expectedSha256 = "string" }, ["path", "outputPath", "expectedSha256"]),
        Describe("file.replace", "Replace all literal matches in UTF-8 text using the current source hash. No regex; no match leaves file unchanged.", true, new { path = "string", search = "string", replacement = "string", expectedSha256 = "string" }, ["path", "search", "replacement", "expectedSha256"]),
        Describe("project.check", "Build or test an approved snapshot with automatic language detection in Windows AppContainer.", true,
            new { path = "string", kind = "string", expectedSnapshotSha256 = "string" }, ["path", "kind", "expectedSnapshotSha256"]),
        Describe("file.write", "Write UTF-8; replacing existing content requires expectedSha256.", true,
            new { path = "string", content = "string", expectedSha256 = "string" }, ["path", "content"]),
        Describe("word.read", "Read DOCX body and table text.", false, new { path = "string" }, ["path"]),
        Describe("word.create", "Create a new DOCX from plain text.", true, new { path = "string", content = "string" }, ["path", "content"]),
        Describe("word.replace", "Replace literal DOCX body text. Omit outputPath to edit the original with its current expectedSha256; specify a NEW outputPath only when user requests a copy. Preserves surrounding formatting.", true,
            new { path = "string", outputPath = "string", expectedSha256 = "string", search = "string", replacement = "string" }, ["path", "search", "replacement"]),
        Describe("word.format", "Format DOCX body/table paragraphs. Omit outputPath to modify original; specify a NEW path only for an explicit copy. Requires current expectedSha256. scope document or paragraph; paragraph requires exact text. bold true/false, fontName, fontSizePt 6..72 (half-point steps), alignment left/center/right/justify, style Title/Heading1/Heading2/Heading3/Normal. At least one option required.", true,
            new { path = "string", outputPath = "string", expectedSha256 = "string", scope = "string", text = "string", bold = "string", fontName = "string", fontSizePt = "string", alignment = "string", style = "string" }, ["path", "expectedSha256", "scope"]),
    ];

    public void Validate(ToolCall? call)
    {
        if (call is null || call.RequestId == Guid.Empty || call.Arguments.ValueKind != JsonValueKind.Object)
            throw InvalidArguments();
        ToolDescription description = Catalog.FirstOrDefault(t => t.Name == call.Tool)
            ?? throw new ExecutorException("unknown_tool", 422, "This tool is not registered.");
        JsonElement schema = description.InputSchema;
        string[] keys = call.Arguments.EnumerateObject().Select(p => p.Name).ToArray();
        if (keys.Length != keys.Distinct(StringComparer.Ordinal).Count()) throw InvalidArguments();
        foreach (string required in schema.GetProperty("required").EnumerateArray().Select(e => e.GetString()!))
            if (!call.Arguments.TryGetProperty(required, out JsonElement value) || value.ValueKind != JsonValueKind.String) throw InvalidArguments();
        foreach (JsonProperty arg in call.Arguments.EnumerateObject())
        {
            if (!schema.GetProperty("properties").TryGetProperty(arg.Name, out _) || arg.Value.ValueKind != JsonValueKind.String) throw InvalidArguments();
            if (arg.Name is "path" or "outputPath")
            {
                string path = IsDirectoryTool(call.Tool) ? policy.ResolveDirectory(arg.Value.GetString()!) : policy.Resolve(arg.Value.GetString()!);
                if (call.Tool.StartsWith("word.", StringComparison.Ordinal) && !path.EndsWith(".docx", StringComparison.OrdinalIgnoreCase)) throw InvalidArguments();
            }
        }
        foreach (string field in new[] { "content", "search", "replacement", "query", "text" })
            if (call.Arguments.TryGetProperty(field, out JsonElement content) && Utf8.GetByteCount(content.GetString()!) > options.MaxInputBytes)
                throw new ExecutorException("input_too_large", 413, "Text exceeds the size limit.");
        if (call.Tool is "word.replace" or "file.replace" && string.IsNullOrEmpty(Get(call, "search"))) throw InvalidArguments();
        if (call.Tool == "file.search" && (string.IsNullOrWhiteSpace(Get(call, "query")) || Get(call, "query").Length > 256 || Get(call, "query").Contains('\n'))) throw InvalidArguments();
        if (call.Arguments.TryGetProperty("expectedSha256", out JsonElement expected) && !System.Text.RegularExpressions.Regex.IsMatch(expected.GetString()!, "^[A-Fa-f0-9]{64}$")) throw InvalidArguments();
        if (call.Tool == "word.format") WordFormatting.Validate(call.Arguments);
        if (call.Tool == "word.replace" && SameWordDestination(call) && !call.Arguments.TryGetProperty("expectedSha256", out _)) throw InvalidArguments();
        if (call.Tool == "project.check" && (Get(call, "kind") is not ("build" or "test") ||
            !System.Text.RegularExpressions.Regex.IsMatch(Get(call, "expectedSnapshotSha256"), "^[A-F0-9]{64}$"))) throw InvalidArguments();
    }

    public JsonElement Execute(ToolCall call, CancellationToken cancellation)
    {
        Validate(call);
        cancellation.ThrowIfCancellationRequested();
        string path = IsDirectoryTool(call.Tool) ? policy.ResolveDirectory(Get(call, "path")) : policy.Resolve(Get(call, "path"));
        WorkspaceFiles workspace = new(policy, files, options);
        switch (call.Tool)
        {
            case "file.list": return new ProjectTree(policy).List(path, cancellation);
            case "directory.create": return workspace.CreateDirectory(path, cancellation);
            case "directory.list": return workspace.ListDirectory(path, cancellation);
            case "file.info": return workspace.Info(path);
            case "file.search": return workspace.Search(path, Get(call, "query"), cancellation);
            case "file.copy":
            case "file.move": return workspace.CopyOrMove(path, Get(call, "outputPath"), Get(call, "expectedSha256"), call.Tool == "file.move", cancellation);
            case "file.replace": return workspace.Replace(path, Get(call, "search"), Get(call, "replacement"), Get(call, "expectedSha256"), cancellation);
            case "project.check": return new ProjectChecks(policy, options).Execute(path, Get(call, "kind"), Get(call, "expectedSnapshotSha256"), cancellation);
            case "file.read":
                byte[] text = Read(path, options.MaxInputBytes);
                return JsonSerializer.SerializeToElement(new { path, content = Utf8.GetString(text), sha256 = AtomicFiles.Hash(text) });
            case "file.write":
                byte[] written = Utf8.GetBytes(Get(call, "content"));
                files.Write(path, written, call.Arguments.TryGetProperty("expectedSha256", out JsonElement hash) ? hash.GetString() : null);
                return JsonSerializer.SerializeToElement(new { path, bytes = written.Length, sha256 = AtomicFiles.Hash(written) });
            case "word.read":
                byte[] input = Read(path, options.MaxDocumentBytes);
                return JsonSerializer.SerializeToElement(new { path, content = word.Read(input), sha256 = AtomicFiles.Hash(input) });
            case "word.create":
                files.Write(path, WordDocuments.Create(Get(call, "content")));
                return JsonSerializer.SerializeToElement(new { path });
            case "word.replace":
                string output = WordDestination(call);
                byte[] original = Read(path, options.MaxDocumentBytes);
                if (WordFormatting.Get(call.Arguments, "expectedSha256") is string expectedHash) WorkspaceFiles.RequireHash(AtomicFiles.Hash(original), expectedHash);
                (byte[] bytes, int count) = word.Replace(original, Get(call, "search"), Get(call, "replacement"));
                if (count == 0) throw new ExecutorException("search_not_found", 422, "Search was not found; the document was not changed.");
                cancellation.ThrowIfCancellationRequested();
                files.Write(output, bytes, SameWordDestination(call) ? Get(call, "expectedSha256") : null);
                return JsonSerializer.SerializeToElement(new { path = output, replacements = count, sha256 = AtomicFiles.Hash(bytes) });
            case "word.format":
                byte[] source = Read(path, options.MaxDocumentBytes);
                WorkspaceFiles.RequireHash(AtomicFiles.Hash(source), Get(call, "expectedSha256"));
                (byte[] formatted, int paragraphs) = word.Format(source, call.Arguments, cancellation);
                cancellation.ThrowIfCancellationRequested();
                string destination = WordDestination(call);
                files.Write(destination, formatted, SameWordDestination(call) ? Get(call, "expectedSha256") : null);
                return JsonSerializer.SerializeToElement(new { path = destination, paragraphs, sha256 = AtomicFiles.Hash(formatted) });
            default: throw InvalidArguments();
        }
    }

    private static byte[] Read(string path, int limit)
    {
        using FileStream file = new(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (file.Length > limit) throw new ExecutorException("file_too_large", 413, "File exceeds the size limit.");
        using MemoryStream result = new();
        file.CopyTo(result);
        if (result.Length > limit) throw new ExecutorException("file_too_large", 413, "File exceeds the size limit.");
        return result.ToArray();
    }

    private static string Get(ToolCall call, string name) => call.Arguments.GetProperty(name).GetString()!;
    private static bool IsDirectoryTool(string tool) => tool is "file.list" or "project.check" or "file.search" or "directory.create" or "directory.list";
    private string WordDestination(ToolCall call) => policy.Resolve(WordFormatting.Get(call.Arguments, "outputPath") ?? Get(call, "path"));
    private bool SameWordDestination(ToolCall call) => string.Equals(WordDestination(call), policy.Resolve(Get(call, "path")), OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
    private static ExecutorException InvalidArguments() => new("invalid_arguments", 422, "Tool arguments do not match the tool schema.");
    private static ToolDescription Describe(string name, string description, bool mutates, object fields, string[] required)
    {
        Dictionary<string, object> properties = JsonSerializer.SerializeToElement(fields).EnumerateObject()
            .ToDictionary(p => p.Name, p => PropertySchema(p.Name));
        return new(name, description, JsonSerializer.SerializeToElement(new { type = "object", properties, required, additionalProperties = false }), mutates);
    }
    private static object PropertySchema(string field) => field switch
    {
        "scope" => new { type = "string", @enum = new[] { "document", "paragraph" } },
        "bold" => new { type = "string", @enum = new[] { "true", "false" } },
        "alignment" => new { type = "string", @enum = new[] { "left", "center", "right", "justify" } },
        "style" => new { type = "string", @enum = new[] { "Normal", "Title", "Heading1", "Heading2", "Heading3" } },
        "kind" => new { type = "string", @enum = new[] { "build", "test" } },
        "expectedSha256" => new { type = "string", pattern = "^[A-Fa-f0-9]{64}$" },
        "expectedSnapshotSha256" => new { type = "string", pattern = "^[A-F0-9]{64}$" },
        _ => new { type = "string" }
    };
}
