using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;

namespace ClawBox.Desktop;

// Render model text only; markdown never executes HTML, commands or navigation.
public sealed class MarkdownText : TextBlock
{
    public static readonly DependencyProperty MarkdownProperty = DependencyProperty.Register(nameof(Markdown), typeof(string), typeof(MarkdownText),
        new PropertyMetadata("", (owner, _) => ((MarkdownText)owner).Render()));
    public string Markdown { get => (string)GetValue(MarkdownProperty); set => SetValue(MarkdownProperty, value); }
    private void Render()
    {
        Inlines.Clear();
        bool code = false;
        foreach (string line in Markdown.Replace("\r\n", "\n").Split('\n'))
        {
            if (line.StartsWith("```", StringComparison.Ordinal)) { code = !code; Inlines.Add(new LineBreak()); continue; }
            if (code) { Inlines.Add(new Run(line) { FontFamily = new("Consolas"), Background = new SolidColorBrush(Color.FromRgb(245, 245, 246)) }); }
            else
            {
                string text = Regex.Replace(line, @"^#{1,6}\s+", "");
                text = Regex.Replace(text, @"^\s*[-*]\s+", "• ");
                bool heading = line.StartsWith('#');
                foreach (string piece in Regex.Split(text, @"(\*\*[^*]+\*\*|`[^`]+`)", RegexOptions.None, TimeSpan.FromMilliseconds(200)))
                {
                    Run run = new(piece);
                    if (piece.StartsWith("**", StringComparison.Ordinal) && piece.EndsWith("**", StringComparison.Ordinal))
                    { run.Text = piece[2..^2]; run.FontWeight = FontWeights.SemiBold; }
                    else if (piece.StartsWith('`') && piece.EndsWith('`') && piece.Length > 1)
                    { run.Text = piece[1..^1]; run.FontFamily = new("Consolas"); run.Background = new SolidColorBrush(Color.FromRgb(242, 242, 243)); }
                    if (heading) run.FontWeight = FontWeights.SemiBold;
                    Inlines.Add(run);
                }
            }
            Inlines.Add(new LineBreak());
        }
        // Line breaks separate content lines; there is no synthetic final blank line.
        if (Inlines.LastInline is LineBreak trailing) Inlines.Remove(trailing);
    }
}
