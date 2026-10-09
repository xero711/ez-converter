using System.IO;
using System.Text;
using ClosedXML.Excel;
using UglyToad.PdfPig;
using UglyToad.PdfPig.DocumentLayoutAnalysis.TextExtractor;

namespace MediaConverter.Services;

public static class PdfTextConversionService
{
    private const int MaxExcelRows = 1_048_576;

    public static Task<ToolRunResult> ConvertAsync(
        string inputPath,
        string outputPath,
        string targetExtension,
        IProgress<int>? progress,
        CancellationToken cancellationToken)
    {
        return Task.Run(() =>
        {
            var temporaryPath = Path.Combine(
                Path.GetDirectoryName(outputPath)!,
                $".{Guid.NewGuid():N}.{targetExtension}");
            try
            {
                if (targetExtension.Equals("xlsx", StringComparison.OrdinalIgnoreCase))
                {
                    WriteSpreadsheet(inputPath, temporaryPath, progress, cancellationToken);
                }
                else if (targetExtension.Equals("txt", StringComparison.OrdinalIgnoreCase))
                {
                    WriteText(inputPath, temporaryPath, progress, cancellationToken);
                }
                else
                {
                    throw new ConversionException("PDFから選択した形式への変換には対応していません。");
                }

                cancellationToken.ThrowIfCancellationRequested();
                File.Move(temporaryPath, outputPath);
                return new ToolRunResult(0, string.Empty, string.Empty);
            }
            finally
            {
                if (File.Exists(temporaryPath))
                {
                    File.Delete(temporaryPath);
                }
            }
        }, cancellationToken);
    }

    private static void WriteSpreadsheet(
        string inputPath,
        string outputPath,
        IProgress<int>? progress,
        CancellationToken cancellationToken)
    {
        using var workbook = new XLWorkbook();
        var sheet = workbook.Worksheets.Add("PDFの文字");
        sheet.Cell(1, 1).Value = "ページ";
        sheet.Cell(1, 2).Value = "本文（文字のみ）";
        sheet.Range(1, 1, 1, 2).Style.Font.Bold = true;
        sheet.Column(1).Width = 10;
        sheet.Column(2).Width = 90;
        sheet.SheetView.FreezeRows(1);

        var row = 2;
        var readableLines = ExtractLines(inputPath, (pageNumber, line) =>
        {
            foreach (var part in SplitForExcel(line))
            {
                if (row > MaxExcelRows)
                {
                    throw new ConversionException("PDFの文字量がExcelの行数上限を超えました。TXT形式を選んでください。");
                }

                sheet.Cell(row, 1).Value = pageNumber;
                sheet.Cell(row, 2).Value = part;
                row++;
            }
        }, progress, cancellationToken);

        RequireReadableText(readableLines);
        cancellationToken.ThrowIfCancellationRequested();
        workbook.SaveAs(outputPath);
    }

    private static void WriteText(
        string inputPath,
        string outputPath,
        IProgress<int>? progress,
        CancellationToken cancellationToken)
    {
        using var writer = new StreamWriter(outputPath, false, new UTF8Encoding(false));
        var previousPage = 0;
        var readableLines = ExtractLines(inputPath, (pageNumber, line) =>
        {
            if (pageNumber != previousPage)
            {
                if (previousPage != 0)
                {
                    writer.WriteLine();
                }

                writer.WriteLine($"--- ページ {pageNumber} ---");
                previousPage = pageNumber;
            }

            writer.WriteLine(line);
        }, progress, cancellationToken);

        RequireReadableText(readableLines);
        cancellationToken.ThrowIfCancellationRequested();
    }

    private static int ExtractLines(
        string inputPath,
        Action<int, string> onLine,
        IProgress<int>? progress,
        CancellationToken cancellationToken)
    {
        using var document = PdfDocument.Open(inputPath);
        var readableLines = 0;
        for (var pageNumber = 1; pageNumber <= document.NumberOfPages; pageNumber++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var page = document.GetPage(pageNumber);
            var lines = ContentOrderTextExtractor.GetText(page)
                .Split(["\r\n", "\n", "\r"], StringSplitOptions.RemoveEmptyEntries)
                .Select(line => line.Trim())
                .Where(line => line.Length > 0)
                .ToArray();

            if (lines.Length == 0)
            {
                onLine(pageNumber, "このページから読み取れる文字はありません。");
            }
            else
            {
                foreach (var line in lines)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    onLine(pageNumber, line);
                    readableLines++;
                }
            }

            progress?.Report(5 + 90 * pageNumber / document.NumberOfPages);
        }

        return readableLines;
    }

    private static IEnumerable<string> SplitForExcel(string value)
    {
        const int maxCellLength = 32_767;
        for (var offset = 0; offset < value.Length; offset += maxCellLength)
        {
            yield return value.Substring(offset, Math.Min(maxCellLength, value.Length - offset));
        }
    }

    private static void RequireReadableText(int readableLines)
    {
        if (readableLines == 0)
        {
            throw new ConversionException("このPDFには抽出できる文字がありません。画像だけのPDFはExcelに変換できません。");
        }
    }
}
