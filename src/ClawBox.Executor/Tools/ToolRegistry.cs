using System.Text;
using System.Text.Json;
using ClawBox.Contracts;
using ClawBox.Executor.Api;
using ClawBox.Executor.Configuration;
using ClawBox.Executor.Files;
using ClawBox.Executor.Word;

namespace ClawBox.Executor.Tools;

public sealed class ToolRegistry(PathPolicy policy, AtomicFiles files, WordDocuments word, ExecutorOptions options)
{
    private static readonly UTF8Encoding Utf8 = new(false, true);
    public static IReadOnlyList<ToolDescription> Catalog { get; } = [
        Describe("file.read", "Read UTF-8 text and SHA-256.", false, new { path = "string" }, ["path"]),
        Describe("file.write", "Write UTF-8; replacing existing content requires expectedSha256.", true,
            new { path = "string", content = "string", expectedSha256 = "string" }, ["path", "content"]),
        Describe("word.read", "Read DOCX body and table text.", false, new { path = "string" }, ["path"]),
        Describe("word.create", "Create a new DOCX from plain text.", true, new { path = "string", content = "string" }, ["path", "content"]),
        Describe("word.replace", "Replace literal body text into a new DOCX, preserving surrounding formatting.", true,
            new { path = "string", outputPath = "string", search = "string", replacement = "string" }, ["path", "outputPath", "search", "replacement"]),
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
                string path = policy.Resolve(arg.Value.GetString()!);
                if (call.Tool.StartsWith("word.", StringComparison.Ordinal) && !path.EndsWith(".docx", StringComparison.OrdinalIgnoreCase)) throw InvalidArguments();
            }
        }
        foreach (string field in new[] { "content", "search", "replacement" })
            if (call.Arguments.TryGetProperty(field, out JsonElement content) && Utf8.GetByteCount(content.GetString()!) > options.MaxInputBytes)
                throw new ExecutorException("input_too_large", 413, "Text exceeds the size limit.");
        if (call.Tool == "word.replace" && string.IsNullOrEmpty(Get(call, "search"))) throw InvalidArguments();
    }

    public JsonElement Execute(ToolCall call, CancellationToken cancellation)
    {
        Validate(call);
        cancellation.ThrowIfCancellationRequested();
        string path = policy.Resolve(Get(call, "path"));
        switch (call.Tool)
        {
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
                string output = policy.Resolve(Get(call, "outputPath"));
                (byte[] bytes, int count) = word.Replace(Read(path, options.MaxDocumentBytes), Get(call, "search"), Get(call, "replacement"));
                cancellation.ThrowIfCancellationRequested();
                files.Write(output, bytes);
                return JsonSerializer.SerializeToElement(new { path = output, replacements = count });
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
    private static ExecutorException InvalidArguments() => new("invalid_arguments", 422, "Tool arguments do not match the tool schema.");
    private static ToolDescription Describe(string name, string description, bool mutates, object fields, string[] required)
    {
        Dictionary<string, object> properties = JsonSerializer.SerializeToElement(fields).EnumerateObject()
            .ToDictionary(p => p.Name, p => (object)new { type = p.Value.GetString() });
        return new(name, description, JsonSerializer.SerializeToElement(new { type = "object", properties, required, additionalProperties = false }), mutates);
    }
}
