using System.Globalization;
using System.Text.Json;
using ClawBox.Executor.Api;
using DocumentFormat.OpenXml.Wordprocessing;

namespace ClawBox.Executor.Word;

public static class WordFormatting
{
    private static readonly string[] Fields = ["bold", "fontName", "fontSizePt", "alignment", "style"];
    public static void Validate(JsonElement arguments)
    {
        string scope = Get(arguments, "scope")!;
        if (scope is not ("document" or "paragraph") || !Fields.Any(f => arguments.TryGetProperty(f, out _))) throw Invalid();
        if (scope == "paragraph" && string.IsNullOrEmpty(Get(arguments, "text"))) throw Invalid();
        if (scope == "document" && arguments.TryGetProperty("text", out _)) throw Invalid();
        if (Get(arguments, "bold") is string bold && bold is not ("true" or "false")) throw Invalid();
        if (Get(arguments, "fontName") is string font && (string.IsNullOrWhiteSpace(font) || font.Length > 80 || font.Any(char.IsControl))) throw Invalid();
        if (Get(arguments, "fontSizePt") is string size && (!decimal.TryParse(size, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out decimal pt) || pt < 6 || pt > 72 || pt * 2 != decimal.Truncate(pt * 2))) throw Invalid();
        if (Get(arguments, "alignment") is string alignment && alignment is not ("left" or "center" or "right" or "justify")) throw Invalid();
        if (Get(arguments, "style") is string style && style is not ("Normal" or "Title" or "Heading1" or "Heading2" or "Heading3")) throw Invalid();
    }
    public static string? Get(JsonElement arguments, string name) => arguments.TryGetProperty(name, out JsonElement value) ? value.GetString() : null;
    public static void Apply(Paragraph paragraph, JsonElement arguments)
    {
        ParagraphProperties properties = paragraph.ParagraphProperties ??= new();
        if (Get(arguments, "alignment") is string alignment)
            properties.Justification = new Justification { Val = alignment switch { "center" => JustificationValues.Center, "right" => JustificationValues.Right, "justify" => JustificationValues.Both, _ => JustificationValues.Left } };
        if (Get(arguments, "style") is string style) properties.ParagraphStyleId = new() { Val = style };
        foreach (Run run in paragraph.Descendants<Run>())
        {
            RunProperties formatting = run.RunProperties ??= new();
            if (Get(arguments, "bold") is string bold)
            {
                formatting.Bold = new() { Val = bold == "true" };
                formatting.BoldComplexScript = new() { Val = bold == "true" };
            }
            if (Get(arguments, "fontName") is string font) formatting.RunFonts = new() { Ascii = font, HighAnsi = font, EastAsia = font, ComplexScript = font };
            if (Get(arguments, "fontSizePt") is string size)
            {
                string halfPoints = (decimal.Parse(size, CultureInfo.InvariantCulture) * 2).ToString("0", CultureInfo.InvariantCulture);
                formatting.FontSize = new() { Val = halfPoints };
                formatting.FontSizeComplexScript = new() { Val = halfPoints };
            }
        }
    }
    private static ExecutorException Invalid() => new("invalid_arguments", 422, "Specify a supported scope and formatting option; paragraph scope requires exact paragraph text.");
}
