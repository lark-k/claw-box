using System.Runtime.ExceptionServices;
using ClawBox.Desktop.Models;
using ClawBox.Desktop.Services;
using ClawBox.Desktop.ViewModels;

namespace ClawBox.Desktop.Tests;

public sealed class AccessPreferenceTests
{
    [Theory]
    [InlineData("approval", "请求批准", "\uE815", "#5B5C60")]
    [InlineData("readOnly", "只读", "\uE890", "#377DF5")]
    [InlineData("fullAccess", "完全访问", "\uEA18", "#F06A22")]
    public void Selection_is_saved_immediately_and_survives_chat_settings_disconnect_failed_reconnect_and_restart(string mode, string label, string icon, string color) => RunSta(() =>
    {
        string root = Path.Combine(Path.GetTempPath(), "QuantumHarness.Access.Tests", Guid.NewGuid().ToString("N"));
        try
        {
            LocalState state = new(root);
            state.Save("settings.json", new DesktopSettings { ProfilePath = Path.Combine(root, "missing-profile.json"), GatewayTokenFile = Path.Combine(root, "missing-gateway.json") });
            using ShellViewModel model = new(state);
            List<string?> changed = [];
            model.PropertyChanged += (_, e) => changed.Add(e.PropertyName);
            model.SetAccess(mode);
            Assert.Equal(mode, state.Load("settings.json", () => new DesktopSettings()).AccessMode);
            Assert.Contains(nameof(model.AccessIcon), changed); Assert.Contains(nameof(model.AccessForeground), changed);
            model.NewRecent(); model.SaveSettings(); model.Disconnect();
            model.Connect().GetAwaiter().GetResult();
            Assert.False(model.Connected);
            Assert.Equal(mode, model.Settings.AccessMode);
            using ShellViewModel reopened = new(state);
            Assert.Equal(label, reopened.AccessLabel); Assert.Equal(icon, reopened.AccessIcon); Assert.Equal(color, reopened.AccessForeground);
        }
        finally { Directory.Delete(root, true); }
    });
    [Theory]
    [InlineData("unknown")]
    [InlineData("")]
    [InlineData(null)]
    public void Unknown_saved_mode_falls_back_to_request_approval(string? mode) => RunSta(() =>
    {
        string root = Path.Combine(Path.GetTempPath(), "QuantumHarness.Access.Tests", Guid.NewGuid().ToString("N"));
        try
        {
            LocalState state = new(root); state.Save("settings.json", new DesktopSettings { AccessMode = mode! });
            using ShellViewModel model = new(state);
            Assert.Equal("approval", model.Settings.AccessMode);
            model.SetAccess("unexpected"); Assert.Equal("approval", model.Settings.AccessMode);
        }
        finally { Directory.Delete(root, true); }
    });
    [Fact]
    public void Preview_is_bounded_and_updates_with_streaming_content()
    {
        Message message = new() { Text = "  hello  " };
        Assert.Equal("hello", message.PreviewText);
        List<string?> changed = []; message.PropertyChanged += (_, e) => changed.Add(e.PropertyName);
        message.Text = new string('中', 900);
        Assert.Contains(nameof(message.PreviewText), changed); Assert.Equal(601, message.PreviewText.Length);
        message.Text = ""; message.IsProcessing = true;
        Assert.Contains("正在处理", message.PreviewText);
    }
    private static void RunSta(Action action)
    {
        Exception? failure = null;
        Thread thread = new(() => { try { action(); } catch (Exception error) { failure = error; } });
        thread.SetApartmentState(ApartmentState.STA); thread.Start(); thread.Join();
        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
    }
}
