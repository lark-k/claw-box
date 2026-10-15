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
        return state.Load("chat-index.json", () => new List<Entry>()).Select(LoadEntry).ToList();
    }
    private Conversation LoadEntry(Entry entry)
    {
        string path = FilePath(entry.Id, entry.CreatedAt);
        try { return Read(path, entry); }
        catch (Exception error) when (error is JsonException or FileNotFoundException or DirectoryNotFoundException)
        {
            string backup = path + ".bak"; CheckPath(backup);
            try
            {
                Conversation recovered = Read(backup, entry);
                recovered.Messages.Add(new() { Role = "system", Text = "本条对话已从上一份有效备份载入，最近部分内容可能缺失。原损坏文件保留；不会重新发送历史任务。" });
                return recovered;
            }
            catch (Exception backupError) when (backupError is JsonException or FileNotFoundException or DirectoryNotFoundException) { }
            return new()
            {
                Id = entry.Id,
                CreatedAt = entry.CreatedAt,
                UpdatedAt = entry.CreatedAt,
                Title = "记录暂不可用",
                HistoryUnavailable = true,
                Messages = [new() { Role = "system", Text = "本条聊天记录损坏或丢失，暂无有效备份，内容尚未恢复。原文件和索引已保留，当前记录只读。请点击“新建对话”继续使用；不会重新发送历史任务。" }]
            };
        }
    }
    private static Conversation Read(string path, Entry entry)
    {
        Conversation conversation = JsonSerializer.Deserialize<Conversation>(File.ReadAllText(path), Json) ?? throw new JsonException();
        if (conversation.Id != entry.Id || conversation.CreatedAt != entry.CreatedAt) throw new InvalidOperationException("对话索引与记录不一致。原文件已保留。");
        if (conversation.Title is null || conversation.WorkspacePath is null || conversation.Messages is null ||
            conversation.Messages.Any(message => message is null || message.Text is null || message.Activities is null || message.Changes is null || message.ProgressEntries is null))
            throw new JsonException();
        return conversation;
    }
    public void Save(Conversation conversation)
    {
        if (conversation.HistoryUnavailable) return;
        string path = FilePath(conversation.Id, conversation.CreatedAt);
        state.Save(Path.GetRelativePath(state.DirectoryPath, path), conversation);
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
        string backup = path + ".bak"; CheckPath(backup);
        // Only this application-owned transcript. Never use WorkspacePath as a delete target.
        if (File.Exists(path)) File.Delete(path);
        if (File.Exists(backup)) File.Delete(backup);
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
