using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Documents;

namespace ClawBox.Desktop.Tests;

public sealed class MarkdownTextTests
{
    [Theory]
    [InlineData("**")]
    [InlineData("***")]
    [InlineData("****")]
    [InlineData("**unfinished")]
    [InlineData("before **")]
    [InlineData("**a*b**")]
    [InlineData("`")]
    [InlineData("``")]
    public void Unmatched_markers_render_literally(string text) => RunSta(() =>
    {
        MarkdownText control = new() { Markdown = text };
        Assert.Equal(text, new TextRange(control.ContentStart, control.ContentEnd).Text);
    });
    [Fact]
    public void Complete_tokens_keep_their_formatting_and_literal_surroundings() => RunSta(() =>
    {
        MarkdownText control = new() { Markdown = "before **bold** and `code` after **" };
        Assert.Equal("before bold and code after **", new TextRange(control.ContentStart, control.ContentEnd).Text);
        Assert.Contains(control.Inlines.OfType<Run>(), r => r.Text == "bold" && r.FontWeight == FontWeights.SemiBold);
        Assert.Contains(control.Inlines.OfType<Run>(), r => r.Text == "code" && r.FontFamily.Source == "Consolas");
    });
    [Fact]
    public void Every_streaming_prefix_and_null_can_be_rendered() => RunSta(() =>
    {
        const string reply = "# Heading\n**bold** and `code`\n***\n```text\n**literal**\n```\nend **";
        MarkdownText control = new();
        for (int length = 0; length <= reply.Length; length++)
        {
            control.Markdown = reply[..length]; control.Measure(new Size(600, double.PositiveInfinity));
        }
        control.Markdown = null!;
        Assert.Empty(control.Inlines);
    });
    [Fact]
    public void Long_unfinished_markup_is_plain_text() => RunSta(() =>
    {
        string text = "**" + new string('x', 64000);
        MarkdownText control = new() { Markdown = text };
        Assert.Equal(text, new TextRange(control.ContentStart, control.ContentEnd).Text);
    });
    private static void RunSta(Action action)
    {
        Exception? failure = null;
        Thread thread = new(() => { try { action(); } catch (Exception error) { failure = error; } });
        thread.SetApartmentState(ApartmentState.STA); thread.Start(); thread.Join();
        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
    }
}
