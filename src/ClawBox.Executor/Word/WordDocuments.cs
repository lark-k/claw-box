using System.IO.Compression;
using System.Text;
using ClawBox.Executor.Api;
using ClawBox.Executor.Configuration;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;

namespace ClawBox.Executor.Word;

public sealed class WordDocuments(ExecutorOptions options)
{
    public string Read(byte[] bytes)
    {
        ValidatePackage(bytes);
        using MemoryStream stream = new(bytes);
        using WordprocessingDocument document = WordprocessingDocument.Open(stream, false,
            new OpenSettings { MaxCharactersInPart = options.MaxExpandedDocumentBytes });
        if (document.DocumentType != WordprocessingDocumentType.Document) throw InvalidDocument();
        Body body = document.MainDocumentPart?.Document?.Body ?? throw InvalidDocument();
        StringBuilder result = new();
        int size = 0;
        bool first = true;
        foreach (Paragraph paragraph in body.Descendants<Paragraph>())
        {
            string text = paragraph.InnerText;
            size = checked(size + Encoding.UTF8.GetByteCount(text) + (first ? 0 : Environment.NewLine.Length));
            if (size > options.MaxInputBytes) throw new ExecutorException("output_too_large", 413, "Extracted text exceeds the size limit.");
            if (!first) result.Append(Environment.NewLine);
            first = false;
            result.Append(text);
        }
        return result.ToString();
    }

    public static byte[] Create(string text)
    {
        using MemoryStream stream = new();
        using (WordprocessingDocument document = WordprocessingDocument.Create(stream, WordprocessingDocumentType.Document))
        {
            MainDocumentPart main = document.AddMainDocumentPart();
            main.Document = new Document(new Body(text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n')
                .Select(line => new Paragraph(new Run(new Text(line) { Space = SpaceProcessingModeValues.Preserve })))));
            main.Document.Save();
        }
        return stream.ToArray();
    }

    public (byte[] Bytes, int Replacements) Replace(byte[] bytes, string search, string replacement)
    {
        if (string.IsNullOrEmpty(search) || search.Contains('\n') || search.Contains('\r'))
            throw new ExecutorException("invalid_arguments", 422, "Replacement search must be nonempty and within a paragraph.");
        ValidatePackage(bytes);
        using MemoryStream stream = new();
        stream.Write(bytes);
        stream.Position = 0;
        int count = 0;
        using (WordprocessingDocument document = WordprocessingDocument.Open(stream, true,
            new OpenSettings { MaxCharactersInPart = options.MaxExpandedDocumentBytes }))
        {
            if (document.DocumentType != WordprocessingDocumentType.Document) throw InvalidDocument();
            Body body = document.MainDocumentPart?.Document?.Body ?? throw InvalidDocument();
            long totalOutputChars = 0;
            foreach (Paragraph paragraph in body.Descendants<Paragraph>())
            {
                // Do not join text across tabs/breaks/fields. Ordinary matches may span formatting runs.
                List<Text> segment = [];
                foreach (OpenXmlElement element in paragraph.Descendants())
                {
                    if (element is Text text) segment.Add(text);
                    else if (element is Break or TabChar or FieldChar)
                    {
                        count += ReplaceSegment(segment, search, replacement, options.MaxInputBytes);
                        segment.Clear();
                    }
                }
                count += ReplaceSegment(segment, search, replacement, options.MaxInputBytes);
                totalOutputChars += paragraph.InnerText.Length;
                if (totalOutputChars > options.MaxInputBytes) throw new ExecutorException("output_too_large", 413, "Replacement output exceeds the size limit.");
            }
            document.MainDocumentPart!.Document!.Save();
        }
        byte[] output = stream.ToArray();
        ValidatePackage(output);
        return (output, count);
    }

    public (byte[] Bytes, int Paragraphs) Format(byte[] bytes, System.Text.Json.JsonElement arguments, CancellationToken cancellation)
    {
        WordFormatting.Validate(arguments); ValidatePackage(bytes);
        using MemoryStream stream = new(); stream.Write(bytes); stream.Position = 0; int count = 0;
        using (WordprocessingDocument document = WordprocessingDocument.Open(stream, true,
            new OpenSettings { MaxCharactersInPart = options.MaxExpandedDocumentBytes }))
        {
            if (document.DocumentType != WordprocessingDocumentType.Document) throw InvalidDocument();
            MainDocumentPart main = document.MainDocumentPart ?? throw InvalidDocument();
            Body body = main.Document?.Body ?? throw InvalidDocument();
            if (WordFormatting.Get(arguments, "style") is string style) EnsureStyle(main, style);
            foreach (Paragraph paragraph in body.Descendants<Paragraph>())
            {
                cancellation.ThrowIfCancellationRequested();
                if (WordFormatting.Get(arguments, "scope") == "paragraph" && paragraph.InnerText != WordFormatting.Get(arguments, "text")) continue;
                WordFormatting.Apply(paragraph, arguments); count++;
            }
            if (count == 0) throw new ExecutorException("paragraph_not_found", 422, "No matching body/table paragraph was found; the document was not changed.");
            main.Document!.Save();
        }
        byte[] output = stream.ToArray(); ValidatePackage(output); return (output, count);
    }
    private static void EnsureStyle(MainDocumentPart main, string id)
    {
        StyleDefinitionsPart part = main.StyleDefinitionsPart ?? main.AddNewPart<StyleDefinitionsPart>();
        Styles styles = part.Styles ??= new();
        if (styles.Elements<Style>().Any(s => s.StyleId?.Value == id)) return;
        Style style = new() { Type = StyleValues.Paragraph, StyleId = id, CustomStyle = false };
        style.Append(new StyleName { Val = id }, new PrimaryStyle());
        if (id.StartsWith("Heading", StringComparison.Ordinal))
        {
            int level = int.Parse(id[^1..], System.Globalization.CultureInfo.InvariantCulture);
            style.Append(new StyleParagraphProperties(new KeepNext(), new OutlineLevel { Val = level - 1 }),
                new StyleRunProperties(new Bold(), new FontSize { Val = (36 - (level - 1) * 4).ToString(System.Globalization.CultureInfo.InvariantCulture) }));
        }
        else if (id == "Title") style.Append(new StyleRunProperties(new FontSize { Val = "48" }));
        styles.Append(style); styles.Save();
    }

    private static int ReplaceSegment(List<Text> nodes, string search, string replacement, int maxChars)
    {
        string combined = string.Concat(nodes.Select(t => t.Text));
        List<int> matches = [];
        for (int start = 0; start <= combined.Length - search.Length;)
        {
            int found = combined.IndexOf(search, start, StringComparison.Ordinal);
            if (found < 0) break;
            matches.Add(found);
            if (matches.Count > 10000) throw new ExecutorException("output_too_large", 413, "Too many replacement matches.");
            start = found + search.Length;
        }
        if ((long)combined.Length + (long)matches.Count * (replacement.Length - search.Length) > maxChars)
            throw new ExecutorException("output_too_large", 413, "Replacement output exceeds the size limit.");
        int[] starts = new int[nodes.Count];
        for (int i = 1; i < starts.Length; i++) starts[i] = starts[i - 1] + nodes[i - 1].Text.Length;
        foreach (int position in matches.AsEnumerable().Reverse())
        {
            int first = Array.FindLastIndex(starts, p => p <= position);
            int last = Array.FindLastIndex(starts, p => p < position + search.Length);
            int offset = position - starts[first];
            int end = position + search.Length - starts[last];
            if (first == last)
                nodes[first].Text = nodes[first].Text[..offset] + replacement + nodes[first].Text[end..];
            else
            {
                nodes[first].Text = nodes[first].Text[..offset] + replacement;
                for (int i = first + 1; i < last; i++) nodes[i].Text = "";
                nodes[last].Text = nodes[last].Text[end..];
            }
        }
        foreach (Text node in nodes) node.Space = SpaceProcessingModeValues.Preserve;
        return matches.Count;
    }

    private void ValidatePackage(byte[] bytes)
    {
        if (bytes.Length > options.MaxDocumentBytes) throw new ExecutorException("file_too_large", 413, "Document exceeds the size limit.");
        try
        {
            using ZipArchive archive = new(new MemoryStream(bytes), ZipArchiveMode.Read);
            long expanded = 0;
            foreach (ZipArchiveEntry entry in archive.Entries)
            {
                expanded = checked(expanded + entry.Length);
                if (archive.Entries.Count > 2048 || expanded > options.MaxExpandedDocumentBytes)
                    throw new ExecutorException("document_too_large", 413, "Expanded document exceeds the size limit.");
                if (entry.FullName.Contains("vbaProject", StringComparison.OrdinalIgnoreCase)) throw InvalidDocument();
            }
        }
        catch (InvalidDataException) { throw InvalidDocument(); }
    }

    private static ExecutorException InvalidDocument() => new("invalid_document", 422, "A valid macro-free DOCX document is required.");
}
