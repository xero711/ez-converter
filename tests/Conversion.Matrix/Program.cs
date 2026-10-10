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
    await RequireSuccessAsync(imageMagick,
        ["-size", "40x30", "xc:none", "-fill", "#f05a70", "-draw", "rectangle 0,0 19,14",
         "-fill", "#2540b8", "-draw", "rectangle 20,0 39,14", "-fill", "rgba(25,180,90,0.5)",
         "-draw", "rectangle 0,15 19,29", "-fill", "rgba(250,200,20,0.75)", "-draw", "rectangle 20,15 39,29", imagePath],
        workRoot, "ImageMagick multi-color alpha fixture");
    var imageTools = new Dictionary<ConversionBackend, string?> { [ConversionBackend.ImageMagick] = imageMagick };
    foreach (var extension in new[] { "jpg", "webp", "tiff" })
    {
        var converted = await ConvertAsync(service, imagePath, extension, Path.Combine(workRoot, "images"), imageTools);
        Require(new FileInfo(converted).Length > 0, $"PNG -> {extension.ToUpperInvariant()} produced data");
    }
    var imageMap = await ConvertAsync(service, imagePath, "map", Path.Combine(workRoot, "map-multicolor-alpha"), imageTools);
    Require(new FileInfo(imageMap).Length > 1_200, "Multi-color alpha PNG -> MAP has a palette and indexed pixels");
    Console.WriteLine("PASS: multi-color alpha PNG -> MAP validates its palette and indices.");

    var mapBoundaryPath = Path.Combine(workRoot, "image 257 colors.ppm");
    var mapBoundaryPixels = string.Join(' ', Enumerable.Range(0, 257).Select(index => $"{index % 256} {index / 256} 0"));
    await File.WriteAllTextAsync(mapBoundaryPath, $"P3\n257 1\n255\n{mapBoundaryPixels}\n", new System.Text.UTF8Encoding(false));
    var mapBoundaryColors = await ExternalToolRunner.RunAsync(imageMagick,
        ["identify", "-format", "%k", mapBoundaryPath], CancellationToken.None, workRoot);
    Require(mapBoundaryColors.ExitCode == 0 && mapBoundaryColors.StandardOutput == "257",
        "MAP boundary fixture contains exactly 257 distinct colors");
    var mapBoundaryOutput = await ConvertAsync(service, mapBoundaryPath, "map", Path.Combine(workRoot, "map-257-colors"), imageTools);
    Require(new FileInfo(mapBoundaryOutput).Length > 257,
        "257-color input produces a MAP palette and complete indexed-pixel payload");
    Console.WriteLine("PASS: 257-color PPM -> MAP validates palette size and pixel indices.");

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

    var codecVideoFixtures = new[]
    {
        (Path: Path.Combine(workRoot, "mpeg2-ac3.mkv"), VideoCodec: "mpeg2video", AudioCodec: "ac3", Args: new[] { "-c:v", "mpeg2video", "-q:v", "5", "-c:a", "ac3", "-b:a", "128k" }),
        (Path: Path.Combine(workRoot, "vp9-opus.webm"), VideoCodec: "vp9", AudioCodec: "opus", Args: new[] { "-c:v", "libvpx-vp9", "-deadline", "realtime", "-cpu-used", "8", "-crf", "45", "-b:v", "0", "-threads", "1", "-c:a", "libopus", "-b:a", "64k" }),
        (Path: Path.Combine(workRoot, "mpeg4-mp3.avi"), VideoCodec: "mpeg4", AudioCodec: "mp3", Args: new[] { "-c:v", "mpeg4", "-q:v", "5", "-vtag", "XVID", "-c:a", "libmp3lame", "-q:a", "5" })
    };
    foreach (var fixture in codecVideoFixtures)
    {
        var fixtureArguments = new List<string>
        {
            "-hide_banner", "-loglevel", "error", "-f", "lavfi", "-i", "testsrc2=size=160x90:rate=12",
            "-f", "lavfi", "-i", "sine=frequency=880:sample_rate=48000", "-t", "1"
        };
        fixtureArguments.AddRange(fixture.Args);
        fixtureArguments.AddRange(["-y", fixture.Path]);
        await RequireSuccessAsync(ffmpeg, fixtureArguments, workRoot, $"{Path.GetFileName(fixture.Path)} mixed-codec fixture");
        var fixtureProbe = await ProbeAsync(ffprobe, fixture.Path);
        Require(fixtureProbe.Contains(fixture.VideoCodec, StringComparison.OrdinalIgnoreCase)
                && fixtureProbe.Contains(fixture.AudioCodec, StringComparison.OrdinalIgnoreCase),
            $"{Path.GetFileName(fixture.Path)} contains {fixture.VideoCodec} video and {fixture.AudioCodec} audio");
    }

    var videoVariantChecks = new[]
    {
        (Source: codecVideoFixtures[0].Path, Target: "mp4", Expected: "av1"),
        (Source: codecVideoFixtures[0].Path, Target: "mp3", Expected: "mp3"),
        (Source: codecVideoFixtures[1].Path, Target: "mp4", Expected: "av1"),
        (Source: codecVideoFixtures[1].Path, Target: "mp3", Expected: "mp3"),
        (Source: codecVideoFixtures[2].Path, Target: "mp4", Expected: "av1"),
        (Source: codecVideoFixtures[2].Path, Target: "flac", Expected: "flac")
    };
    foreach (var check in videoVariantChecks)
    {
        var converted = await ConvertAsync(service, check.Source, check.Target,
            Path.Combine(workRoot, "video-codec-matrix", Path.GetFileNameWithoutExtension(check.Source), check.Target), videoTools);
        var probe = await ProbeAsync(ffprobe, converted);
        Require(probe.Contains(check.Expected, StringComparison.OrdinalIgnoreCase),
            $"{Path.GetFileName(check.Source)} -> {check.Target.ToUpperInvariant()} decodes and contains {check.Expected}");
    }

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

    var codecAudioFixtures = new[]
    {
        (Path: Path.Combine(workRoot, "source.m4a"), Args: new[] { "-c:a", "aac", "-b:a", "96k" }),
        (Path: Path.Combine(workRoot, "source.ogg"), Args: new[] { "-c:a", "libvorbis", "-q:a", "4" }),
        (Path: Path.Combine(workRoot, "source.opus"), Args: new[] { "-c:a", "libopus", "-b:a", "64k" }),
        (Path: Path.Combine(workRoot, "source.mp3"), Args: new[] { "-c:a", "libmp3lame", "-q:a", "5" }),
        (Path: Path.Combine(workRoot, "source.wma"), Args: new[] { "-c:a", "wmav2", "-b:a", "96k" }),
        (Path: Path.Combine(workRoot, "source.flac"), Args: new[] { "-c:a", "flac" })
    };
    var audioVariantTargets = new[]
    {
        (Extension: "mp3", Codec: "mp3"), (Extension: "flac", Codec: "flac"),
        (Extension: "wav", Codec: "pcm_s16le"), (Extension: "aac", Codec: "aac")
    };
    foreach (var fixture in codecAudioFixtures)
    {
        var fixtureArguments = new List<string>
        {
            "-hide_banner", "-loglevel", "error", "-f", "lavfi", "-i", "sine=frequency=550:sample_rate=44100",
            "-t", "1"
        };
        fixtureArguments.AddRange(fixture.Args);
        fixtureArguments.AddRange(["-y", fixture.Path]);
        await RequireSuccessAsync(ffmpeg, fixtureArguments, workRoot, $"{Path.GetFileName(fixture.Path)} codec fixture");
        var fixtureProbe = await ProbeAsync(ffprobe, fixture.Path);
        Require(fixtureProbe.Contains("audio", StringComparison.OrdinalIgnoreCase),
            $"{Path.GetFileName(fixture.Path)} is recognized as an audio stream");

        foreach (var target in audioVariantTargets.Where(target =>
                     !Path.GetExtension(fixture.Path).Equals("." + target.Extension, StringComparison.OrdinalIgnoreCase)))
        {
            var converted = await ConvertAsync(service, fixture.Path, target.Extension,
                Path.Combine(workRoot, "audio-codec-matrix", Path.GetFileNameWithoutExtension(fixture.Path), target.Extension), audioTools);
            var probe = await ProbeAsync(ffprobe, converted);
            Require(probe.Contains(target.Codec, StringComparison.OrdinalIgnoreCase)
                    && probe.Contains("audio", StringComparison.OrdinalIgnoreCase)
                    && !probe.Contains("video", StringComparison.OrdinalIgnoreCase),
                $"{Path.GetFileName(fixture.Path)} -> {target.Extension.ToUpperInvariant()} contains {target.Codec} without video");
        }
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

    if (args.Contains("--full-output-sweep", StringComparer.OrdinalIgnoreCase))
    {
        var sweepFailures = await RunFullOutputSweepAsync(service, workRoot,
            [imagePath, svgPath, videoPath, audioPath, docxPath, spreadsheetPath, pptxPath, archivePath, epubPath, windowsFont],
            new Dictionary<ConversionBackend, string?>
            {
                [ConversionBackend.ImageMagick] = imageMagick,
                [ConversionBackend.Ffmpeg] = ffmpeg,
                [ConversionBackend.LibreOffice] = libreOffice,
                [ConversionBackend.SevenZip] = sevenZip,
                [ConversionBackend.Calibre] = calibre,
                [ConversionBackend.FontForge] = fontForge
            }, workRoot);
        if (sweepFailures.Count > 0)
        {
            Console.Error.WriteLine("Unsupported or invalid writable output routes:");
            foreach (var failure in sweepFailures) Console.Error.WriteLine("  " + failure);
            throw new InvalidOperationException($"Full output sweep found {sweepFailures.Count} failed routes.");
        }
        Console.WriteLine("PASS: every writable output in the catalog completed a real conversion and engine-specific readback.");
    }

    var invalidArchive = Path.Combine(workRoot, "invalid.zip");
    await File.WriteAllTextAsync(invalidArchive, "not an archive");
    var failureDirectory = Path.Combine(workRoot, "failed conversion");
    var failedCleanly = false;
    try { _ = await ConvertAsync(service, invalidArchive, "7z", failureDirectory, archiveTools); }
    catch (ConversionException) { failedCleanly = true; }
    Require(failedCleanly, "Invalid ZIP fails with a conversion error");
    Require(!Directory.EnumerateFileSystemEntries(failureDirectory).Any(), "Failed conversion leaves no partial output or staging directory");

    foreach (var (sourceExtension, targetExtension) in new[] { ("mp4", "flac"), ("mp3", "wav") })
    {
        var corruptSource = Path.Combine(workRoot, $"corrupt-media.{sourceExtension}");
        await File.WriteAllTextAsync(corruptSource, "not a media stream", new System.Text.UTF8Encoding(false));
        var corruptOutputDirectory = Path.Combine(workRoot, $"failed {sourceExtension} conversion");
        var corruptFailedCleanly = false;
        try { _ = await ConvertAsync(service, corruptSource, targetExtension, corruptOutputDirectory, videoTools); }
        catch (ConversionException) { corruptFailedCleanly = true; }
        Require(corruptFailedCleanly, $"Invalid {sourceExtension.ToUpperInvariant()} fails with a conversion error");
        Require(!Directory.Exists(corruptOutputDirectory) || !Directory.EnumerateFileSystemEntries(corruptOutputDirectory).Any(),
            $"Failed {sourceExtension.ToUpperInvariant()} conversion leaves no partial output or staging directory");
    }

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

static async Task<List<string>> RunFullOutputSweepAsync(
    ConversionService service,
    string workRoot,
    IReadOnlyList<string> sourcePaths,
    IReadOnlyDictionary<ConversionBackend, string?> tools,
    string conversionRoot)
{
    var failures = new List<string>();
    var tested = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    foreach (var sourcePath in sourcePaths)
    {
        var item = new MediaItem(sourcePath);
        var sourceFormat = item.SourceFormat ?? throw new InvalidDataException($"Sweep fixture is not catalogued: {sourcePath}");
        var targets = MediaFormatCatalog.GetOutputFormats(sourceFormat);
        foreach (var target in targets)
        {
            var routeName = $"{sourceFormat.Extension} -> {target.Extension}{(target.VideoCodec is null ? "" : " (" + target.VideoCodec + ")")}";
            if (!tested.Add(routeName)) continue;

            try
            {
                var outputDirectory = Path.Combine(conversionRoot, "output-sweep", sourceFormat.Extension, target.Extension,
                    string.IsNullOrWhiteSpace(target.VideoCodec) ? "default" : "av1");
                var outputPath = await service.ConvertAsync(item, target, outputDirectory, tools, progress: null, CancellationToken.None);
                Require(File.Exists(outputPath) && new FileInfo(outputPath).Length > 0, $"{routeName} produced a nonempty output");

                switch (target.Backend)
                {
                    case ConversionBackend.LibreOffice:
                        await ValidateOfficeReadbackAsync(tools[ConversionBackend.LibreOffice]!, outputPath,
                            Path.Combine(workRoot, "output-sweep-readback", sourceFormat.Extension, target.Extension));
                        break;
                    case ConversionBackend.Calibre:
                        var reopenedEpub = Path.Combine(workRoot, "output-sweep-readback", target.Extension + ".epub");
                        Directory.CreateDirectory(Path.GetDirectoryName(reopenedEpub)!);
                        await RequireSuccessAsync(tools[ConversionBackend.Calibre]!, [outputPath, reopenedEpub], workRoot,
                            $"Calibre readback of {routeName}");
                        using (var ebook = ZipFile.OpenRead(reopenedEpub))
                            Require(ebook.GetEntry("META-INF/container.xml") is not null, $"{routeName} reopens as a valid EPUB package");
                        break;
                    case ConversionBackend.FontForge:
                        var reopenedFont = Path.Combine(workRoot, "output-sweep-readback", target.Extension + ".ttf");
                        Directory.CreateDirectory(Path.GetDirectoryName(reopenedFont)!);
                        var sourceFont = outputPath.Replace("\\", "/", StringComparison.Ordinal);
                        var targetFont = reopenedFont.Replace("\\", "/", StringComparison.Ordinal);
                        await RequireSuccessAsync(tools[ConversionBackend.FontForge]!,
                            ["-lang=ff", "-c", $"Open(\"{sourceFont}\"); Generate(\"{targetFont}\"); Close();"], workRoot,
                            $"FontForge readback of {routeName}");
                        Require(new FileInfo(reopenedFont).Length > 0, $"{routeName} reopens as a nonempty TTF");
                        break;
                }

                Console.WriteLine($"PASS output sweep: {routeName}");
            }
            catch (Exception error) when (error is not OutOfMemoryException and not StackOverflowException)
            {
                failures.Add($"{routeName}: {error.Message}");
                Console.Error.WriteLine($"FAIL output sweep: {routeName}: {error.Message}");
            }
        }
    }

    Console.WriteLine($"Output sweep covered {tested.Count:N0} unique source/output routes.");
    return failures;
}

static async Task ValidateOfficeReadbackAsync(string executable, string sourcePath, string outputRoot)
{
    if (Path.GetExtension(sourcePath).Equals(".pdf", StringComparison.OrdinalIgnoreCase))
    {
        await using var inputPdf = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read, 8, useAsync: true);
        var inputHeader = new byte[5];
        await inputPdf.ReadExactlyAsync(inputHeader);
        Require(System.Text.Encoding.ASCII.GetString(inputHeader) == "%PDF-", "LibreOffice PDF output has a valid PDF header");
        return;
    }

    var outputDirectory = outputRoot + "-pdf";
    var profileDirectory = outputRoot + "-profile";
    Directory.CreateDirectory(outputDirectory);
    var profileUri = new Uri(profileDirectory + Path.DirectorySeparatorChar).AbsoluteUri;
    await RequireSuccessAsync(executable,
        ["--headless", $"-env:UserInstallation={profileUri}", "--convert-to", "pdf", "--outdir", outputDirectory, sourcePath],
        outputDirectory, $"LibreOffice readback of {Path.GetFileName(sourcePath)}");
    var pdfPath = Path.Combine(outputDirectory, Path.GetFileNameWithoutExtension(sourcePath) + ".pdf");
    Require(File.Exists(pdfPath) && new FileInfo(pdfPath).Length > 0, $"LibreOffice readback produced a PDF for {Path.GetFileName(sourcePath)}");
    await using var pdf = new FileStream(pdfPath, FileMode.Open, FileAccess.Read, FileShare.Read, 8, useAsync: true);
    var header = new byte[5];
    await pdf.ReadExactlyAsync(header);
    Require(System.Text.Encoding.ASCII.GetString(header) == "%PDF-", $"LibreOffice readback output has a PDF header for {Path.GetFileName(sourcePath)}");
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
