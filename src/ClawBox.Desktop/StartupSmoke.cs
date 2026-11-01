using System.Text.Json;
using System.Windows;
using System.Windows.Threading;
using ClawBox.Desktop.Models;
using ClawBox.Desktop.Services;
using ClawBox.Desktop.ViewModels;

namespace ClawBox.Desktop;

// Runs the real first-launch flow with isolated state, without pairing or modal guides.
public static class StartupSmoke
{
    public static void Run(string destination)
    {
        Directory.CreateDirectory(destination);
        string root = Path.Combine(destination, "fresh-" + Guid.NewGuid().ToString("N"));
        LocalState state = new(root);
        state.Save("settings.json", new DesktopSettings
        {
            Workspace = Path.Combine(root, "workspace"),
            ProfilePath = Path.Combine(root, "missing-profile.json"),
            GatewayTokenFile = Path.Combine(root, "missing-gateway.json")
        });
        using ShellViewModel model = new(state);
        MainWindow window = new(model);
        Application.Current.MainWindow = window;
        bool homeReady = false;
        bool unexpectedDialog = false;
        DispatcherFrame frame = new();
        DateTimeOffset started = DateTimeOffset.UtcNow;
        DateTimeOffset deadline = DateTimeOffset.UtcNow.AddSeconds(10);
        DispatcherTimer timer = new(TimeSpan.FromMilliseconds(100), DispatcherPriority.Background, (_, _) =>
        {
            Window? dialog = Application.Current.Windows.OfType<Window>().FirstOrDefault(w => w != window && w.IsVisible);
            if (dialog is not null)
            {
                unexpectedDialog = true;
                dialog.Close();
                frame.Continue = false;
            }
            else if (window.IsLoaded && window.IsVisible && DateTimeOffset.UtcNow - started >= TimeSpan.FromMilliseconds(600))
            {
                homeReady = !model.Connected && !model.IsConnecting;
                frame.Continue = false;
            }
            else if (DateTimeOffset.UtcNow >= deadline) frame.Continue = false;
        }, Dispatcher.CurrentDispatcher);
        try
        {
            timer.Start(); window.Show(); Dispatcher.PushFrame(frame);
            if (!homeReady || unexpectedDialog) throw new InvalidOperationException("Fresh startup must show only the home screen, without a connection dialog.");
            File.WriteAllText(Path.Combine(destination, "startup.json"), JsonSerializer.Serialize(new { mainWindow = true, connectionChooser = false, connected = model.Connected }));
        }
        finally { timer.Stop(); window.Close(); }
    }
}
