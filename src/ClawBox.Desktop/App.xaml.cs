using System.Threading;
using System.Windows;
using ClawBox.Desktop.Services;
using ClawBox.Desktop.ViewModels;

namespace ClawBox.Desktop;

public partial class App : Application
{
    private Mutex? singleInstance;
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        if (e.Args.Length == 2 && e.Args[0] == "--connection-check")
        {
            Shutdown(ConnectionCheck.Run(e.Args[1]));
            return;
        }
        if (e.Args.Length >= 2 && e.Args[0] == "--visual-smoke")
        {
            VisualSmoke.Run(e.Args[1]);
            Shutdown();
            return;
        }
        singleInstance = new Mutex(true, @"Local\QuantumHarness-" + Environment.UserName, out bool created);
        if (!created) { MessageBox.Show("Quantum Harness 已在运行。", "Quantum Harness"); Shutdown(); return; }
        try
        {
            ShellViewModel model = new(new LocalState());
            MainWindow window = new(model);
            MainWindow = window;
            window.Show();
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidOperationException)
        { MessageBox.Show(error.Message, "Quantum Harness"); Shutdown(); }
    }
    protected override void OnExit(ExitEventArgs e) { singleInstance?.Dispose(); base.OnExit(e); }
}
