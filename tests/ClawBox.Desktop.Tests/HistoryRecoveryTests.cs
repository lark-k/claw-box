using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Text.Json;
using ClawBox.Desktop.Models;
using ClawBox.Desktop.Services;
using ClawBox.Desktop.ViewModels;

namespace ClawBox.Desktop.Tests;

public sealed class HistoryRecoveryTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "QuantumHarness.History.Tests", Guid.NewGuid().ToString("N"));
    [Theory]
    [InlineData("zeros")]
    [InlineData("truncated")]
    [InlineData("null")]
    [InlineData("missing")]
    public void Unreadable_record_becomes_read_only_without_losing_other_chats_or_rewriting_original(string damage)
    {
        LocalState state = new(root); ConversationStore store = new(state);
        Conversation broken = new(); Conversation healthy = new() { Title = "healthy", Messages = [new() { Text = "keep me" }] };
        store.Migrate([broken, healthy]);
        string path = store.FilePath(broken.Id, broken.CreatedAt);
        if (damage == "missing") File.Delete(path);
        else File.WriteAllText(path, damage switch { "zeros" => new string('\0', 4096), "null" => "null", _ => "{\"id\":" });
        byte[]? original = File.Exists(path) ? File.ReadAllBytes(path) : null;
        List<Conversation> loaded = store.Load();
        Conversation unavailable = loaded.Single(c => c.Id == broken.Id);
        Assert.True(unavailable.HistoryUnavailable); Assert.Equal(broken.CreatedAt, unavailable.CreatedAt);
        Assert.Contains("内容尚未恢复", Assert.Single(unavailable.Messages).Text);
        Assert.Equal("keep me", Assert.Single(loaded.Single(c => c.Id == healthy.Id).Messages).Text);
        store.Save(unavailable); store.SaveIndex(loaded);
        Assert.Equal(original, File.Exists(path) ? File.ReadAllBytes(path) : null);
        Assert.True(store.Load().Single(c => c.Id == broken.Id).HistoryUnavailable);
    }
    [Fact]
    public void Valid_backup_recovers_the_same_chat_and_saving_preserves_corrupt_bytes()
    {
        LocalState state = new(root); ConversationStore store = new(state);
        Conversation chat = new() { Messages = [new() { Text = "last durable version" }] };
        store.Migrate([chat]);
        chat.Messages.Add(new() { Text = "newer version" }); store.Save(chat);
        string path = store.FilePath(chat.Id, chat.CreatedAt);
        byte[] damaged = new byte[3000]; File.WriteAllBytes(path, damaged);
        Conversation recovered = Assert.Single(store.Load());
        Assert.False(recovered.HistoryUnavailable); Assert.Equal(chat.Id, recovered.Id);
        Assert.Contains(recovered.Messages, m => m.Text == "last durable version");
        Assert.DoesNotContain(recovered.Messages, m => m.Text == "newer version");
        Assert.Contains(recovered.Messages, m => m.Role == "system" && m.Text.Contains("备份"));
        Assert.Equal(damaged, File.ReadAllBytes(path));
        store.Save(recovered);
        string preserved = Assert.Single(Directory.GetFiles(Path.GetDirectoryName(path)!, "*.corrupt-*"));
        Assert.Equal(damaged, File.ReadAllBytes(preserved));
        Assert.False(Assert.Single(store.Load()).HistoryUnavailable);
        using JsonDocument backup = JsonDocument.Parse(File.ReadAllText(path + ".bak"));
    }
    [Fact]
    public void Invalid_backup_does_not_prevent_startup_or_get_overwritten_by_placeholder()
    {
        LocalState state = new(root); ConversationStore store = new(state); Conversation chat = new(); store.Migrate([chat]);
        string path = store.FilePath(chat.Id, chat.CreatedAt);
        File.WriteAllText(path, "broken"); File.WriteAllText(path + ".bak", "also broken");
        Conversation unavailable = Assert.Single(store.Load()); store.Save(unavailable);
        Assert.True(unavailable.HistoryUnavailable); Assert.Equal("broken", File.ReadAllText(path));
        Assert.Equal("also broken", File.ReadAllText(path + ".bak"));
    }
    [Fact]
    public void Backup_cannot_redirect_conversation_identity()
    {
        LocalState state = new(root); ConversationStore store = new(state); Conversation chat = new(); store.Migrate([chat]);
        string path = store.FilePath(chat.Id, chat.CreatedAt);
        File.WriteAllText(path, "broken"); File.WriteAllText(path + ".bak", JsonSerializer.Serialize(new Conversation(), new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        Assert.Throws<InvalidOperationException>(() => store.Load());
    }
    [Fact]
    public void Durable_save_rotates_valid_backup_and_removes_failed_staging_files()
    {
        LocalState state = new(root);
        state.Save("fixture.json", new { value = 1 }); state.Save("fixture.json", new { value = 2 }); state.Save("fixture.json", new { value = 3 });
        using JsonDocument backup = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "fixture.json.bak")));
        Assert.Equal(2, backup.RootElement.GetProperty("value").GetInt32());
        Dictionary<string, object> cycle = []; cycle["cycle"] = cycle;
        Assert.Throws<JsonException>(() => state.Save("fixture.json", cycle));
        Assert.Empty(Directory.GetFiles(root, "*.tmp"));
        using JsonDocument current = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "fixture.json")));
        Assert.Equal(3, current.RootElement.GetProperty("value").GetInt32());
        Assert.Throws<IOException>(() => state.Save("../outside.json", new { value = 1 }));
    }
    [Fact]
    public void Corrupt_actual_history_shape_starts_view_model_and_new_chat_can_send()
    {
        RunSta(() =>
        {
            LocalState state = new(root); ConversationStore store = new(state); Conversation chat = new(); store.Migrate([chat]);
            string path = store.FilePath(chat.Id, chat.CreatedAt); byte[] damaged = new byte[712792]; File.WriteAllBytes(path, damaged);
            using ShellViewModel model = new(state);
            Assert.True(model.Selected.HistoryUnavailable); Assert.Equal(chat.CreatedAt, model.Selected.CreatedAt);
            typeof(ShellViewModel).GetProperty(nameof(ShellViewModel.Connected))!.SetValue(model, true);
            Assert.False(model.CanSend);
            model.RenameConversation(model.Selected, "must not replace the record");
            Assert.Equal("记录暂不可用", model.Title);
            typeof(ShellViewModel).GetProperty(nameof(ShellViewModel.Connected))!.SetValue(model, false);
            model.NewRecent();
            typeof(ShellViewModel).GetProperty(nameof(ShellViewModel.Connected))!.SetValue(model, true);
            Assert.False(model.Selected.HistoryUnavailable); Assert.True(model.CanSend);
            Assert.Equal(damaged, File.ReadAllBytes(path));
            using ShellViewModel restarted = new(state);
            Assert.Equal(2, restarted.Conversations.Count);
            Assert.True(restarted.Conversations.Single(c => c.Id == chat.Id).HistoryUnavailable);
        });
    }
    private static void RunSta(Action action)
    {
        Exception? failure = null;
        Thread thread = new(() => { try { action(); } catch (Exception error) { failure = error; } });
        thread.SetApartmentState(ApartmentState.STA); thread.Start(); thread.Join();
        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
    }
    public void Dispose() { if (Directory.Exists(root)) Directory.Delete(root, true); }
}
