using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Windows.Controls;
using ClawBox.Desktop.Models;
using ClawBox.Desktop.Services;

namespace ClawBox.Desktop.Tests;

public sealed class ConnectionOrderTests
{
    [Fact]
    public async Task Component_only_check_does_not_probe_the_device_port()
    {
        using TcpListener listener = new(IPAddress.Loopback, 0); listener.Start();
        SetupUpdate[] checks = await BoardSetup.Diagnose("127.0.0.1", ((IPEndPoint)listener.LocalEndpoint).Port, CancellationToken.None, includeNetwork: false);
        Assert.Equal("local", Assert.Single(checks).Step); Assert.False(listener.Pending());
    }
    [Fact]
    public void Later_steps_wait_until_password_and_confirmation_then_reset_on_edit() => RunSta(() =>
    {
        ConnectionSetupWindow window = new(new DesktopSettings(), _ => throw new InvalidOperationException("No connection expected"), preview: true);
        try
        {
            var rows = Field<Dictionary<string, (TextBlock Status, TextBlock Detail)>>(window, "rows");
            foreach (string step in new[] { "subnet", "network", "identity", "internet", "board", "deploy", "verify" }) Assert.Equal("等待", rows[step].Status.Text);
            Invoke(window, "BeginChecks"); Assert.False(Field<bool>(window, "networkChecksEnabled"));
            Field<PasswordBox>(window, "password").Password = "fixture-only";
            Invoke(window, "BeginChecks"); Assert.False(Field<bool>(window, "networkChecksEnabled"));
            Field<CheckBox>(window, "consent").IsChecked = true;
            Assert.True(Field<bool>(window, "networkChecksEnabled"));
            Field<PasswordBox>(window, "password").Clear();
            Assert.False(Field<bool>(window, "networkChecksEnabled"));
            Assert.Equal("等待", rows["network"].Status.Text); Assert.Equal("等待", rows["identity"].Status.Text);
        }
        finally { window.Close(); }
    });
    [Fact]
    public void Withdrawing_confirmation_clears_old_network_results() => RunSta(() =>
    {
        ConnectionSetupWindow window = new(new DesktopSettings(), _ => throw new InvalidOperationException("No connection expected"), preview: true);
        try
        {
            Field<PasswordBox>(window, "password").Password = "fixture-only";
            Field<CheckBox>(window, "consent").IsChecked = true;
            Invoke(window, "Apply", new SetupUpdate("network", "missing", "fixture"));
            Field<CheckBox>(window, "consent").IsChecked = false;
            Assert.False(Field<bool>(window, "networkChecksEnabled"));
            var rows = Field<Dictionary<string, (TextBlock Status, TextBlock Detail)>>(window, "rows");
            Assert.Equal("等待", rows["network"].Status.Text);
        }
        finally { window.Close(); }
    });
    private static T Field<T>(object target, string name) => (T)target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(target)!;
    [Fact]
    public void Returning_from_network_settings_invalidates_old_checks_and_shows_refresh_immediately() => RunSta(() =>
    {
        ConnectionSetupWindow window = new(new DesktopSettings(), _ => throw new InvalidOperationException("No connection expected"), preview: true);
        try
        {
            Field<PasswordBox>(window, "password").Password = "fixture-only";
            Field<CheckBox>(window, "consent").IsChecked = true;
            Invoke(window, "Apply", new SetupUpdate("identity", "ready", "fixture"));
            Invoke(window, "Apply", new SetupUpdate("internet", "missing", "fixture"));
            int before = Field<ConnectionCheckGate>(window, "autoGate").Version;
            Invoke(window, "RetryNetworkCheck");
            Assert.True(Field<ConnectionCheckGate>(window, "autoGate").Version > before);
            var rows = Field<Dictionary<string, (TextBlock Status, TextBlock Detail)>>(window, "rows");
            Assert.Equal("正在检查…", rows["subnet"].Status.Text);
            Assert.Equal("正在检查…", rows["internet"].Status.Text);
        }
        finally { window.Close(); }
    });
    private static void Invoke(object target, string name, params object[] args) => target.GetType().GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(target, args);
    private static void RunSta(Action action)
    {
        Exception? failure = null;
        Thread thread = new(() => { try { action(); } catch (Exception error) { failure = error; } });
        thread.SetApartmentState(ApartmentState.STA); thread.Start(); thread.Join();
        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
    }
}
