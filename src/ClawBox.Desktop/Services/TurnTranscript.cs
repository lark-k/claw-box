using System.Text;
using ClawBox.Desktop.Models;

namespace ClawBox.Desktop.Services;

// The gateway can concatenate several assistant messages into a single SSE content stream.
// Marked commentary and text preceding a real tool request belong to the collapsible process.
public sealed class TurnTranscript(Message reply)
{
    public const string ProgressMarker = "[[QH_PROGRESS]]";
    public const string FinalMarker = "[[QH_FINAL]]";
    private readonly StringBuilder pending = new();
    private ProgressEntry? current;
    private bool final;
    private int characters;
    public void Append(string text)
    {
        characters += text.Length;
        if (characters > 2 * 1024 * 1024) throw new InvalidOperationException("回复超过界面限制。");
        pending.Append(text);
        Drain(false);
        reply.Changed(nameof(Message.ProgressEntries));
    }
    private void Drain(bool flush)
    {
        while (pending.Length > 0)
        {
            string text = pending.ToString();
            int progress = text.IndexOf(ProgressMarker, StringComparison.Ordinal), end = text.IndexOf(FinalMarker, StringComparison.Ordinal);
            int index = progress < 0 ? end : end < 0 ? progress : Math.Min(progress, end);
            if (index >= 0)
            {
                Write(text[..index]);
                bool isFinal = index == end;
                if (isFinal) { FoldUnmarked(); current = null; final = true; }
                else { final = false; current = new() { Kind = "agent" }; reply.ProgressEntries.Add(current); }
                pending.Remove(0, index + (isFinal ? FinalMarker.Length : ProgressMarker.Length));
                continue;
            }
            int keep = 0;
            if (!flush)
                for (int i = 1; i < Math.Min(text.Length + 1, Math.Max(ProgressMarker.Length, FinalMarker.Length)); i++)
                    if (ProgressMarker.StartsWith(text[^i..], StringComparison.Ordinal) || FinalMarker.StartsWith(text[^i..], StringComparison.Ordinal)) keep = i;
            int emit = text.Length - keep;
            if (emit == 0) break;
            Write(text[..emit]); pending.Remove(0, emit);
        }
    }
    private void Write(string text)
    {
        if (current is not null && !final)
        {
            current.Detail += text;
            current.Text = ProgressEntry.Concise(current.Detail);
        }
        else reply.Text += text;
    }
    public void SealBeforeTool()
    {
        FoldUnmarked();
        current = null;
    }
    private void FoldUnmarked()
    {
        if (final) return;
        if (!string.IsNullOrWhiteSpace(reply.Text))
            reply.ProgressEntries.Add(new() { Kind = "agent", Text = ProgressEntry.Concise(reply.Text), Detail = reply.Text });
        reply.Text = "";
    }
    public void Complete()
    {
        Drain(true);
        reply.Text = reply.Text.Trim();
    }
    public void Interrupt()
    {
        Drain(true);
        final = false;
        SealBeforeTool();
    }
}
