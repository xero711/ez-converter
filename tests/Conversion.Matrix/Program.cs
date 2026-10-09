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
    var ffmpeg = FindTool(repositoryRoot, [@"Tools\FFmpeg\bin\ffmpeg.exe"]) ?? ToolLocator.FindFfmpeg();
    if (ffmpeg is null)
    {
        var ffmpegUpdater = new FfmpegUpdateService(Path.Combine(workRoot, "managed-tools"));
        _ = await ffmpegUpdater.UpdateIfNeededAsync(force: true);
        ffmpeg = ffmpegUpdater.ExecutablePath;
    }
    if (!File.Exists(ffmpeg))
        throw new FileNotFoundException("FFmpeg could not be prepared for the bundled-format conversion matrix.", ffmpeg);
    var ffprobe = Path.Combine(Path.GetDirectoryName(ffmpeg)!, "ffprobe.exe");
    if (!File.Exists(ffprobe)) throw new FileNotFoundException("ffprobe is required to inspect converted video fixtures.", ffprobe);
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

    var imageOnlyHtml = Path.Combine(workRoot, "image-only.html");
    await File.WriteAllTextAsync(imageOnlyHtml,
        "<!doctype html><html><body><svg xmlns=\"http://www.w3.org/2000/svg\" width=\"64\" height=\"48\"><rect width=\"64\" height=\"48\" fill=\"#f05a70\"/></svg></body></html>",
        new System.Text.UTF8Encoding(false));
    var imageOnlyPdf = await ConvertAsync(service, imageOnlyHtml, "pdf", Path.Combine(workRoot, "image-only-pdf"), officeTools);
    var noOcrOutput = Path.Combine(workRoot, "image-only-pdf-text");
    var imageOnlyRejected = false;
    try { _ = await ConvertAsync(service, imageOnlyPdf, "txt", noOcrOutput, new Dictionary<ConversionBackend, string?>()); }
    catch (ConversionException exception) when (exception.Message.Contains("抽出できる文字がありません", StringComparison.Ordinal))
    { imageOnlyRejected = true; }
    Require(imageOnlyRejected, "Image-only PDF is rejected clearly when OCR is unavailable");
    Require(!Directory.EnumerateFileSystemEntries(noOcrOutput).Any(), "Rejected image-only PDF leaves no partial TXT or staging output");

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

    var flatPresentationPath = Path.Combine(workRoot, "presentation.fodp");
    await File.WriteAllTextAsync(flatPresentationPath,
        """
        <?xml version="1.0" encoding="UTF-8"?>
        <office:document xmlns:office="urn:oasis:names:tc:opendocument:xmlns:office:1.0" xmlns:style="urn:oasis:names:tc:opendocument:xmlns:style:1.0" xmlns:text="urn:oasis:names:tc:opendocument:xmlns:text:1.0" xmlns:draw="urn:oasis:names:tc:opendocument:xmlns:drawing:1.0" xmlns:presentation="urn:oasis:names:tc:opendocument:xmlns:presentation:1.0" xmlns:fo="urn:oasis:names:tc:opendocument:xmlns:xsl-fo-compatible:1.0" xmlns:svg="urn:oasis:names:tc:opendocument:xmlns:svg-compatible:1.0" office:mimetype="application/vnd.oasis.opendocument.presentation" office:version="1.2">
          <office:font-face-decls/>
          <office:styles>
            <style:default-style style:family="graphic"><style:graphic-properties draw:stroke="none" draw:fill="none"/></style:default-style>
            <style:style style:name="page" style:family="drawing-page"/>
            <style:style style:name="title" style:family="presentation"><style:graphic-properties draw:stroke="none" draw:fill="none"/><style:paragraph-properties fo:text-align="center"/><style:text-properties fo:font-size="28pt" fo:font-weight="bold"/></style:style>
            <style:style style:name="page-layout" style:family="page-layout"><style:page-layout-properties fo:page-width="28cm" fo:page-height="15.75cm" style:print-orientation="landscape"/></style:style>
            <style:style style:name="title-layout" style:family="presentation-page-layout"><presentation:placeholder presentation:object="title" svg:x="2cm" svg:y="1cm" svg:width="24cm" svg:height="5cm"/></style:style>
          </office:styles>
          <office:automatic-styles/>
          <office:master-styles><style:master-page style:name="Default" style:page-layout-name="page-layout"/></office:master-styles>
          <office:body><office:presentation><draw:page office:name="Slide 1" draw:style-name="page" draw:master-page-name="Default" presentation:page-layout-name="title-layout"><draw:frame draw:style-name="title" presentation:class="title" svg:x="2cm" svg:y="1cm" svg:width="24cm" svg:height="5cm"><draw:text-box><text:p text:style-name="title">EZ Converter Presentation Fixture</text:p></draw:text-box></draw:frame></draw:page></office:presentation></office:body>
        </office:document>
        """,
        new System.Text.UTF8Encoding(false));
    var presentationDirectory = Path.Combine(workRoot, "presentation-fixture");
    Directory.CreateDirectory(presentationDirectory);
    var presentationProfile = new Uri(Path.Combine(workRoot, "presentation-fixture-profile") + Path.DirectorySeparatorChar).AbsoluteUri;
    await RequireSuccessAsync(libreOffice,
        ["--headless", $"-env:UserInstallation={presentationProfile}", "--convert-to", "odp", "--outdir", presentationDirectory, flatPresentationPath],
        workRoot, "Flat ODF presentation fixture");
    var odpPath = Path.Combine(presentationDirectory, "presentation.odp");
    Require(File.Exists(odpPath) && new FileInfo(odpPath).Length > 0, "LibreOffice imported the synthetic flat ODF presentation");
    var pptxPath = await ConvertAsync(service, odpPath, "pptx", Path.Combine(workRoot, "presentations"), officeTools);
    using (var pptx = ZipFile.OpenRead(pptxPath))
    {
        Require(pptx.GetEntry("ppt/presentation.xml") is not null && pptx.GetEntry("ppt/slides/slide1.xml") is not null,
            "ODP -> PPTX produced a valid OpenXML presentation package");
    }
    var presentationPdf = await ConvertAsync(service, pptxPath, "pdf", Path.Combine(workRoot, "presentation-pdf"), officeTools);
    var presentationTextPath = await ConvertAsync(service, presentationPdf, "txt", Path.Combine(workRoot, "presentation-text"), new Dictionary<ConversionBackend, string?>());
    Require((await File.ReadAllTextAsync(presentationTextPath)).Contains("EZ Converter Presentation Fixture", StringComparison.Ordinal),
        "ODP -> PPTX -> PDF -> TXT preserves the slide text");

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

    var sourceTar = Path.Combine(workRoot, "source.tar");
    await RequireSuccessAsync(sevenZip, ["a", "-ttar", sourceTar, ".", "-y"], archiveSourceDirectory, "TAR fixture");
    var sourceTarGz = Path.Combine(workRoot, "source.tar.gz");
    await RequireSuccessAsync(sevenZip, ["a", "-tgzip", sourceTarGz, sourceTar, "-y"], workRoot, "TAR.GZ fixture");
    var tarGzOutput = await ConvertAsync(service, sourceTarGz, "zip", Path.Combine(workRoot, "tar-gz-archives"), archiveTools);
    var tarGzExtracted = Path.Combine(workRoot, "tar-gz-extracted");
    Directory.CreateDirectory(tarGzExtracted);
    await RequireSuccessAsync(sevenZip, ["x", tarGzOutput, $"-o{tarGzExtracted}", "-y"], workRoot, "TAR.GZ -> ZIP content check");
    Require(await File.ReadAllTextAsync(Path.Combine(tarGzExtracted, "日本語 & sample.txt")) == "archive fixture",
        "TAR.GZ -> ZIP preserves the Japanese file name and contents");

    var videoPath = Path.Combine(workRoot, "video source.mp4");
    await RequireSuccessAsync(ffmpeg,
        ["-hide_banner", "-loglevel", "error", "-f", "lavfi", "-i", "testsrc2=size=256x144:rate=24",
         "-f", "lavfi", "-i", "sine=frequency=1000:sample_rate=48000", "-t", "1", "-c:v", "libx264",
         "-pix_fmt", "yuv420p", "-c:a", "aac", "-y", videoPath], workRoot, "MP4 fixture");
    var videoTools = new Dictionary<ConversionBackend, string?> { [ConversionBackend.Ffmpeg] = ffmpeg };
    var threeGpPath = await ConvertAsync(service, videoPath, "3gp", Path.Combine(workRoot, "video-3gp"), videoTools);
    var threeGpProbe = await ProbeAsync(ffprobe, threeGpPath);
    Require(threeGpProbe.Contains("3gp", StringComparison.OrdinalIgnoreCase)
            && threeGpProbe.Contains("h264", StringComparison.OrdinalIgnoreCase)
            && threeGpProbe.Contains("aac", StringComparison.OrdinalIgnoreCase),
        "MP4 -> 3GP is readable and contains H.264 video and AAC audio");
    var xvidPath = await ConvertAsync(service, videoPath, "xvid", Path.Combine(workRoot, "video-xvid"), videoTools);
    var xvidProbe = await ProbeAsync(ffprobe, xvidPath);
    Require(xvidProbe.Contains("avi", StringComparison.OrdinalIgnoreCase)
            && xvidProbe.Contains("mpeg4", StringComparison.OrdinalIgnoreCase)
            && xvidProbe.Contains("xvid", StringComparison.OrdinalIgnoreCase),
        "MP4 -> Xvid is readable as AVI with the MPEG-4 codec and XVID tag");

    var audioPath = Path.Combine(workRoot, "audio source.wav");
    await RequireSuccessAsync(ffmpeg,
        ["-hide_banner", "-loglevel", "error", "-f", "lavfi", "-i", "sine=frequency=660:sample_rate=44100",
         "-t", "1", "-c:a", "pcm_s16le", "-y", audioPath], workRoot, "WAV fixture");
    var audioTools = new Dictionary<ConversionBackend, string?> { [ConversionBackend.Ffmpeg] = ffmpeg };
    foreach (var (extension, codec) in new[] { ("mp3", "mp3"), ("flac", "flac") })
    {
        var audioOutput = await ConvertAsync(service, audioPath, extension, Path.Combine(workRoot, "audio-" + extension), audioTools);
        var audioProbe = await ProbeAsync(ffprobe, audioOutput);
        Require(audioProbe.Contains(codec, StringComparison.OrdinalIgnoreCase)
                && audioProbe.Contains("audio", StringComparison.OrdinalIgnoreCase)
                && !audioProbe.Contains("video", StringComparison.OrdinalIgnoreCase),
            $"WAV -> {extension.ToUpperInvariant()} contains the expected audio codec and no video stream");
    }

    var svgPath = Path.Combine(workRoot, "vector source.svg");
    await File.WriteAllTextAsync(svgPath,
        "<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"64\" height=\"48\"><rect width=\"64\" height=\"48\" fill=\"#f05a70\"/></svg>",
        new System.Text.UTF8Encoding(false));
    var vectorTools = new Dictionary<ConversionBackend, string?> { [ConversionBackend.ImageMagick] = imageMagick };
    foreach (var extension in new[] { "eps", "ps" })
    {
        var postScriptPath = await ConvertAsync(service, svgPath, extension, Path.Combine(workRoot, "vectors"), vectorTools);
        var postScriptText = await File.ReadAllTextAsync(postScriptPath);
        Require(postScriptText.StartsWith("%!PS-Adobe-", StringComparison.Ordinal)
                && postScriptText.Contains("%%BoundingBox:", StringComparison.Ordinal)
                && postScriptText.TrimEnd().EndsWith("%%EOF", StringComparison.Ordinal),
            $"SVG -> {extension.ToUpperInvariant()} produces a structurally complete PostScript document rather than a renamed raster output");
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

static async Task<string> ProbeAsync(string ffprobe, string path)
{
    var result = await ExternalToolRunner.RunAsync(ffprobe,
        ["-v", "error", "-show_entries", "format=format_name:stream=codec_type,codec_name,codec_tag_string",
         "-of", "default=noprint_wrappers=1:nokey=1", path], CancellationToken.None);
    Require(result.ExitCode == 0, "ffprobe could not read converted video: " + result.StandardError);
    return result.StandardOutput;
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
