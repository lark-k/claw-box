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
