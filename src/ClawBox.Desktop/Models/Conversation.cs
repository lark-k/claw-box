using System.ComponentModel;
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
    public string Text { get => text; set => Set(ref text, value); }
    public DateTimeOffset At { get; set; } = DateTimeOffset.Now;
    [JsonIgnore] public string Label => Role == "user" ? "你" : Role == "system" ? "任务状态" : "Quantum";
}

public sealed class Conversation : Observable
{
    private string title = "新任务";
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Title { get => title; set => Set(ref title, value); }
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.Now;
    public List<Message> Messages { get; set; } = [];
}

public sealed class DesktopSettings
{
    public string Workspace { get; set; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ClawBox", "workspace");
    public string ProfilePath { get; set; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ClawBox", "connections", "192.168.137.2-22", "connection.json");
    public string GatewayTokenFile { get; set; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ClawBox", "desktop", "gateway.secrets.json");
}
