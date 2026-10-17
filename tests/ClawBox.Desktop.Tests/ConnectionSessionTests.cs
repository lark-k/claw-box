using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Windows.Controls;
using ClawBox.Desktop.Models;
using ClawBox.Desktop.Services;
using ClawBox.Desktop.ViewModels;

namespace ClawBox.Desktop.Tests;

public sealed class ConnectionSessionTests
{
    [Fact]
    public void Opening_connected_guide_uses_current_endpoint_and_does_not_request_password_or_reconnect() => RunSta(() =>
    {
        using Fixture fixture = new(); fixture.Connect();
        ConnectionSetupWindow guide = fixture.Open();
        try
        {
            Assert.Equal("192.168.137.2", Field<TextBox>(guide, "host").Text);
            Assert.Equal("22", Field<TextBox>(guide, "port").Text);
            Assert.False(Field<TextBox>(guide, "host").IsEnabled);
            Assert.False(Field<PasswordBox>(guide, "password").IsEnabled);
            Assert.Equal("断开连接", Field<Button>(guide, "reconnect").Content);
            foreach (string step in new[] { "network", "identity", "verify" }) Assert.Equal("✓ 已通过", Rows(guide)[step].Status.Text);
            Assert.All(Rows(guide).Values, row => Assert.NotEqual("等待", row.Status.Text));
            Assert.Equal("未核验", Rows(guide)["boardIp"].Status.Text);
            Assert.Equal("未核验", Rows(guide)["internet"].Status.Text);
            Field<PasswordBox>(guide, "password").Password = "fixture-only";
            Assert.Equal("", Field<PasswordBox>(guide, "password").Password);
            Assert.False(Field<Button>(guide, "provision").IsEnabled);
        }
        finally { guide.Close(); }
    });

    [Fact]
    public void Disconnect_from_guide_updates_both_views_and_keeps_pairing_and_history() => RunSta(() =>
    {
        using Fixture fixture = new(); fixture.Connect();
        ConnectionSetupWindow guide = fixture.Open();
        try
        {
            int historyCount = fixture.Model.Conversations.Count;
            ((Task)Invoke(guide, "Reconnect")!).GetAwaiter().GetResult();
            Assert.False(fixture.Model.Connected); Assert.False(fixture.Model.CanSend);
            Assert.Equal("连接盒子", fixture.Model.ConnectionButtonLabel);
            foreach (string step in new[] { "network", "identity", "internet", "board", "verify" }) Assert.Equal("已断开", Rows(guide)[step].Status.Text);
            Assert.Equal("已保存", Rows(guide)["deploy"].Status.Text);
            Assert.Equal("使用已有配对连接", Field<Button>(guide, "reconnect").Content);
            Assert.True(Field<Button>(guide, "reconnect").IsEnabled);
            Assert.True(Field<TextBox>(guide, "host").IsEnabled);
            Assert.Contains("已断开", Field<TextBlock>(guide, "summary").Text);
            Invoke(guide, "BeginChecks");
            Assert.False(Field<bool>(guide, "networkChecksEnabled"));
            Assert.Equal("fixture-pairing", File.ReadAllText(fixture.Model.Settings.ProfilePath));
            Assert.Equal("fixture-token-placeholder", File.ReadAllText(fixture.Model.Settings.GatewayTokenFile));
            Assert.Equal(historyCount, fixture.Model.Conversations.Count);
            Assert.Equal("请求批准", fixture.Model.AccessLabel);
        }
        finally { guide.Close(); }
        ConnectionSetupWindow reopened = fixture.Open();
        try { Assert.Equal("已断开", Rows(reopened)["verify"].Status.Text); }
        finally { reopened.Close(); }
    });

    [Fact]
    public void Disconnect_invalidates_late_checks_and_external_reconnect_updates_open_guide() => RunSta(() =>
    {
        using Fixture fixture = new(); fixture.Connect();
        ConnectionSetupWindow guide = fixture.Open();
        try
        {
            int version = Field<ConnectionCheckGate>(guide, "autoGate").Version;
            Type progressType = typeof(ConnectionSetupWindow).GetNestedType("WindowProgress", BindingFlags.NonPublic)!;
            IProgress<SetupUpdate> old = (IProgress<SetupUpdate>)Activator.CreateInstance(progressType, guide, (int?)version, fixture.Device)!;
            fixture.Model.Disconnect();
            old.Report(new("verify", "ready", "stale result"));
            Assert.Equal("已断开", Rows(guide)["verify"].Status.Text);
            fixture.Connect();
            Assert.Equal("✓ 已通过", Rows(guide)["verify"].Status.Text);
            Assert.Equal("断开连接", Field<Button>(guide, "reconnect").Content);
            Assert.Equal("断开连接", fixture.Model.ConnectionButtonLabel);
        }
        finally { guide.Close(); }
    });

    [Fact]
    public void Detailed_checks_are_reused_only_for_the_same_verified_device() => RunSta(() =>
    {
        using Fixture fixture = new();
        Invoke(fixture.Model, "RememberCheck", fixture.Device, new SetupUpdate("boardIp", "ready", "fixture static IP"));
        Invoke(fixture.Model, "RememberCheck", fixture.Device, new SetupUpdate("internet", "ready", "fixture network"));
        fixture.Connect();
        ConnectionSetupWindow guide = fixture.Open();
        try
        {
            Assert.Equal("✓ 已通过", Rows(guide)["boardIp"].Status.Text);
            Assert.Contains("最近检查", Rows(guide)["internet"].Detail.Text);
            typeof(ShellViewModel).GetProperty(nameof(ShellViewModel.ConnectionDevice))!.SetValue(fixture.Model, fixture.Device with { Fingerprint = "different-device" });
            fixture.Model.Changed(nameof(ShellViewModel.Connected));
            Assert.Equal("未核验", Rows(guide)["boardIp"].Status.Text);
            Assert.Equal("未核验", Rows(guide)["internet"].Status.Text);
        }
        finally { guide.Close(); }
    });

    [Fact]
    public void Disconnect_during_a_task_requires_task_to_finish_without_silently_dropping_it() => RunSta(() =>
    {
        using Fixture fixture = new(); fixture.Connect();
        typeof(ShellViewModel).GetProperty(nameof(ShellViewModel.Busy))!.SetValue(fixture.Model, true);
        fixture.Model.Disconnect();
        Assert.True(fixture.Model.Connected); Assert.Contains("停止当前任务", fixture.Model.Status);
        typeof(ShellViewModel).GetProperty(nameof(ShellViewModel.Busy))!.SetValue(fixture.Model, false);
        fixture.Model.Disconnect(); Assert.False(fixture.Model.Connected);
    });

    [Fact]
    public void Replacing_board_disconnects_and_persists_fresh_pairing_without_touching_history_or_old_files() => RunSta(() =>
    {
        using Fixture fixture = new();
        ShellViewModel model = fixture.Model;
        string oldProfile = model.Settings.ProfilePath, oldGateway = model.Settings.GatewayTokenFile;
        string root = Path.GetDirectoryName(oldProfile)!;
        model.AddProject(Path.Combine(root, "project"));
        model.Selected.Messages.Add(new() { Text = "保留的聊天内容" });
        model.RenameConversation(model.Selected, "保留的会话");
        model.SetAccess("fullAccess");
        Guid chat = model.Selected.Id; string workspace = model.Settings.Workspace;
        string chatPath = new ConversationStore(new LocalState(root)).FilePath(chat, model.Selected.CreatedAt);
        byte[] history = File.ReadAllBytes(chatPath);
        string userFile = Path.Combine(root, "user-file.txt"); File.WriteAllText(userFile, "保留用户文件");
        fixture.Connect();
        Invoke(model, "RememberCheck", fixture.Device, new SetupUpdate("boardIp", "ready", "old device"));
        Assert.True(model.BeginBoardReplacement());
        Assert.False(model.Connected); Assert.False(model.HasConnectionAttempted); Assert.Null(model.ConnectionDevice);
        Assert.False(model.CanSend); Assert.Null(Invoke(model, "RememberedCheck", "boardIp"));
        Assert.True(Guid.TryParseExact(model.Settings.PairingId, "N", out _));
        Assert.False(File.Exists(model.Settings.ProfilePath)); Assert.False(File.Exists(model.Settings.GatewayTokenFile));
        Assert.NotEqual(oldProfile, model.Settings.ProfilePath);
        Assert.Equal("fixture-pairing", File.ReadAllText(oldProfile));
        Assert.Equal("fixture-token-placeholder", File.ReadAllText(oldGateway));
        Assert.Equal(history, File.ReadAllBytes(chatPath)); Assert.Equal("保留用户文件", File.ReadAllText(userFile));
        Assert.Equal(chat, model.Selected.Id); Assert.Equal(workspace, model.Settings.Workspace); Assert.Equal("完全访问", model.AccessLabel);
        using ShellViewModel restarted = new(new LocalState(root));
        Assert.Equal(model.Settings.PairingId, restarted.Settings.PairingId);
        Assert.Equal(model.Settings.ProfilePath, restarted.Settings.ProfilePath);
        Assert.Equal(model.Settings.GatewayTokenFile, restarted.Settings.GatewayTokenFile);
        Assert.Equal("保留的聊天内容", Assert.Single(restarted.Conversations.Single(c => c.Id == chat).Messages).Text);
        ConnectionSetupWindow guide = fixture.Open();
        try
        {
            Assert.True(Field<PasswordBox>(guide, "password").IsEnabled);
            Assert.False(Field<Button>(guide, "reconnect").IsEnabled);
            Assert.NotEqual("✓ 已通过", Rows(guide)["identity"].Status.Text);
        }
        finally { guide.Close(); }
    });

    [Theory]
    [InlineData("busy")]
    [InlineData("approval")]
    [InlineData("task")]
    public void Replacement_is_blocked_while_work_or_approval_is_pending(string mode) => RunSta(() =>
    {
        using Fixture fixture = new(); fixture.Connect(); ShellViewModel model = fixture.Model;
        string profile = model.Settings.ProfilePath;
        using System.Text.Json.JsonDocument arguments = System.Text.Json.JsonDocument.Parse("{\"path\":\"file.txt\"}");
        if (mode == "busy") typeof(ShellViewModel).GetProperty(nameof(ShellViewModel.Busy))!.SetValue(model, true);
        if (mode == "approval") model.Approvals.Add(new(new(new(Guid.NewGuid(), "file.read", arguments.RootElement.Clone()), new string('A', 64), "pending", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddMinutes(5), null)));
        if (mode == "task") model.Tasks.Add(new(new(Guid.NewGuid(), Guid.NewGuid(), "file.write", "running", DateTimeOffset.UtcNow, null, null, null)));
        Assert.False(model.BeginBoardReplacement());
        Assert.True(model.Connected); Assert.Equal(profile, model.Settings.ProfilePath); Assert.Equal("", model.Settings.PairingId);
        Assert.Equal("fixture-pairing", File.ReadAllText(profile));
        Assert.Contains("请先结束", model.Status);
    });

    [Fact]
    public void Failed_replacement_settings_save_keeps_previous_pairing_and_connection() => RunSta(() =>
    {
        using Fixture fixture = new(); fixture.Model.SaveSettings(); fixture.Connect();
        string profile = fixture.Model.Settings.ProfilePath, gateway = fixture.Model.Settings.GatewayTokenFile;
        string settings = Path.Combine(Path.GetDirectoryName(profile)!, "settings.json");
        byte[] original = File.ReadAllBytes(settings);
        File.SetAttributes(settings, FileAttributes.ReadOnly);
        try { Assert.Throws<UnauthorizedAccessException>(() => fixture.Model.BeginBoardReplacement()); }
        finally { File.SetAttributes(settings, FileAttributes.Normal); }
        Assert.True(fixture.Model.Connected); Assert.Equal(fixture.Device, fixture.Model.ConnectionDevice);
        Assert.Equal("", fixture.Model.Settings.PairingId); Assert.Equal(profile, fixture.Model.Settings.ProfilePath);
        Assert.Equal(gateway, fixture.Model.Settings.GatewayTokenFile); Assert.Equal(original, File.ReadAllBytes(settings));
    });

    private sealed class Fixture : IDisposable
    {
        private readonly string root = Path.Combine(Path.GetTempPath(), "QuantumHarness.Session.Tests", Guid.NewGuid().ToString("N"));
        public ShellViewModel Model { get; }
        public SetupRequest Device { get; } = new("192.168.137.2", 22, "fixture-identity");
        public Fixture()
        {
            Model = new(new LocalState(root));
            Model.Settings.ProfilePath = Path.Combine(root, "connection.json");
            Model.Settings.GatewayTokenFile = Path.Combine(root, "gateway.secrets.json");
            File.WriteAllText(Model.Settings.ProfilePath, "fixture-pairing");
            File.WriteAllText(Model.Settings.GatewayTokenFile, "fixture-token-placeholder");
        }
        public void Connect()
        {
            typeof(ShellViewModel).GetProperty(nameof(ShellViewModel.ConnectionDevice))!.SetValue(Model, Device);
            typeof(ShellViewModel).GetProperty(nameof(ShellViewModel.Connected))!.SetValue(Model, true);
            Model.Status = "盒子已连接";
        }
        public ConnectionSetupWindow Open() => new(Model.Settings, _ => throw new InvalidOperationException("Opening or disconnecting must not connect."), preview: true, session: Model);
        public void Dispose() { Model.Dispose(); Directory.Delete(root, true); }
    }
    private static Dictionary<string, (TextBlock Status, TextBlock Detail)> Rows(ConnectionSetupWindow guide) => Field<Dictionary<string, (TextBlock Status, TextBlock Detail)>>(guide, "rows");
    private static T Field<T>(object target, string name) => (T)target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(target)!;
    private static object? Invoke(object target, string name, params object[] args) => target.GetType().GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(target, args);
    private static void RunSta(Action action)
    {
        Exception? failure = null;
        Thread thread = new(() => { try { action(); } catch (Exception error) { failure = error; } });
        thread.SetApartmentState(ApartmentState.STA); thread.Start(); thread.Join();
        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
    }
}
