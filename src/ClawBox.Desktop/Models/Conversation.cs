using System.ComponentModel;
using System.Collections.ObjectModel;
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;

namespace ClawBox.Desktop.Models;

public class Observable : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;
    protected bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        Changed(name);
        return true;
    }
    public void Changed([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new(name));
}

public sealed class Message : Observable
{
    private string text = "";
    public string Role { get; set; } = "assistant";
    public string Text { get => text; set { if (Set(ref text, value)) { Changed(nameof(DisplayText)); Changed(nameof(HasText)); Changed(nameof(PreviewText)); } } }
    [JsonIgnore] public string DisplayText => Text.Trim();
    [JsonIgnore] public bool HasText => !string.IsNullOrWhiteSpace(Text);
    public DateTimeOffset At { get; set; } = DateTimeOffset.Now;
    public ObservableCollection<ActivityRecord> Activities { get; set; } = [];
    public ObservableCollection<FileChange> Changes { get; set; } = [];
    private string duration = "处理过程";
    public string Duration { get => duration; set => Set(ref duration, value); }
    private bool processing;
    [JsonIgnore] public bool IsProcessing { get => processing; set { if (Set(ref processing, value)) { ProgressExpanded = value; Changed(nameof(IsThinking)); Changed(nameof(PreviewText)); } } }
    private bool progressExpanded;
    [JsonIgnore] public bool ProgressExpanded { get => progressExpanded; set => Set(ref progressExpanded, value); }
    public ObservableCollection<ProgressEntry> ProgressEntries { get; set; } = [];
    private bool awaitingApproval;
    [JsonIgnore] public bool AwaitingApproval { get => awaitingApproval; set { Set(ref awaitingApproval, value); Changed(nameof(IsThinking)); Changed(nameof(ProcessingLabel)); } }
    [JsonIgnore] public bool IsThinking => IsProcessing && !AwaitingApproval;
    [JsonIgnore] public string ProcessingLabel => AwaitingApproval ? "等待批准" : "思考中";
    private string waitingHint = "";
    [JsonIgnore] public string WaitingHint { get => waitingHint; set => Set(ref waitingHint, value); }
    [JsonIgnore] public string Label => Role == "user" ? "你" : Role == "system" ? "任务状态" : "Quantum";
    [JsonIgnore] public string PreviewTitle => $"{Label} · {At:HH:mm}";
    [JsonIgnore]
    public string PreviewText => DisplayText.Length == 0 ? IsProcessing ? "正在处理这条消息…" : "这条消息没有正文。"
        : DisplayText.Length > 600 ? DisplayText[..600] + "…" : DisplayText;
}

public sealed class Conversation : Observable
{
    private string title = "新任务";
    public Guid Id { get; set; } = Guid.NewGuid();
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.Now;
    public string WorkspacePath { get; set; } = "";
    public bool IsProject { get; set; }
    public string Title { get => title; set => Set(ref title, value); }
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.Now;
    public List<Message> Messages { get; set; } = [];
    [JsonIgnore] public bool HistoryUnavailable { get; set; }
}

public sealed class DesktopSettings
{
    public List<string> Projects { get; set; } = [];
    public string AccessMode { get; set; } = "approval";
    public string Workspace { get; set; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ClawBox", "workspace");
    public string ProfilePath { get; set; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ClawBox", "connections", "192.168.137.2-22", "connection.json");
    public string GatewayTokenFile { get; set; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ClawBox", "desktop", "gateway.secrets.json");
}

public sealed record ActivityRecord(Guid RequestId, string Description, string Detail)
{
    [JsonIgnore] public string Icon => Description.Contains("project.", StringComparison.Ordinal) ? "\uE756" : "\uE70F";
}
public sealed class ProgressEntry : Observable
{
    public Guid? RequestId { get; set; }
    public string Kind { get; set; } = "agent";
    private string text = "", detail = "";
    public string Text { get => text; set => Set(ref text, value); }
    public string Detail { get => detail; set { if (Set(ref detail, value)) Changed(nameof(Icon)); } }
    [JsonIgnore] public string Foreground => Kind == "agent" ? "#45454A" : "#929298";
    [JsonIgnore] public string Icon => Kind == "command" || Detail.Split('\n')[0].Contains("project.", StringComparison.Ordinal) ? "\uE756" : "\uE70F";
    public static string Concise(string text)
    {
        string normalized = string.Join(" ", text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        if (normalized.Length <= 80) return normalized;
        int length = char.IsHighSurrogate(normalized[79]) ? 79 : 80;
        return normalized[..length] + "…";
    }
}
public sealed record DiffLine(string Number, string Text, string Kind)
{
    [JsonIgnore] public string Background => Kind == "added" ? "#E6F4E8" : Kind == "removed" ? "#FDE8E6" : "#F7F7F8";
    [JsonIgnore] public string Foreground => Kind == "added" ? "#049841" : Kind == "removed" ? "#D32F2F" : "#707076";
}
public sealed class FileChange
{
    public string Path { get; set; } = "";
    public string DisplayPath { get; set; } = "";
    public string Note { get; set; } = "";
    public bool HasDiff { get; set; }
    public List<DiffLine> Lines { get; set; } = [];
    [JsonIgnore] public string Added => HasDiff ? "+" + Lines.Count(l => l.Kind == "added") : "";
    [JsonIgnore] public string Removed => HasDiff ? "-" + Lines.Count(l => l.Kind == "removed") : "";
}

public sealed class ProjectGroup(string path, IEnumerable<Conversation> conversations) : Observable
{
    private bool expanded = true;
    private IEnumerable<Conversation> chats = conversations;
    public string Path { get; } = path;
    public string Name => System.IO.Path.GetFileName(System.IO.Path.TrimEndingDirectorySeparator(Path));
    public IEnumerable<Conversation> Conversations { get => chats; set => Set(ref chats, value); }
    public bool IsExpanded { get => expanded; set => Set(ref expanded, value); }
}
