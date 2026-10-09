using ClosedXML.Excel;
using System.IO.Compression;
using System.IO;
using MediaConverter.Models;
using MediaConverter.Services;

var repositoryRoot = FindRepositoryRoot();
var workRoot = Path.Combine(Path.GetTempPath(), "EZConverter-Conversion-Matrix-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(workRoot);
try
{
    var simulatedRoutes = 0;
    var targets = MediaFormatCatalog.OutputFormats.Append(MediaFormatCatalog.Av1Mp4OutputFormat).ToArray();
    foreach (var source in MediaFormatCatalog.AllFormats.Where(format => format.CanRead))
    {
        var visibleOutputs = MediaFormatCatalog.GetOutputFormats(source);
        foreach (var target in targets)
        {
            var route = ConversionRoutes.Resolve(source, target);
            var visible = visibleOutputs.Any(candidate =>
                candidate.Extension.Equals(target.Extension, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(candidate.VideoCodec, target.VideoCodec, StringComparison.OrdinalIgnoreCase));
            Require(visible == (route is not null), $"UI route list matches resolver for {source.Extension} -> {target.Extension}");
            if (route is not null)
            {
                Require(target.CanWrite, $"Resolved output {target.Extension} is writable");
                simulatedRoutes++;
            }
        }
    }
    Console.WriteLine($"PASS: {simulatedRoutes:N0} supported source/output routes agree with the format picker.");

    var imageMagick = FindTool(repositoryRoot, [@"Tools\ImageMagick\magick.exe"]) ?? ToolLocator.FindImageMagick()
        ?? throw new InvalidOperationException("ImageMagick is required for the bundled-format conversion matrix.");
    var sevenZip = FindTool(repositoryRoot, [@"Tools\7-Zip\7z.exe"]) ?? ToolLocator.FindSevenZip()
        ?? throw new InvalidOperationException("7-Zip is required for the bundled-format conversion matrix.");
    var libreOffice = FindTool(repositoryRoot, [@"Tools\LibreOffice\program\soffice.com", @"Tools\LibreOffice\program\soffice.exe"])
        ?? ToolLocator.FindLibreOffice()
        ?? throw new InvalidOperationException("LibreOffice is required for the bundled-format conversion matrix.");
    var calibre = FindTool(repositoryRoot, [@"Tools\Calibre\Calibre Portable\Calibre\ebook-convert.exe"])
        ?? ToolLocator.FindCalibre()
        ?? throw new InvalidOperationException("Calibre is required for the bundled-format conversion matrix.");
    var fontForge = FindTool(repositoryRoot, [@"Tools\FontForge\bin\fontforge.exe"]) ?? ToolLocator.FindFontForge()
        ?? throw new InvalidOperationException("FontForge is required for the bundled-format conversion matrix.");

    var service = new ConversionService();

    var imagePath = Path.Combine(workRoot, "image source 日本語.png");
    await RequireSuccessAsync(imageMagick, ["-size", "40x30", "xc:#f05a70", imagePath], workRoot, "ImageMagick fixture");
    var imageTools = new Dictionary<ConversionBackend, string?> { [ConversionBackend.ImageMagick] = imageMagick };
    foreach (var extension in new[] { "jpg", "webp", "tiff" })
    {
        var converted = await ConvertAsync(service, imagePath, extension, Path.Combine(workRoot, "images"), imageTools);
        Require(new FileInfo(converted).Length > 0, $"PNG -> {extension.ToUpperInvariant()} produced data");
    }

    var htmlPath = Path.Combine(workRoot, "文書 source.html");
    await File.WriteAllTextAsync(htmlPath,
        "<!doctype html><html><head><meta charset=\"utf-8\"><title>EZ Converter test</title></head><body><h1>変換試験</h1><p>Japanese document content 123</p></body></html>",
        new System.Text.UTF8Encoding(false));
    var officeTools = new Dictionary<ConversionBackend, string?> { [ConversionBackend.LibreOffice] = libreOffice };
    var docxPath = await ConvertAsync(service, htmlPath, "docx", Path.Combine(workRoot, "documents"), officeTools);
    using (var docx = ZipFile.OpenRead(docxPath))
        Require(docx.GetEntry("word/document.xml") is not null, "LibreOffice DOCX output is a valid OpenXML package");
    var pdfPath = await ConvertAsync(service, docxPath, "pdf", Path.Combine(workRoot, "documents"), officeTools);
    var extractedText = await ConvertAsync(service, pdfPath, "txt", Path.Combine(workRoot, "documents"), new Dictionary<ConversionBackend, string?>());
    Require((await File.ReadAllTextAsync(extractedText)).Contains("変換試験", StringComparison.Ordinal), "HTML -> DOCX -> PDF -> TXT preserves Japanese text");

    var spreadsheetPath = Path.Combine(workRoot, "table.xlsx");
    using (var workbook = new XLWorkbook())
    {
        var sheet = workbook.Worksheets.Add("データ");
        sheet.Cell(1, 1).Value = "名前";
        sheet.Cell(1, 2).Value = "値";
        sheet.Cell(2, 1).Value = "試験";
        sheet.Cell(2, 2).Value = 42;
        workbook.SaveAs(spreadsheetPath);
    }
    var csvPath = await ConvertAsync(service, spreadsheetPath, "csv", Path.Combine(workRoot, "sheets"), officeTools);
    Require((await File.ReadAllTextAsync(csvPath)).Contains("試験", StringComparison.Ordinal), "XLSX -> CSV preserves Japanese cell values");

    var archiveSourceDirectory = Path.Combine(workRoot, "archive source");
    Directory.CreateDirectory(archiveSourceDirectory);
    await File.WriteAllTextAsync(Path.Combine(archiveSourceDirectory, "日本語 & sample.txt"), "archive fixture");
    var archivePath = Path.Combine(workRoot, "source.zip");
    await RequireSuccessAsync(sevenZip, ["a", "-tzip", archivePath, ".", "-y"], archiveSourceDirectory, "ZIP fixture");
    var archiveTools = new Dictionary<ConversionBackend, string?> { [ConversionBackend.SevenZip] = sevenZip };
    foreach (var extension in new[] { "7z", "tar.gz", "jar" })
    {
        var converted = await ConvertAsync(service, archivePath, extension, Path.Combine(workRoot, "archives"), archiveTools);
        Require(new FileInfo(converted).Length > 0, $"ZIP -> {extension.ToUpperInvariant()} produced a readable archive");
    }

    var ebookSourcePath = Path.Combine(workRoot, "book source.html");
    await File.WriteAllTextAsync(ebookSourcePath,
        "<!doctype html><html><head><meta charset=\"utf-8\"><title>EZ Book</title></head><body><h1>変換試験</h1><p>Electronic book fixture.</p></body></html>",
        new System.Text.UTF8Encoding(false));
    var epubPath = Path.Combine(workRoot, "source.epub");
    await RequireSuccessAsync(calibre, [ebookSourcePath, epubPath], workRoot, "EPUB fixture");
    var ebookTools = new Dictionary<ConversionBackend, string?> { [ConversionBackend.Calibre] = calibre };
    var azw3Path = await ConvertAsync(service, epubPath, "azw3", Path.Combine(workRoot, "ebooks"), ebookTools);
    var roundTripEpub = await ConvertAsync(service, azw3Path, "epub", Path.Combine(workRoot, "ebooks-roundtrip"), ebookTools);
    using (var roundTrip = ZipFile.OpenRead(roundTripEpub))
    {
        var contentEntries = roundTrip.Entries.Where(entry =>
            entry.FullName.EndsWith(".html", StringComparison.OrdinalIgnoreCase) ||
            entry.FullName.EndsWith(".xhtml", StringComparison.OrdinalIgnoreCase));
        var content = await Task.WhenAll(contentEntries.Select(async entry =>
        {
            await using var stream = entry.Open();
            using var reader = new StreamReader(stream);
            return await reader.ReadToEndAsync();
        }));
        Require(content.Any(text => text.Contains("Electronic book fixture", StringComparison.Ordinal)), "EPUB -> AZW3 -> EPUB remains readable");
    }

    var windowsFont = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Fonts", "arial.ttf");
    if (!File.Exists(windowsFont)) throw new FileNotFoundException("The Windows Arial test font is unavailable.", windowsFont);
    var fontTools = new Dictionary<ConversionBackend, string?> { [ConversionBackend.FontForge] = fontForge };
    var otfPath = await ConvertAsync(service, windowsFont, "otf", Path.Combine(workRoot, "fonts"), fontTools);
    var fontRoundTrip = Path.Combine(workRoot, "font-roundtrip.ttf");
    await RequireSuccessAsync(fontForge, ["-lang=ff", "-c", $"Open(\"{otfPath.Replace("\\", "/", StringComparison.Ordinal)}\"); Generate(\"{fontRoundTrip.Replace("\\", "/", StringComparison.Ordinal)}\"); Close();"], workRoot, "OTF output reopen");
    Require(new FileInfo(fontRoundTrip).Length > 0, "TTF -> OTF -> TTF remains readable");

    var invalidArchive = Path.Combine(workRoot, "invalid.zip");
    await File.WriteAllTextAsync(invalidArchive, "not an archive");
    var failureDirectory = Path.Combine(workRoot, "failed conversion");
    var failedCleanly = false;
    try { _ = await ConvertAsync(service, invalidArchive, "7z", failureDirectory, archiveTools); }
    catch (ConversionException) { failedCleanly = true; }
    Require(failedCleanly, "Invalid ZIP fails with a conversion error");
    Require(!Directory.EnumerateFileSystemEntries(failureDirectory).Any(), "Failed conversion leaves no partial output or staging directory");

    Console.WriteLine("PASS: ImageMagick, LibreOffice, PDF text extraction, 7-Zip, Calibre, and FontForge sample conversions.");
}
finally
{
    try { Directory.Delete(workRoot, recursive: true); }
    catch (IOException) { }
    catch (UnauthorizedAccessException) { }
}

static async Task<string> ConvertAsync(
    ConversionService service,
    string sourcePath,
    string targetExtension,
    string outputDirectory,
    IReadOnlyDictionary<ConversionBackend, string?> tools)
{
    var item = new MediaItem(sourcePath);
    var target = targetExtension.Equals("mp4", StringComparison.OrdinalIgnoreCase)
        ? MediaFormatCatalog.Av1Mp4OutputFormat
        : MediaFormatCatalog.FindByExtension(targetExtension)
          ?? throw new InvalidOperationException($"Output format is missing: {targetExtension}");
    Require(item.SourceFormat is not null, $"Input is catalogued: {Path.GetFileName(sourcePath)}");
    Require(ConversionRoutes.Resolve(item.SourceFormat!, target) is not null,
        $"Route exists: {item.SourceFormat!.Extension} -> {target.Extension}");
    return await service.ConvertAsync(item, target, outputDirectory, tools, progress: null, CancellationToken.None);
}

static async Task RequireSuccessAsync(string executable, IEnumerable<string> arguments, string workingDirectory, string description)
{
    var result = await ExternalToolRunner.RunAsync(executable, arguments, CancellationToken.None, workingDirectory);
    Require(result.ExitCode == 0, $"{description} failed: {result.StandardError}");
}

static string? FindTool(string repositoryRoot, IEnumerable<string> relativePaths)
{
    foreach (var relative in relativePaths)
    {
        var path = Path.Combine(repositoryRoot, relative);
        if (File.Exists(path)) return path;
    }
    return null;
}

static string FindRepositoryRoot()
{
    for (var directory = new DirectoryInfo(Environment.CurrentDirectory); directory is not null; directory = directory.Parent)
    {
        if (File.Exists(Path.Combine(directory.FullName, "MediaConverter.csproj"))) return directory.FullName;
    }
    throw new DirectoryNotFoundException("Could not find the EZ Converter project root.");
}

static void Require(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException("FAIL: " + message);
}
