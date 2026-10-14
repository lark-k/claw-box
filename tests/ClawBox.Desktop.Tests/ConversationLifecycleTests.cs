using System.Runtime.ExceptionServices;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using ClawBox.Contracts;
using ClawBox.Desktop.Models;
using ClawBox.Desktop.Services;
using ClawBox.Desktop.ViewModels;

namespace ClawBox.Desktop.Tests;

public sealed class ConversationLifecycleTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "QuantumHarness.Tests", Guid.NewGuid().ToString("N"));
    [Fact]
    public void Migration_and_deletion_reclaim_transcript_without_deleting_user_files()
    {
        LocalState state = new(Path.Combine(root, "desktop"));
        string workspace = Path.Combine(root, "user-project"); Directory.CreateDirectory(workspace);
        string userFile = Path.Combine(workspace, "important.txt"); File.WriteAllText(userFile, "keep me");
        Conversation first = new() { WorkspacePath = workspace, Messages = [new() { Text = new string('x', 100000) }] };
        Conversation second = new() { WorkspacePath = workspace, Title = "保留会话" };
        state.Save("conversations.json", new[] { first, second });
        ConversationStore store = new(state);
        store.Migrate(store.Load());
        Assert.False(File.Exists(Path.Combine(state.DirectoryPath, "conversations.json")));
        string transcript = store.FilePath(first.Id, first.CreatedAt);
        Assert.Contains(first.CreatedAt.ToString("yyyy-MM-dd"), transcript);
        Assert.True(new FileInfo(transcript).Length > 100000);
        store.SaveIndex([second]); store.Delete(first);
        Assert.False(File.Exists(transcript));
        Assert.Equal("keep me", File.ReadAllText(userFile));
        Assert.Equal(second.Id, Assert.Single(store.Load()).Id);
    }
    [Fact]
    public void Removal_only_hides_project_and_recent_conversations_keep_their_workspace()
    {
        RunSta(() =>
        {
            LocalState state = new(Path.Combine(root, "desktop"));
            using ShellViewModel model = new(state);
            string project = Path.Combine(root, "project"); Directory.CreateDirectory(project);
            model.AddProject(project);
            Conversation projectChat = model.Selected;
            model.NewRecent(); Conversation recent = model.Selected;
            Assert.Contains(recent.CreatedAt.ToString("yyyy-MM-dd"), recent.WorkspacePath);
            Assert.EndsWith(recent.Id.ToString("N"), recent.WorkspacePath);
            model.Selected = projectChat;
            Assert.Equal(project, model.Settings.Workspace);
            model.RemoveProject(project);
            Assert.Empty(model.Projects);
            Assert.Contains(projectChat, model.Recent);
            Assert.True(Directory.Exists(project));
            model.AddProject(project);
            Assert.Contains(projectChat, Assert.Single(model.Projects).Conversations);
            model.SetAccess("fullAccess");
            Assert.Equal("完全访问", model.AccessLabel);
            using ShellViewModel restarted = new(state);
            Assert.Equal("逐请求审批", restarted.AccessLabel);
            Assert.Contains(restarted.Conversations, c => c.Id == projectChat.Id && c.WorkspacePath == project);
            restarted.DeleteConversation(restarted.Conversations.Single(c => c.Id == projectChat.Id));
            Assert.DoesNotContain(restarted.Conversations, c => c.Id == projectChat.Id);
            Assert.True(Directory.Exists(project));
        });
    }
    [Fact]
    public void Index_cannot_redirect_transcript_identity()
    {
        LocalState state = new(Path.Combine(root, "desktop")); ConversationStore store = new(state);
        Conversation original = new(); store.Migrate([original]);
        File.WriteAllText(store.FilePath(original.Id, original.CreatedAt), JsonSerializer.Serialize(new Conversation()));
        Assert.Throws<InvalidOperationException>(() => store.Load());
    }
    [Fact]
    public void Pending_tools_prevent_workspace_switch_and_deletion()
    {
        RunSta(() =>
        {
            using ShellViewModel model = new(new LocalState(Path.Combine(root, "desktop")));
            Conversation first = model.Selected; model.NewRecent(); Conversation second = model.Selected;
            model.Tasks.Add(new(new(Guid.NewGuid(), Guid.NewGuid(), "file.write", "running", DateTimeOffset.UtcNow, null, null, null)));
            model.Selected = first; model.DeleteConversation(second); model.NewRecent();
            Assert.Equal(second, model.Selected);
            Assert.Equal(2, model.Conversations.Count);
            Assert.Contains(second, model.Conversations);
            model.Tasks.Clear(); model.Selected = first;
            Assert.Equal(first, model.Selected);
        });
    }
    [Fact]
    public void Binary_and_outside_workspace_are_not_read_as_text()
    {
        string workspace = Path.Combine(root, "project"); Directory.CreateDirectory(workspace);
        string binary = Path.Combine(workspace, "image.bin"); File.WriteAllBytes(binary, [1, 0, 2]);
        Assert.Null(FileChanges.Capture(binary, workspace).Text);
        string outside = Path.Combine(root, "private.txt"); File.WriteAllText(outside, "secret");
        Assert.Null(FileChanges.Capture(outside, workspace).Text);
    }
    [Fact]
    public void Real_diff_counts_added_and_removed_lines_and_hash_conflicts_are_not_presented_as_content()
    {
        string workspace = Path.Combine(root, "project"); Directory.CreateDirectory(workspace);
        string path = Path.Combine(workspace, "sample.cs"); File.WriteAllText(path, "keep\nold\n");
        var before = FileChanges.Capture(path, workspace);
        File.WriteAllText(path, "keep\nnew\nextra\n");
        FileChange change = FileChanges.Compare(path, workspace, before, FileChanges.Capture(path, workspace));
        Assert.Equal("+2", change.Added); Assert.Equal("-1", change.Removed);
        Assert.Equal("sample.cs", change.DisplayPath);
        Assert.Contains(change.Lines, l => l.Kind == "context" && l.Text.Contains("keep"));
        Assert.Null(FileChanges.Capture(path, workspace, new string('0', 64)).Text);
        Assert.False(FileChanges.Compare(path, workspace, before, new(true, null)).HasDiff);
    }
    [Theory]
    [InlineData("readOnly", "file.read", true)]
    [InlineData("readOnly", "file.write", false)]
    [InlineData("readOnly", "project.check", false)]
    [InlineData("approval", "file.write", true)]
    [InlineData("fullAccess", "file.write", true)]
    public void Access_modes_preserve_read_only_boundary(string mode, string tool, bool allowed) => Assert.Equal(allowed, FileChanges.CanApprove(mode, tool));
    [Fact]
    public async Task Full_access_submits_each_exact_request_with_admin_credentials_once_and_never_replays_after_failure()
    {
        using JsonDocument arguments = JsonDocument.Parse("""{"path":"C:\\workspace\\note.txt","content":"hello"}""");
        ToolRequest request = new(new(Guid.NewGuid(), "file.write", arguments.RootElement.Clone()), new string('A', 64), "pending", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddMinutes(5), null);
        using DecisionHandler handler = new(request);
        using ExecutorClient client = new("admin-fixture", "worker-fixture", handler: handler);
        AutomaticApprovals automatic = new();
        Task Approve(ToolRequest item) => client.Decide(item, true, CancellationToken.None);
        await automatic.Apply("approval", [request], Approve);
        await automatic.Apply("readOnly", [request], Approve);
        Assert.Equal(0, handler.Count);
        await automatic.Apply("fullAccess", [request], Approve);
        await automatic.Apply("fullAccess", [request], Approve);
        Assert.Equal(1, handler.Count);
        await automatic.Apply("fullAccess", [request with { Call = request.Call with { RequestId = Guid.NewGuid() }, ExpiresAt = DateTimeOffset.UtcNow.AddSeconds(-1) }], Approve);
        Assert.Equal(1, handler.Count);
        automatic.Reset(); handler.Fail = true;
        await Assert.ThrowsAsync<HttpRequestException>(() => automatic.Apply("fullAccess", [request], Approve));
        await automatic.Apply("fullAccess", [request], Approve);
        Assert.Equal(2, handler.Count);
    }
    private sealed class DecisionHandler(ToolRequest expected) : HttpMessageHandler
    {
        public int Count { get; private set; }
        public bool Fail { get; set; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Count++;
            Assert.Equal("admin-fixture", request.Headers.Authorization?.Parameter);
            Assert.EndsWith(expected.Call.RequestId + "/decision", request.RequestUri!.AbsolutePath);
            using JsonDocument body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken));
            Assert.Equal(expected.RequestHash, body.RootElement.GetProperty("requestHash").GetString());
            Assert.True(body.RootElement.GetProperty("approve").GetBoolean());
            return new(Fail ? HttpStatusCode.InternalServerError : HttpStatusCode.OK);
        }
    }
    private static void RunSta(Action action)
    {
        Exception? error = null;
        Thread thread = new(() => { try { action(); } catch (Exception caught) { error = caught; } });
        thread.SetApartmentState(ApartmentState.STA); thread.Start(); thread.Join();
        if (error is not null) ExceptionDispatchInfo.Capture(error).Throw();
    }
    public void Dispose() { if (Directory.Exists(root)) Directory.Delete(root, true); }
}
