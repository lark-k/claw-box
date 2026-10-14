using System.Text.Json;
using ClawBox.Desktop.Models;

namespace ClawBox.Desktop.Services;

public sealed class ConversationStore(LocalState state)
{
    private sealed record Entry(Guid Id, DateTimeOffset CreatedAt);
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    public List<Conversation> Load()
    {
        CheckPath(Path.Combine(state.DirectoryPath, "chat-index.json"));
        CheckPath(Path.Combine(state.DirectoryPath, "conversations.json"));
        if (!File.Exists(Path.Combine(state.DirectoryPath, "chat-index.json")))
            return state.Load("conversations.json", () => new List<Conversation>());
        return state.Load("chat-index.json", () => new List<Entry>()).Select(entry =>
        {
            Conversation conversation = JsonSerializer.Deserialize<Conversation>(File.ReadAllText(FilePath(entry.Id, entry.CreatedAt)), Json)
                ?? throw new InvalidOperationException("对话记录格式损坏。原文件已保留。");
            if (conversation.Id != entry.Id || conversation.CreatedAt != entry.CreatedAt) throw new InvalidOperationException("对话索引与记录不一致。原文件已保留。");
            return conversation;
        }).ToList();
    }
    public void Save(Conversation conversation)
    {
        string path = FilePath(conversation.Id, conversation.CreatedAt);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string stage = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        CheckPath(stage);
        File.WriteAllText(stage, JsonSerializer.Serialize(conversation, Json));
        File.Move(stage, path, true);
    }
    public void SaveIndex(IEnumerable<Conversation> conversations)
    {
        CheckPath(Path.Combine(state.DirectoryPath, "chat-index.json"));
        state.Save("chat-index.json", conversations.Select(c => new Entry(c.Id, c.CreatedAt)).ToArray());
    }
    public void Migrate(IEnumerable<Conversation> conversations)
    {
        foreach (Conversation conversation in conversations) Save(conversation);
        SaveIndex(conversations);
        string legacy = Path.Combine(state.DirectoryPath, "conversations.json");
        CheckPath(legacy);
        if (File.Exists(legacy)) File.Delete(legacy);
    }
    public void Delete(Conversation conversation)
    {
        string path = FilePath(conversation.Id, conversation.CreatedAt);
        // Only this application-owned transcript. Never use WorkspacePath as a delete target.
        if (File.Exists(path)) File.Delete(path);
    }
    public string FilePath(Guid id, DateTimeOffset createdAt)
    {
        string path = Path.Combine(state.DirectoryPath, "chats", createdAt.ToString("yyyy-MM-dd"), id.ToString("N") + ".json");
        CheckPath(path);
        return path;
    }
    private void CheckPath(string path)
    {
        string root = Path.GetFullPath(state.DirectoryPath);
        if (!Path.GetFullPath(path).StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new IOException("对话数据路径超出应用目录。");
        for (FileSystemInfo? item = new FileInfo(path); item is not null; item = item is FileInfo file ? file.Directory : ((DirectoryInfo)item).Parent)
            if (item.Exists && (item.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new IOException("对话数据路径含链接，操作已停止。");
    }
}
