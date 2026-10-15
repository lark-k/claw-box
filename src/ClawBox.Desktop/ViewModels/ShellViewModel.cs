using System.Collections.ObjectModel;
using System.IO;
using System.Text.Json;
using System.Windows.Threading;
using ClawBox.Contracts;
using ClawBox.Desktop.Models;
using ClawBox.Desktop.Services;

namespace ClawBox.Desktop.ViewModels;

public sealed class ShellViewModel : Observable, IDisposable
{
    private readonly LocalState state;
    private readonly ConversationStore store;
    private Message? activeReply;
    private TurnTranscript? transcript;
    private DateTimeOffset turnStarted;
    private readonly Dictionary<Guid, (ToolCall Call, FileChanges.Snapshot Before, FileChanges.Snapshot? Source)> tracked = [];
    private readonly HashSet<Guid> completed = [];
    private readonly AutomaticApprovals automatic = new();
    private readonly Dictionary<string, FileChanges.Snapshot> originals = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, ProjectGroup> projectGroups = new(StringComparer.OrdinalIgnoreCase);
    private readonly RuntimeSession runtime = new();
    private CancellationTokenSource? connecting;
    public bool IsConnecting => connecting is not null;
    public string ConnectionButtonLabel => IsConnecting ? "停止连接" : "连接盒子";
    public void CancelConnection() => connecting?.Cancel();
    private readonly DispatcherTimer timer;
    private CancellationTokenSource? turn;
    private bool userStopped;
    private IdleDeadline? idle;
    private Conversation selected;
    private string status = "未连接盒子", input = "", search = "";
    private bool connected, busy, refreshing;
    public ObservableCollection<Conversation> Conversations { get; }
    public ObservableCollection<Message> Messages { get; } = [];
    public ObservableCollection<ApprovalCard> Approvals { get; } = [];
    public ObservableCollection<TaskCard> Tasks { get; } = [];
    public ObservableCollection<string> Attachments { get; } = [];
    public ObservableCollection<FileChange> Changes { get; } = [];
    public bool HasApprovals => Approvals.Count > 0;
    public IEnumerable<ApprovalCard> VisibleApprovals => Approvals.Take(1);
    public string ChangeTotals => $"本轮 · {Changes.Count} 个文件";
    public IEnumerable<ProjectGroup> Projects => Settings.Projects.Select(path =>
    {
        Conversation[] chats = FilteredConversations.Where(c => c.IsProject && c.WorkspacePath.Equals(path, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (!projectGroups.TryGetValue(path, out ProjectGroup? group)) projectGroups[path] = group = new(path, chats);
        else if (!group.Conversations.SequenceEqual(chats)) group.Conversations = chats;
        return group;
    }).ToArray();
    public IEnumerable<Conversation> Recent => FilteredConversations.Where(c => !c.IsProject || !Settings.Projects.Contains(c.WorkspacePath, StringComparer.OrdinalIgnoreCase)).ToArray();
    public string AccessLabel => Settings.AccessMode switch { "readOnly" => "只读", "fullAccess" => "完全访问", _ => "请求批准" };
    public string ContextInfo => $"本地文字约 {Math.Ceiling((Messages.Sum(m => (long)m.Text.Length) + Input.Length) / 2.0):N0} 标记\n仅估算本地对话文字；不含盒子历史和工具内容。\n服务端未提供上下文容量和实际用量。";
    public DesktopSettings Settings { get; }
    public string Status { get => status; set => Set(ref status, value); }
    public bool Connected { get => connected; private set => Set(ref connected, value); }
    public bool Busy { get => busy; private set { Set(ref busy, value); Changed(nameof(CanSend)); Changed(nameof(CanManageChats)); Changed(nameof(IsResponding)); } }
    public bool IsResponding => Busy && turn is not null;
    public bool CanSend => Connected && !Busy && Approvals.Count == 0 && !Selected.HistoryUnavailable;
    public bool CanManageChats => !Busy && Approvals.Count == 0 && !Tasks.Any(t => t.IsRunning);
    public string Input { get => input; set { Set(ref input, value); Changed(nameof(ContextInfo)); } }
    public string Search { get => search; set { Set(ref search, value); SidebarChanged(); } }
    public IEnumerable<Conversation> FilteredConversations => Conversations.Where(c => c.Title.Contains(Search, StringComparison.OrdinalIgnoreCase)).OrderByDescending(c => c.UpdatedAt);
    public Conversation Selected
    {
        get => selected;
        set
        {
            if (!CanManageChats || value is null || value == selected) return;
            selected = value;
            bool reconnect = Connected && !Settings.Workspace.Equals(value.WorkspacePath, StringComparison.OrdinalIgnoreCase);
            Settings.Workspace = value.WorkspacePath;
            if (reconnect) SaveSettings();
            activeReply = null; tracked.Clear(); completed.Clear();
            Messages.Clear();
            foreach (Message message in selected.Messages) Messages.Add(message);
            Attachments.Clear();
            Changes.Clear();
            foreach (FileChange change in selected.Messages.LastOrDefault(m => m.Role == "assistant")?.Changes ?? []) Changes.Add(change);
            Changed(); Changed(nameof(Title)); Changed(nameof(WorkspacePath)); Changed(nameof(ContextInfo)); Changed(nameof(CanSend));
            if (reconnect) _ = Connect();
        }
    }
    public string Title => Selected.Title;
    public string WorkspacePath => Settings.Workspace;
    public ShellViewModel(LocalState state)
    {
        this.state = state;
        store = new(state);
        Settings = state.Load("settings.json", () => new DesktopSettings());
        Settings.AccessMode = "approval";
        Conversations = new(store.Load());
        foreach (Conversation conversation in Conversations.Where(c => c.WorkspacePath.Length == 0))
        {
            conversation.WorkspacePath = Settings.Workspace;
            if (conversation.HistoryUnavailable) continue;
            conversation.IsProject = !Settings.Workspace.Equals(new DesktopSettings().Workspace, StringComparison.OrdinalIgnoreCase);
            if (conversation.IsProject && !Settings.Projects.Contains(Settings.Workspace, StringComparer.OrdinalIgnoreCase)) Settings.Projects.Add(Settings.Workspace);
            conversation.CreatedAt = conversation.Messages.FirstOrDefault()?.At ?? conversation.UpdatedAt;
        }
        if (Conversations.Count == 0) Conversations.Add(CreateRecent());
        selected = Conversations[0];
        Settings.Workspace = selected.WorkspacePath;
        foreach (Message message in selected.Messages) Messages.Add(message);
        foreach (FileChange change in selected.Messages.LastOrDefault(m => m.Role == "assistant")?.Changes ?? []) Changes.Add(change);
        if (!File.Exists(Path.Combine(state.DirectoryPath, "chat-index.json"))) store.Migrate(Conversations);
        state.Save("settings.json", Settings);
        Approvals.CollectionChanged += (_, _) => { Changed(nameof(CanSend)); Changed(nameof(CanManageChats)); Changed(nameof(HasApprovals)); Changed(nameof(VisibleApprovals)); if (activeReply is not null) activeReply.AwaitingApproval = HasApprovals; };
        Tasks.CollectionChanged += (_, _) => Changed(nameof(CanManageChats));
        Changes.CollectionChanged += (_, _) => Changed(nameof(ChangeTotals));
        timer = new(TimeSpan.FromMilliseconds(800), DispatcherPriority.Background, async (_, _) => await Refresh(), Dispatcher.CurrentDispatcher);
        timer.Stop();
    }
    public void NewConversation()
    {
        if (!CanManageChats) return;
        Attachments.Clear();
        Conversation conversation = Selected.IsProject && Settings.Projects.Contains(Selected.WorkspacePath, StringComparer.OrdinalIgnoreCase)
            ? new() { WorkspacePath = Selected.WorkspacePath, IsProject = true } : CreateRecent();
        Conversations.Insert(0, conversation); Selected = conversation;
        SidebarChanged(); Save();
    }
    private static Conversation CreateRecent()
    {
        Conversation conversation = new();
        conversation.WorkspacePath = Path.Combine(new DesktopSettings().Workspace, conversation.CreatedAt.ToString("yyyy-MM-dd"), conversation.Id.ToString("N"));
        return conversation;
    }
    public void NewRecent()
    {
        if (!CanManageChats) return;
        Conversation conversation = CreateRecent();
        Conversations.Insert(0, conversation); Selected = conversation; Save(); SidebarChanged();
    }
    public void AddProject(string path)
    {
        if (!CanManageChats) return;
        path = WorkspaceContext.Normalize(path);
        if (!Settings.Projects.Contains(path, StringComparer.OrdinalIgnoreCase)) Settings.Projects.Add(path);
        Conversation conversation = new() { WorkspacePath = path, IsProject = true };
        Conversations.Insert(0, conversation); Selected = conversation;
        state.Save("settings.json", Settings); Save(); SidebarChanged();
    }
    public void RemoveProject(string path)
    {
        if (!CanManageChats) { Status = "请先结束当前任务并处理待审批请求，再移除工作区。"; return; }
        Settings.Projects.RemoveAll(p => p.Equals(path, StringComparison.OrdinalIgnoreCase));
        state.Save("settings.json", Settings); SidebarChanged();
    }
    public void DeleteConversation(Conversation conversation)
    {
        if (!CanManageChats) { Status = "请先结束当前任务并处理待审批请求。"; return; }
        if (!Conversations.Contains(conversation)) return;
        // Validate the transcript target before changing the index; preserve all workspaces.
        store.FilePath(conversation.Id, conversation.CreatedAt);
        store.SaveIndex(Conversations.Where(c => c != conversation));
        try { store.Delete(conversation); }
        catch { store.SaveIndex(Conversations); throw; }
        Conversations.Remove(conversation);
        if (Conversations.Count == 0) Conversations.Add(CreateRecent());
        if (Selected == conversation) Selected = Conversations[0];
        foreach (Conversation remaining in Conversations) store.Save(remaining);
        store.SaveIndex(Conversations); SidebarChanged();
    }
    private void SidebarChanged() { Changed(nameof(FilteredConversations)); Changed(nameof(Projects)); Changed(nameof(Recent)); }
    public void RenameConversation(Conversation conversation, string title)
    {
        if (!CanManageChats) { Status = "请先结束当前任务并处理待审批请求。"; return; }
        if (conversation.HistoryUnavailable) { Status = "损坏的原记录已保留，请新建对话继续使用。"; return; }
        if (!Conversations.Contains(conversation)) return;
        title = title.Trim();
        if (title.Length is 0 or > 120 || title.Any(char.IsControl)) throw new ArgumentException("会话名称应为 1 至 120 个字符，不能包含换行。", nameof(title));
        string original = conversation.Title;
        conversation.Title = title;
        try { store.Save(conversation); store.SaveIndex(Conversations); }
        catch { conversation.Title = original; throw; }
        if (conversation == Selected) Changed(nameof(Title));
        SidebarChanged();
    }
    public void SetAccess(string mode)
    {
        if (mode is not ("approval" or "readOnly" or "fullAccess")) return;
        if (mode != Settings.AccessMode) automatic.Reset();
        Settings.AccessMode = mode; Changed(nameof(AccessLabel));
        foreach (ApprovalCard card in Approvals) card.Allowed = FileChanges.CanApprove(mode, card.Tool);
        _ = Refresh();
    }
    public void SaveSettings()
    {
        Settings.AccessMode = "approval"; Changed(nameof(AccessLabel)); automatic.Reset();
        timer.Stop(); runtime.Dispose(); Connected = false;
        Approvals.Clear(); Tasks.Clear(); Changed(nameof(CanSend));
        state.Save("settings.json", Settings);
        Changed(nameof(WorkspacePath));
    }
    public async Task Connect(CancellationToken cancellation = default)
    {
        if (Busy) return;
        using CancellationTokenSource attempt = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        connecting = attempt; Changed(nameof(IsConnecting)); Changed(nameof(ConnectionButtonLabel));
        Settings.AccessMode = "approval"; Changed(nameof(AccessLabel)); automatic.Reset();
        Busy = true; Connected = false; timer.Stop();
        Status = "正在连接盒子…";
        try
        {
            await runtime.Start(Settings, attempt.Token, message => Status = message);
            Connected = true; Status = "盒子已连接"; timer.Start();
            Changed(nameof(CanSend));
        }
        catch (Exception error) when (error is IOException or InvalidOperationException or JsonException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        { Status = error.Message; }
        catch (OperationCanceledException) { Status = "连接已停止，请检查网络后重试。"; }
        finally { connecting = null; Changed(nameof(IsConnecting)); Changed(nameof(ConnectionButtonLabel)); Busy = false; }
    }
    public async Task Send()
    {
        if (!CanSend || string.IsNullOrWhiteSpace(Input)) return;
        string message = Input.Trim();
        if (Attachments.Count > 0) message += "\n用户选择的 Windows 文件或项目目录（访问前请求本地审批）：\n" + string.Join("\n", Attachments);
        if (message.Length > 64000) { Status = "任务输入过长，请缩短后发送。"; return; }
        Input = ""; Attachments.Clear();
        if (selected.Messages.Count == 0 && selected.Title == "新任务") selected.Title = message.Split('\n')[0][..Math.Min(24, message.Split('\n')[0].Length)];
        Changed(nameof(Title)); Changed(nameof(FilteredConversations));
        Add(new() { Role = "user", Text = message });
        Message reply = new(); activeReply = reply;
        TurnTranscript response = new(reply); transcript = response;
        using TurnProgress progress = new(reply);
        Changes.Clear(); tracked.Clear(); completed.Clear(); originals.Clear(); turnStarted = DateTimeOffset.UtcNow;
        Add(reply);
        userStopped = false;
        using IdleDeadline deadline = new(TimeSpan.FromMinutes(30));
        idle = deadline; turn = deadline.Source;
        Busy = true; Status = "智能体正在处理任务…";
        try
        {
            await runtime.Gateway!.Send(selected.Id, message, Settings.Workspace, text =>
            {
                response.Append(text);
                if (!string.IsNullOrWhiteSpace(text)) deadline.Signal();
                Changed(nameof(ContextInfo));
            }, turn.Token);
            response.Complete();
            if (string.IsNullOrWhiteSpace(reply.Text)) reply.Text = "任务已返回，请核对右侧工具执行结果。";
            await Refresh();
            ExecutorTask[] executed = (await runtime.Executor!.Tasks(turn.Token)).Where(t => tracked.ContainsKey(t.RequestId)).ToArray();
            Status = executed.Length == 0 ? "模型已回复，尚无本地执行回执" : "模型已回复，请核对本地执行回执";
        }
        catch (Exception error) when (error is OperationCanceledException or IOException or System.Net.Http.HttpRequestException or InvalidOperationException or JsonException)
        {
            response.Interrupt();
            Add(new() { Role = "system", Text = "等待已结束，执行结果可能未知。请核对任务记录和实际文件；不会自动重新发送。\n" + (error is OperationCanceledException ? userStopped ? "用户已停止等待。" : "连续 30 分钟没有收到回复或工具进展，已停止等待。" : error.Message) });
            Status = "请核对执行结果";
        }
        finally { progress.Dispose(); transcript = null; idle = null; turn = null; Busy = false; Save(); await Refresh(); }
    }
    public async Task Stop()
    {
        userStopped = true;
        turn?.Cancel();
        if (runtime.Executor is not null)
            try { await runtime.Executor.Cancel(CancellationToken.None); }
            catch (Exception error) when (error is IOException or System.Net.Http.HttpRequestException or OperationCanceledException or JsonException) { Status = "连接异常，取消结果未知。"; }
    }
    private void Add(Message message) { selected.Messages.Add(message); Messages.Add(message); Save(); }
    private void Save() { selected.UpdatedAt = DateTimeOffset.Now; store.Save(selected); store.SaveIndex(Conversations); Changed(nameof(ContextInfo)); }
    public async Task Refresh()
    {
        if (!Connected || refreshing) return;
        refreshing = true;
        try
        {
            if (!runtime.Alive) throw new InvalidOperationException("盒子连接已断开。核对任务结果后重新连接。");
            ToolRequest[] pending = await runtime.Executor!.Pending(CancellationToken.None);
            foreach (ApprovalCard old in Approvals.Where(a => !pending.Any(p => p.Call.RequestId == a.Request.Call.RequestId)).ToArray()) Approvals.Remove(old);
            foreach (ToolRequest request in pending)
                if (!Approvals.Any(a => a.Request.Call.RequestId == request.Call.RequestId))
                {
                    Approvals.Add(new(request) { Allowed = FileChanges.CanApprove(Settings.AccessMode, request.Call.Tool) });
                    if (activeReply is not null && request.CreatedAt >= turnStarted)
                    {
                        transcript?.SealBeforeTool();
                        Record(request.Call.RequestId, "请求 " + request.Call.Tool, new ApprovalCard(request).Summary, ProgressNarrator.Describe(request.Call, "pending", Settings.Workspace));
                    }
                }
            Tasks.Clear();
            foreach (ExecutorTask task in (await runtime.Executor.Tasks(CancellationToken.None)).OrderBy(t => t.CreatedAt))
            {
                if (!tracked.TryGetValue(task.RequestId, out var tracking) || activeReply is null) continue;
                Tasks.Add(new(task));
                Record(task.RequestId, new TaskCard(task).Status + " · " + task.Tool, new TaskCard(task).Detail + "\n" + new TaskCard(task).Output, ProgressNarrator.Describe(tracking.Call, task.Status, Settings.Workspace, task));
                if (task.Status == TaskStates.Succeeded && FileChanges.IsMutation(task.Tool) && completed.Add(task.RequestId))
                {
                    string path = FileChanges.Target(tracking.Call);
                    string? hash = task.Result?.TryGetProperty("sha256", out var sha) == true ? sha.GetString() : null;
                    UpdateChange(path, tracking.Before, FileChanges.Capture(path, Settings.Workspace, hash));
                    if (tracking.Source is not null && tracking.Call.Arguments.TryGetProperty("path", out var source))
                        UpdateChange(source.GetString()!, tracking.Source, new(false, ""));
                    Save();
                }
            }
            if (Approvals.Count > 0) Status = $"等待你确认 {Approvals.Count} 个工具请求";
            await automatic.Apply(Settings.AccessMode, Approvals.Select(a => a.Request).ToArray(), async request =>
            {
                ApprovalCard? card = Approvals.FirstOrDefault(a => a.Request.Call.RequestId == request.Call.RequestId);
                if (Settings.AccessMode == "fullAccess" && card is not null) await Decide(card, true);
            });
        }
        catch (Exception error) when (error is IOException or System.Net.Http.HttpRequestException or InvalidOperationException or TaskCanceledException or JsonException)
        { Connected = false; timer.Stop(); Status = "连接中断，请核对任务后重新连接。"; Changed(nameof(CanSend)); }
        finally { refreshing = false; }
    }
    public async Task Decide(ApprovalCard card, bool approve)
    {
        if (!Connected || card.Deciding || (approve && !FileChanges.CanApprove(Settings.AccessMode, card.Tool))) return;
        card.Deciding = true;
        try
        {
            if (approve && activeReply is not null && card.Request.CreatedAt >= turnStarted)
            {
                var call = card.Request.Call;
                FileChanges.Snapshot? source = call.Tool == "file.move" && call.Arguments.TryGetProperty("path", out var sourcePath)
                    ? FileChanges.Capture(sourcePath.GetString()!, Settings.Workspace) : null;
                tracked[call.RequestId] = (call, FileChanges.IsMutation(call.Tool) ? FileChanges.Capture(FileChanges.Target(call), Settings.Workspace) : new(false, ""), source);
            }
            await runtime.Executor!.Decide(card.Request, approve, CancellationToken.None);
            Approvals.Remove(card); Status = approve ? "已批准，正在执行…" : "已拒绝该请求";
            if (activeReply is not null) Record(card.Request.Call.RequestId, (approve ? "已批准 · " : "已拒绝 · ") + card.Tool, card.Summary, ProgressNarrator.Describe(card.Request.Call, approve ? "approved" : "rejected", Settings.Workspace));
            Save();
        }
        catch (Exception error) when (error is System.Net.Http.HttpRequestException or OperationCanceledException)
        { Status = "决定未确认，先刷新请求状态。"; }
        finally { card.Deciding = false; await Refresh(); }
    }
    private void Record(Guid requestId, string description, string detail, string? progressText = null)
    {
        if (activeReply is null) return;
        int index = activeReply.Activities.ToList().FindIndex(a => a.RequestId == requestId);
        ActivityRecord record = new(requestId, description, detail);
        if (index < 0) activeReply.Activities.Add(record);
        else { if (activeReply.Activities[index] == record) return; activeReply.Activities[index] = record; }
        idle?.Signal();
        if (progressText is not null)
        {
            ProgressEntry? entry = activeReply.ProgressEntries.FirstOrDefault(p => p.RequestId == requestId);
            if (entry is null) { entry = new() { Kind = description.Contains("project.", StringComparison.Ordinal) ? "command" : "tool", RequestId = requestId }; activeReply.ProgressEntries.Add(entry); }
            entry.Text = ProgressEntry.Concise(progressText);
            entry.Detail = description + "\n" + detail;
        }
        activeReply.Changed(nameof(Message.Activities));
        store.Save(selected);
    }
    private void UpdateChange(string path, FileChanges.Snapshot before, FileChanges.Snapshot after)
    {
        if (activeReply is null) return;
        if (!originals.ContainsKey(path)) originals[path] = before;
        FileChange change = FileChanges.Compare(path, Settings.Workspace, originals[path], after);
        FileChange? oldChange = activeReply.Changes.FirstOrDefault(c => c.Path.Equals(path, StringComparison.OrdinalIgnoreCase));
        if (oldChange is not null) { activeReply.Changes.Remove(oldChange); Changes.Remove(oldChange); }
        activeReply.Changes.Add(change); Changes.Add(change);
    }
    public void Attach(string path)
    {
        string full = Path.GetFullPath(path), root = Path.GetFullPath(Settings.Workspace);
        if (full.StartsWith(root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) Attachments.Add(full);
        else
        {
            Directory.CreateDirectory(root);
            string target = Path.Combine(root, Guid.NewGuid().ToString("N")[..8] + "-" + Path.GetFileName(full));
            File.Copy(full, target, overwrite: false);
            Attachments.Add(target);
        }
    }
    public void Dispose() { timer.Stop(); connecting?.Cancel(); turn?.Cancel(); runtime.Dispose(); }
}

public sealed class ApprovalCard(ToolRequest request) : Observable
{
    private bool deciding;
    private bool allowed = true;
    public bool Allowed { get => allowed; set { Set(ref allowed, value); Changed(nameof(CanDecide)); } }
    public ToolRequest Request { get; } = request;
    public string Tool => Request.Call.Tool;
    public string Parameters => DisplayText(JsonSerializer.Serialize(Request.Call.Arguments,
        new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }));
    public string Summary => DisplayText(Request.Call.Arguments.TryGetProperty("path", out JsonElement path) ? path.GetString()! : Tool);
    public string Digest => Request.RequestHash;
    public string Expiry => "有效期至 " + Request.ExpiresAt.ToLocalTime().ToString("HH:mm:ss");
    public bool CanDecide => !Deciding && Allowed;
    public bool CanReject => !Deciding;
    public bool Deciding { get => deciding; set { Set(ref deciding, value); Changed(nameof(CanDecide)); Changed(nameof(CanReject)); } }
    public static string DisplayText(string text) => string.Concat(text.Select(c =>
        char.GetUnicodeCategory(c) is System.Globalization.UnicodeCategory.Format or System.Globalization.UnicodeCategory.LineSeparator or System.Globalization.UnicodeCategory.ParagraphSeparator
            ? "\\u" + ((int)c).ToString("x4") : c.ToString()));
}

public sealed class TaskCard(ExecutorTask task)
{
    private bool CommandFailed => task.Result?.TryGetProperty("commandPassed", out JsonElement value) == true && value.ValueKind == JsonValueKind.False;
    public bool IsRunning => task.Status is TaskStates.Queued or TaskStates.Running;
    public string Tool => task.Tool;
    public string Status => task.Status == "succeeded" && CommandFailed ? "命令未成功" : task.Status == "succeeded" && task.Result?.TryGetProperty("checksPassed", out JsonElement passed) == true && passed.ValueKind == JsonValueKind.False ? "检查未通过" : task.Status switch { "succeeded" => "已完成", "failed" => "执行失败", "queued" => "排队中", "running" => "执行中", "canceled" => "已取消", "interrupted" => "已中断", _ => task.Status };
    public string Output => task.Result?.TryGetProperty("output", out JsonElement output) == true ? ApprovalCard.DisplayText(output.GetString() ?? "") : "";
    public string Detail => task.ErrorCode is string error ? error switch
    {
        "directory_missing" => "目录不存在，请先创建目录（directory_missing）",
        "file_conflict" => "文件已变化或目标已存在，请重新读取核对（file_conflict）",
        "search_not_found" => "没有找到匹配文字，原文件未修改（search_not_found）",
        "paragraph_not_found" => "没有找到匹配段落，原文档未修改（paragraph_not_found）",
        "unsupported_text_encoding" => "不是支持的 UTF-8 文本，DOCX 请使用 Word 工具（unsupported_text_encoding）",
        _ => error
    } : (task.Result?.TryGetProperty("path", out JsonElement path) == true ? path.GetString()! : task.Id.ToString());
    public string? OutputPath => task.Tool is not ("file.list" or "project.check" or "project.command" or "directory.create" or "directory.list" or "file.search") && task.Status == TaskStates.Succeeded && task.Result?.TryGetProperty("path", out JsonElement path) == true ? path.GetString() : null;
}
