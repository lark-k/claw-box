using System.Diagnostics;
using System.Runtime.ExceptionServices;
using System.Text.Json;
using System.Windows.Documents;
using System.Windows.Threading;
using ClawBox.Desktop.Models;
using ClawBox.Desktop.Services;

namespace ClawBox.Desktop.Tests;

public sealed class ResponseFeedbackTests
{
    [Theory]
    [InlineData("一行消息", 0)]
    [InlineData("第一行\n第二行", 1)]
    public void Text_renderer_does_not_add_a_synthetic_final_line(string text, int breaks) => RunSta(() =>
    {
        MarkdownText rendered = new() { Markdown = text };
        Assert.Equal(breaks, rendered.Inlines.OfType<LineBreak>().Count());
        Assert.IsNotType<LineBreak>(rendered.Inlines.LastInline);
    });
    [Fact]
    public void Progress_is_immediate_ticks_before_any_tool_and_collapses_on_completion() => RunSta(() =>
    {
        Message reply = new();
        using TurnProgress progress = new(reply);
        Assert.True(reply.IsProcessing); Assert.True(reply.IsThinking); Assert.True(reply.ProgressExpanded);
        Assert.Equal("已处理 0秒", reply.Duration);
        DispatcherFrame frame = new(); Stopwatch timeout = Stopwatch.StartNew();
        DispatcherTimer observe = new(TimeSpan.FromMilliseconds(50), DispatcherPriority.Background, (_, _) =>
        {
            if (reply.Duration != "已处理 0秒" || timeout.Elapsed > TimeSpan.FromSeconds(5)) frame.Continue = false;
        }, Dispatcher.CurrentDispatcher);
        observe.Start(); Dispatcher.PushFrame(frame); observe.Stop();
        Assert.NotEqual("已处理 0秒", reply.Duration);
        reply.AwaitingApproval = true; Assert.False(reply.IsThinking); Assert.Equal("等待批准", reply.ProcessingLabel);
        reply.ProgressExpanded = true;
        progress.Dispose();
        Assert.False(reply.IsProcessing); Assert.False(reply.ProgressExpanded);
        Assert.StartsWith("用时 ", reply.Duration);
        reply.ProgressExpanded = true; Assert.True(reply.ProgressExpanded);
    });
    [Fact]
    public void Markers_can_be_split_at_every_character_and_final_text_stays_separate()
    {
        Message reply = new(); TurnTranscript transcript = new(reply);
        string stream = TurnTranscript.ProgressMarker + "我会先读取文件。" + TurnTranscript.ProgressMarker + "已找到原因，正在修改。" + TurnTranscript.FinalMarker + "已修复，并完成检查。";
        foreach (char character in stream)
        {
            transcript.Append(character.ToString());
            Assert.DoesNotContain("[[QH_", reply.Text);
        }
        transcript.Complete();
        Assert.Equal("已修复，并完成检查。", reply.Text);
        Assert.Equal(2, reply.ProgressEntries.Count);
        Assert.Equal("我会先读取文件。", reply.ProgressEntries[0].Text);
        Assert.Equal("已找到原因，正在修改。", reply.ProgressEntries[1].Text);
    }
    [Fact]
    public void Unmarked_gateway_commentary_before_tool_is_folded_but_plain_final_is_preserved()
    {
        Message reply = new(); TurnTranscript transcript = new(reply);
        string commentary = "先读取原文件取得摘要。" + new string('文', 200);
        transcript.Append(commentary); transcript.SealBeforeTool();
        Assert.Empty(reply.Text); Assert.True(Assert.Single(reply.ProgressEntries).Text.Length <= 81);
        Assert.Equal(commentary, reply.ProgressEntries[0].Detail);
        transcript.Append("完成！原文件已修改并重命名。"); transcript.Complete();
        Assert.Equal("完成！原文件已修改并重命名。", reply.Text);
        Message plain = new(); TurnTranscript ordinary = new(plain);
        ordinary.Append("这是一段普通回复。"); ordinary.Complete();
        Assert.Equal("这是一段普通回复。", plain.Text); Assert.Empty(plain.ProgressEntries);
    }
    [Fact]
    public void Streaming_boundary_whitespace_has_no_visible_text_or_repeated_tool_gaps()
    {
        Message reply = new(); TurnTranscript transcript = new(reply);
        for (int i = 0; i < 4; i++)
        {
            transcript.Append("\n\n  ");
            Assert.False(reply.HasText); Assert.Empty(reply.DisplayText);
            transcript.SealBeforeTool();
            Assert.Empty(reply.Text); Assert.Empty(reply.ProgressEntries);
        }
        transcript.Append("\n\n创建 src/App.vue：\n\n");
        Assert.Equal("创建 src/App.vue：", reply.DisplayText);
        transcript.SealBeforeTool();
        Assert.Equal("创建 src/App.vue：", Assert.Single(reply.ProgressEntries).Text);
        transcript.Append(TurnTranscript.FinalMarker + "\n\n已创建。\n\n运行方式：\n npm run dev\n\n");
        Assert.Equal("已创建。\n\n运行方式：\n npm run dev", reply.DisplayText);
        Assert.DoesNotContain("displayText", JsonSerializer.Serialize(reply), StringComparison.OrdinalIgnoreCase);
    }
    [Fact]
    public void Progress_only_and_interrupted_output_are_not_promoted_to_success()
    {
        Message progressOnly = new(); TurnTranscript transcript = new(progressOnly);
        transcript.Append(TurnTranscript.ProgressMarker + "准备开始修改。"); transcript.Complete();
        Assert.Empty(progressOnly.Text); Assert.Single(progressOnly.ProgressEntries);
        Message interrupted = new(); TurnTranscript incomplete = new(interrupted);
        incomplete.Append(TurnTranscript.FinalMarker + "尚未完整收到的结果"); incomplete.Interrupt();
        Assert.Empty(interrupted.Text); Assert.Single(interrupted.ProgressEntries);
    }
    [Fact]
    public void Reopening_history_does_not_restart_progress_or_expand_it()
    {
        Message reply = new() { IsProcessing = true, ProgressExpanded = true, AwaitingApproval = true };
        Message restored = JsonSerializer.Deserialize<Message>(JsonSerializer.Serialize(reply))!;
        Assert.False(restored.IsProcessing); Assert.False(restored.ProgressExpanded); Assert.False(restored.AwaitingApproval);
    }
    [Fact]
    public void Failed_check_progress_cannot_claim_tests_passed()
    {
        using JsonDocument arguments = JsonDocument.Parse("""{"path":"C:\\project","kind":"test"}""");
        using JsonDocument result = JsonDocument.Parse("""{"checksPassed":false}""");
        ClawBox.Contracts.ToolCall call = new(Guid.NewGuid(), "project.check", arguments.RootElement.Clone());
        ClawBox.Contracts.ExecutorTask task = new(Guid.NewGuid(), call.RequestId, call.Tool, "succeeded", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, result.RootElement.Clone(), null);
        Assert.Contains("未通过", ProgressNarrator.Describe(call, task.Status, @"C:\project", task));
    }
    private static void RunSta(Action action)
    {
        Exception? error = null;
        Thread thread = new(() => { try { action(); } catch (Exception caught) { error = caught; } });
        thread.SetApartmentState(ApartmentState.STA); thread.Start(); thread.Join();
        if (error is not null) ExceptionDispatchInfo.Capture(error).Throw();
    }
}
