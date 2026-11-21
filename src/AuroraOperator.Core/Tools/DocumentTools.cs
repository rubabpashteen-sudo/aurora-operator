using System.Globalization;
using System.Text;
using ClosedXML.Excel;
using CsvHelper;
using CsvHelper.Configuration;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using UglyToad.PdfPig;
using A = DocumentFormat.OpenXml.Drawing;
using P = DocumentFormat.OpenXml.Presentation;
using W = DocumentFormat.OpenXml.Wordprocessing;

namespace AuroraOperator.Core.Tools;

/// <summary>
/// Document tools: read PDFs (PdfPig), read CSVs as a table (CsvHelper),
/// create Excel workbooks from CSV (ClosedXML), create PowerPoint decks and
/// Word reports (DocumentFormat.OpenXml). Every Create* method returns the
/// path of the file it created.
/// </summary>
public static class DocumentTools
{
    /// <summary>Extracts the text of every page of a PDF, in page order.</summary>
    public static string ReadPdfText(string path)
    {
        if (!File.Exists(path)) throw new FileNotFoundException($"PDF not found: {path}");
        var sb = new StringBuilder();
        using var document = PdfDocument.Open(path);
        foreach (var page in document.GetPages())
        {
            sb.AppendLine(page.Text);
        }
        return sb.ToString().TrimEnd();
    }

    /// <summary>Reads a CSV and returns it as a pipe-separated table (header + rows, max 1000 rows).</summary>
    public static string ReadCsvAsTable(string path)
    {
        if (!File.Exists(path)) throw new FileNotFoundException($"CSV not found: {path}");
        var rows = ReadCsvRows(path);
        if (rows.Count == 0) return "(The CSV is empty.)";
        var sb = new StringBuilder();
        const int maxRows = 1000;
        var shown = rows.Take(maxRows + 1).ToList();
        for (var i = 0; i < shown.Count; i++)
        {
            sb.AppendLine(string.Join(" | ", shown[i]));
            if (i == 0)
                sb.AppendLine(string.Join(" | ", shown[i].Select(_ => "---")));
        }
        if (rows.Count > maxRows + 1)
            sb.AppendLine($"… ({rows.Count - maxRows - 1} more rows not shown)");
        return sb.ToString().TrimEnd();
    }

    /// <summary>Creates an .xlsx workbook from a CSV (first row = header) and returns the xlsx path.</summary>
    public static string CreateExcelFromCsv(string csvPath, string xlsxPath)
    {
        if (!File.Exists(csvPath)) throw new FileNotFoundException($"CSV not found: {csvPath}");
        var rows = ReadCsvRows(csvPath);
        var dir = Path.GetDirectoryName(Path.GetFullPath(xlsxPath));
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        using var workbook = new XLWorkbook();
        var sheet = workbook.Worksheets.Add("Data");
        for (var r = 0; r < rows.Count; r++)
        {
            for (var c = 0; c < rows[r].Length; c++)
            {
                var cell = sheet.Cell(r + 1, c + 1);
                var value = rows[r][c];
                if (r == 0)
                {
                    cell.Value = value;
                    cell.Style.Font.Bold = true;
                }
                else if (double.TryParse(value, NumberStyles.Any, CultureInfo.InvariantCulture, out var number))
                {
                    cell.Value = number;
                }
                else if (DateTime.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
                {
                    cell.Value = date;
                }
                else
                {
                    cell.Value = value;
                }
            }
        }
        if (rows.Count > 0)
            sheet.Range(1, 1, 1, rows[0].Length).Style.Fill.BackgroundColor = XLColor.LightGray;
        workbook.SaveAs(xlsxPath);
        return xlsxPath;
    }

    /// <summary>Creates a .pptx deck (title slide content + one slide per entry) and returns the pptx path.</summary>
    public static string CreatePowerPoint(string title, IReadOnlyList<(string Title, string Body)> slides, string pptxPath)
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(pptxPath));
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        if (File.Exists(pptxPath)) File.Delete(pptxPath);

        var entries = (slides ?? Array.Empty<(string, string)>()).ToList();
        if (entries.Count == 0) entries.Add(("Overview", title));

        using var presentation = PresentationDocument.Create(pptxPath, PresentationDocumentType.Presentation);
        var presentationPart = presentation.AddPresentationPart();
        var masterPart = presentationPart.AddNewPart<SlideMasterPart>();
        var layoutPart = masterPart.AddNewPart<SlideLayoutPart>();

        layoutPart.SlideLayout = new P.SlideLayout(
            new P.CommonSlideData(new P.ShapeTree(
                NonVisualGroupShapeProperties(),
                new P.GroupShapeProperties(new A.Transform2D(
                    new A.Offset { X = 0, Y = 0 },
                    new A.Extents { Cx = 0, Cy = 0 },
                    new A.ChildOffset { X = 0, Y = 0 },
                    new A.ChildExtents { Cx = 0, Cy = 0 })),
                PlaceholderShape(2, "Title", P.PlaceholderValues.Title, 0, 0, 0, 9144000, 1000000),
                PlaceholderShape(3, "Body", P.PlaceholderValues.Body, 1, 0, 1100000, 9144000, 5000000))),
            new P.ColorMapOverride(new A.MasterColorMapping()));
        layoutPart.SlideLayout.Save();

        masterPart.SlideMaster = new P.SlideMaster(
            new P.CommonSlideData(new P.ShapeTree(
                NonVisualGroupShapeProperties(),
                new P.GroupShapeProperties(new A.Transform2D(
                    new A.Offset { X = 0, Y = 0 },
                    new A.Extents { Cx = 0, Cy = 0 },
                    new A.ChildOffset { X = 0, Y = 0 },
                    new A.ChildExtents { Cx = 0, Cy = 0 })),
                PlaceholderShape(2, "Title", P.PlaceholderValues.Title, 0, 0, 0, 9144000, 1000000),
                PlaceholderShape(3, "Body", P.PlaceholderValues.Body, 1, 0, 1100000, 9144000, 5000000))),
            new P.ColorMap
            {
                Background1 = A.ColorSchemeIndexValues.Light1,
                Text1 = A.ColorSchemeIndexValues.Dark1,
                Background2 = A.ColorSchemeIndexValues.Light2,
                Text2 = A.ColorSchemeIndexValues.Dark2,
                Accent1 = A.ColorSchemeIndexValues.Accent1,
                Accent2 = A.ColorSchemeIndexValues.Accent2,
                Accent3 = A.ColorSchemeIndexValues.Accent3,
                Accent4 = A.ColorSchemeIndexValues.Accent4,
                Accent5 = A.ColorSchemeIndexValues.Accent5,
                Accent6 = A.ColorSchemeIndexValues.Accent6,
                Hyperlink = A.ColorSchemeIndexValues.Hyperlink,
                FollowedHyperlink = A.ColorSchemeIndexValues.FollowedHyperlink,
            },
            new P.SlideLayoutIdList(new P.SlideLayoutId
            {
                RelationshipId = masterPart.GetIdOfPart(layoutPart),
            }));
        masterPart.SlideMaster.Save();

        var slideIds = new P.SlideIdList();
        uint slideId = 256;
        foreach (var (slideTitle, slideBody) in entries)
        {
            var slidePart = presentationPart.AddNewPart<SlidePart>();
            slidePart.Slide = new P.Slide(
                new P.CommonSlideData(new P.ShapeTree(
                    NonVisualGroupShapeProperties(),
                    new P.GroupShapeProperties(new A.Transform2D(
                        new A.Offset { X = 0, Y = 0 },
                        new A.Extents { Cx = 0, Cy = 0 },
                        new A.ChildOffset { X = 0, Y = 0 },
                        new A.ChildExtents { Cx = 0, Cy = 0 })),
                    TextShape(2, "Title", slideTitle, P.PlaceholderValues.Title, 0,
                        0, 300000, 9144000, 900000, fontSize: 3200, bold: true),
                    TextShape(3, "Body", slideBody, P.PlaceholderValues.Body, 1,
                        0, 1400000, 9144000, 5000000, fontSize: 1800, bold: false))),
                new P.ColorMapOverride(new A.MasterColorMapping()));
            slidePart.AddPart(layoutPart);
            slidePart.Slide.Save();
            slideIds.Append(new P.SlideId
            {
                Id = slideId++,
                RelationshipId = presentationPart.GetIdOfPart(slidePart),
            });
        }

        presentationPart.Presentation = new P.Presentation(
            new P.SlideMasterIdList(new P.SlideMasterId
            {
                RelationshipId = presentationPart.GetIdOfPart(masterPart),
            }),
            slideIds,
            new P.SlideSize { Cx = 9144000, Cy = 6858000 });
        presentationPart.Presentation.Save();
        return pptxPath;
    }

    /// <summary>Creates a .docx report (title + body paragraphs) and returns the docx path.</summary>
    public static string CreateWordReport(string title, string body, string docxPath)
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(docxPath));
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        if (File.Exists(docxPath)) File.Delete(docxPath);

        using var document = WordprocessingDocument.Create(docxPath, WordprocessingDocumentType.Document);
        var mainPart = document.AddMainDocumentPart();
        var bodyElement = new W.Body();

        var titleParagraph = new W.Paragraph(
            new W.ParagraphProperties(new W.Justification { Val = W.JustificationValues.Center }),
            new W.Run(new W.RunProperties(
                    new W.Bold(),
                    new W.FontSize { Val = "32" }),
                new W.Text(title ?? string.Empty)));
        bodyElement.Append(titleParagraph);

        foreach (var line in (body ?? string.Empty).Split('\n'))
        {
            var text = line.TrimEnd('\r');
            var paragraph = new W.Paragraph(new W.Run(new W.Text(text.Length == 0 ? string.Empty : text)));
            bodyElement.Append(paragraph);
        }

        mainPart.Document = new W.Document(bodyElement);
        mainPart.Document.Save();
        return docxPath;
    }

    private static List<string[]> ReadCsvRows(string path)
    {
        try
        {
            var config = new CsvConfiguration(CultureInfo.InvariantCulture)
            {
                BadDataFound = null,
                MissingFieldFound = null,
                HeaderValidated = null,
            };
            using var reader = new StreamReader(path);
            using var csv = new CsvReader(reader, config);
            if (!csv.Read()) return new List<string[]>();
            csv.ReadHeader();
            var headers = csv.HeaderRecord ?? Array.Empty<string>();
            var rows = new List<string[]> { headers };
            while (csv.Read())
            {
                var row = new string[headers.Length];
                for (var i = 0; i < headers.Length; i++)
                    row[i] = csv.GetField<string>(i) ?? string.Empty;
                rows.Add(row);
            }
            return rows;
        }
        catch (Exception)
        {
            // Malformed CSV for CsvHelper: fall back to a small quote-aware manual parse.
            return ParseCsvManually(File.ReadAllText(path));
        }
    }

    private static List<string[]> ParseCsvManually(string content)
    {
        var rows = new List<string[]>();
        var row = new List<string>();
        var field = new StringBuilder();
        var inQuotes = false;
        for (var i = 0; i < content.Length; i++)
        {
            var ch = content[i];
            if (inQuotes)
            {
                if (ch == '"')
                {
                    if (i + 1 < content.Length && content[i + 1] == '"') { field.Append('"'); i++; }
                    else inQuotes = false;
                }
                else field.Append(ch);
            }
            else if (ch == '"') inQuotes = true;
            else if (ch == ',') { row.Add(field.ToString()); field.Clear(); }
            else if (ch == '\r') { /* ignore */ }
            else if (ch == '\n') { row.Add(field.ToString()); field.Clear(); rows.Add(row.ToArray()); row = new List<string>(); }
            else field.Append(ch);
        }
        if (field.Length > 0 || row.Count > 0)
        {
            row.Add(field.ToString());
            rows.Add(row.ToArray());
        }
        return rows;
    }


    private static A.TextBody BuildTextBody(IReadOnlyList<A.Paragraph> paragraphs)
    {
        var body = new A.TextBody(new A.BodyProperties(), new A.ListStyle());
        foreach (var p in paragraphs) body.Append(p);
        return body;
    }

    private static P.NonVisualGroupShapeProperties NonVisualGroupShapeProperties() =>
        new(
            new P.NonVisualDrawingProperties { Id = 1, Name = string.Empty },
            new P.NonVisualGroupShapeDrawingProperties(),
            new P.ApplicationNonVisualDrawingProperties());

    private static P.Shape PlaceholderShape(uint id, string name, P.PlaceholderValues type, uint index,
        long x, long y, long cx, long cy) =>
        new(
            new P.NonVisualShapeProperties(
                new P.NonVisualDrawingProperties { Id = id, Name = name },
                new P.NonVisualShapeDrawingProperties(),
                new P.ApplicationNonVisualDrawingProperties(
                    new P.PlaceholderShape { Type = type, Index = index })),
            new P.ShapeProperties(
                new A.Transform2D(new A.Offset { X = x, Y = y }, new A.Extents { Cx = cx, Cy = cy }),
                new A.PresetGeometry(new A.AdjustValueList()) { Preset = A.ShapeTypeValues.Rectangle }),
            new A.TextBody(new A.BodyProperties(), new A.ListStyle(), new A.Paragraph()));

    private static P.Shape TextShape(uint id, string name, string text, P.PlaceholderValues type, uint index,
        long x, long y, long cx, long cy, int fontSize, bool bold)
    {
        var paragraphs = new List<A.Paragraph>();
        var lines = (text ?? string.Empty).Split('\n');
        foreach (var rawLine in lines)
        {
            var line = rawLine.TrimEnd('\r');
            var runProperties = new A.RunProperties { FontSize = fontSize, Bold = bold };
            paragraphs.Add(new A.Paragraph(
                new A.Run(runProperties, new A.Text(line.Length == 0 ? string.Empty : line))));
        }
        if (paragraphs.Count == 0)
            paragraphs.Add(new A.Paragraph(new A.Run(new A.Text(string.Empty))));
        return new P.Shape(
            new P.NonVisualShapeProperties(
                new P.NonVisualDrawingProperties { Id = id, Name = name },
                new P.NonVisualShapeDrawingProperties(),
                new P.ApplicationNonVisualDrawingProperties(
                    new P.PlaceholderShape { Type = type, Index = index })),
            new P.ShapeProperties(
                new A.Transform2D(new A.Offset { X = x, Y = y }, new A.Extents { Cx = cx, Cy = cy }),
                new A.PresetGeometry(new A.AdjustValueList()) { Preset = A.ShapeTypeValues.Rectangle }),
            BuildTextBody(paragraphs));
    }
}
