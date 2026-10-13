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
    private readonly RuntimeSession runtime = new();
    private readonly DispatcherTimer timer;
    private CancellationTokenSource? turn;
    private Conversation selected;
    private string status = "未连接盒子", input = "", search = "";
    private bool connected, busy, refreshing;
    public ObservableCollection<Conversation> Conversations { get; }
    public ObservableCollection<Message> Messages { get; } = [];
    public ObservableCollection<ApprovalCard> Approvals { get; } = [];
    public ObservableCollection<TaskCard> Tasks { get; } = [];
    public ObservableCollection<string> Attachments { get; } = [];
    public DesktopSettings Settings { get; }
    public string Status { get => status; set => Set(ref status, value); }
    public bool Connected { get => connected; private set => Set(ref connected, value); }
    public bool Busy { get => busy; private set { Set(ref busy, value); Changed(nameof(CanSend)); } }
    public bool CanSend => Connected && !Busy;
    public string Input { get => input; set => Set(ref input, value); }
    public string Search { get => search; set { Set(ref search, value); Changed(nameof(FilteredConversations)); } }
    public IEnumerable<Conversation> FilteredConversations => Conversations.Where(c => c.Title.Contains(Search, StringComparison.OrdinalIgnoreCase));
    public Conversation Selected
    {
        get => selected;
        set
        {
            if (Busy || value is null || value == selected) return;
            selected = value;
            Messages.Clear();
            foreach (Message message in selected.Messages) Messages.Add(message);
            Attachments.Clear();
            Changed(); Changed(nameof(Title));
        }
    }
    public string Title => Selected.Title;
    public string WorkspacePath => Settings.Workspace;
    public ShellViewModel(LocalState state)
    {
        this.state = state;
        Settings = state.Load("settings.json", () => new DesktopSettings());
        Conversations = new(state.Load("conversations.json", () => new List<Conversation>()));
        if (Conversations.Count == 0) Conversations.Add(new());
        selected = Conversations[0];
        foreach (Message message in selected.Messages) Messages.Add(message);
        timer = new(TimeSpan.FromMilliseconds(800), DispatcherPriority.Background, async (_, _) => await Refresh(), Dispatcher.CurrentDispatcher);
        timer.Stop();
    }
    public void NewConversation()
    {
        if (Busy) return;
        Attachments.Clear();
        Conversation conversation = new();
        Conversations.Insert(0, conversation); Selected = conversation;
        Changed(nameof(FilteredConversations)); Save();
    }
    public void SaveSettings()
    {
        timer.Stop(); runtime.Dispose(); Connected = false;
        Approvals.Clear(); Tasks.Clear(); Changed(nameof(CanSend));
        state.Save("settings.json", Settings);
        Changed(nameof(WorkspacePath));
    }
    public async Task Connect()
    {
        if (Busy) return;
        Busy = true; Connected = false; timer.Stop();
        Status = "正在连接盒子…";
        try
        {
            await runtime.Start(Settings, CancellationToken.None);
            Connected = true; Status = "盒子已连接"; timer.Start();
            Changed(nameof(CanSend));
        }
        catch (Exception error) when (error is IOException or InvalidOperationException or JsonException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        { Status = error.Message; }
        finally { Busy = false; }
    }
    public async Task Send()
    {
        if (!CanSend || string.IsNullOrWhiteSpace(Input)) return;
        string message = Input.Trim();
        if (Attachments.Count > 0) message += "\n用户选择的 Windows 文件或项目目录（访问前请求本地审批）：\n" + string.Join("\n", Attachments);
        if (message.Length > 64000) { Status = "任务输入过长，请缩短后发送。"; return; }
        Input = ""; Attachments.Clear();
        if (selected.Messages.Count == 0) selected.Title = message.Split('\n')[0][..Math.Min(24, message.Split('\n')[0].Length)];
        Changed(nameof(Title)); Changed(nameof(FilteredConversations));
        Add(new() { Role = "user", Text = message });
        Message reply = new();
        Add(reply);
        Busy = true; Status = "智能体正在处理任务…";
        turn = new CancellationTokenSource(TimeSpan.FromMinutes(10));
        try
        {
            DateTimeOffset started = DateTimeOffset.UtcNow;
            await runtime.Gateway!.Send(selected.Id, message, Settings.Workspace, text =>
            {
                if (reply.Text.Length + text.Length > 2 * 1024 * 1024) throw new InvalidOperationException("回复超过界面限制。");
                reply.Text += text;
            }, turn.Token);
            if (string.IsNullOrWhiteSpace(reply.Text)) reply.Text = "任务已返回，请核对右侧工具执行结果。";
            await Refresh();
            ExecutorTask[] executed = (await runtime.Executor!.Tasks(turn.Token)).Where(t => t.CreatedAt >= started).ToArray();
            string receipt = ExecutionReceipt.Summarize(executed, Settings.Workspace);
            Status = executed.Length == 0 ? "模型已回复，尚无本地执行回执" : "模型已回复，请核对本地执行回执";
            if (executed.Length > 0) Add(new() { Role = "system", Text = receipt });
        }
        catch (Exception error) when (error is OperationCanceledException or IOException or System.Net.Http.HttpRequestException or InvalidOperationException or JsonException)
        {
            Add(new() { Role = "system", Text = "等待已结束，执行结果可能未知。请核对任务记录和实际文件；不会自动重新发送。\n" + (error is OperationCanceledException ? "已停止等待。" : error.Message) });
            Status = "请核对执行结果";
        }
        finally { turn.Dispose(); turn = null; Busy = false; Save(); await Refresh(); }
    }
    public async Task Stop()
    {
        turn?.Cancel();
        if (runtime.Executor is not null)
            try { await runtime.Executor.Cancel(CancellationToken.None); }
            catch (Exception error) when (error is IOException or System.Net.Http.HttpRequestException or OperationCanceledException or JsonException) { Status = "连接异常，取消结果未知。"; }
    }
    private void Add(Message message) { selected.Messages.Add(message); Messages.Add(message); Save(); }
    private void Save() { selected.UpdatedAt = DateTimeOffset.Now; state.Save("conversations.json", Conversations.ToList()); }
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
                if (!Approvals.Any(a => a.Request.Call.RequestId == request.Call.RequestId)) Approvals.Add(new(request));
            Tasks.Clear();
            foreach (ExecutorTask task in (await runtime.Executor.Tasks(CancellationToken.None)).Take(30)) Tasks.Add(new(task));
            if (Approvals.Count > 0) Status = $"等待你确认 {Approvals.Count} 个工具请求";
        }
        catch (Exception error) when (error is IOException or System.Net.Http.HttpRequestException or InvalidOperationException or TaskCanceledException or JsonException)
        { Connected = false; timer.Stop(); Status = "连接中断，请核对任务后重新连接。"; Changed(nameof(CanSend)); }
        finally { refreshing = false; }
    }
    public async Task Decide(ApprovalCard card, bool approve)
    {
        if (!Connected || card.Deciding) return;
        card.Deciding = true;
        try { await runtime.Executor!.Decide(card.Request, approve, CancellationToken.None); Approvals.Remove(card); Status = approve ? "已批准，正在执行…" : "已拒绝该请求"; }
        catch (Exception error) when (error is System.Net.Http.HttpRequestException or OperationCanceledException)
        { Status = "决定未确认，先刷新请求状态。"; }
        finally { card.Deciding = false; await Refresh(); }
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
    public void Dispose() { timer.Stop(); turn?.Cancel(); runtime.Dispose(); }
}

public sealed class ApprovalCard(ToolRequest request) : Observable
{
    private bool deciding;
    public ToolRequest Request { get; } = request;
    public string Tool => Request.Call.Tool;
    public string Parameters => DisplayText(JsonSerializer.Serialize(Request.Call.Arguments,
        new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }));
    public string Summary => DisplayText(Request.Call.Arguments.TryGetProperty("path", out JsonElement path) ? path.GetString()! : Tool);
    public string Digest => Request.RequestHash;
    public string Expiry => "有效期至 " + Request.ExpiresAt.ToLocalTime().ToString("HH:mm:ss");
    public bool CanDecide => !Deciding;
    public bool Deciding { get => deciding; set { Set(ref deciding, value); Changed(nameof(CanDecide)); } }
    public static string DisplayText(string text) => string.Concat(text.Select(c =>
        char.GetUnicodeCategory(c) is System.Globalization.UnicodeCategory.Format or System.Globalization.UnicodeCategory.LineSeparator or System.Globalization.UnicodeCategory.ParagraphSeparator
            ? "\\u" + ((int)c).ToString("x4") : c.ToString()));
}

public sealed class TaskCard(ExecutorTask task)
{
    public string Tool => task.Tool;
    public string Status => task.Status == "succeeded" && task.Result?.TryGetProperty("checksPassed", out JsonElement passed) == true && passed.ValueKind == JsonValueKind.False ? "检查未通过" : task.Status switch { "succeeded" => "已完成", "failed" => "执行失败", "queued" => "排队中", "running" => "执行中", "canceled" => "已取消", "interrupted" => "已中断", _ => task.Status };
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
    public string? OutputPath => task.Tool is not ("file.list" or "project.check" or "directory.create" or "directory.list" or "file.search") && task.Status == TaskStates.Succeeded && task.Result?.TryGetProperty("path", out JsonElement path) == true ? path.GetString() : null;
}
