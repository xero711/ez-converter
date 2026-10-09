using System.IO;
using System.Text;
using MediaConverter.Models;
using System.Globalization;

namespace MediaConverter.Services;

public sealed class ConversionService
{
    public async Task<string> ConvertAsync(
        MediaItem item,
        MediaFormat targetFormat,
        string outputDirectory,
        IReadOnlyDictionary<ConversionBackend, string?> toolPaths,
        IProgress<int>? progress,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(item.SourcePath))
        {
            throw new FileNotFoundException("入力ファイルが見つかりません。", item.SourcePath);
        }

        if (item.SourceFormat is null || item.Kind == MediaKind.Unknown)
        {
            throw new InvalidOperationException("入力ファイルの形式が対応一覧にありません。");
        }

        if (!targetFormat.CanWrite)
        {
            throw new InvalidOperationException($"{targetFormat.DisplayName}は読み込み専用形式です。別の出力形式を選択してください。");
        }

        var backend = ResolveBackend(item.SourceFormat, targetFormat);
        if (backend is null)
        {
            throw new InvalidOperationException(
                $"この組み合わせでは変換できません。入力: {item.SourceFormat.DisplayName} / 出力: {targetFormat.DisplayName}。");
        }

        string? executable = null;
        if (backend != ConversionBackend.PdfText &&
            (!toolPaths.TryGetValue(backend.Value, out executable) || executable is null))
        {
            throw new InvalidOperationException($"{GetBackendName(backend.Value)}が見つかりません。アプリを再起動して、もう一度お試しください。");
        }

        Directory.CreateDirectory(outputDirectory);
        var outputPath = GetAvailableOutputPath(outputDirectory, item.SourcePath, targetFormat.Extension);
        var stagingDirectory = Path.Combine(outputDirectory, ".ezconverter-stage-" + Guid.NewGuid().ToString("N"));
        var stagingPath = Path.Combine(stagingDirectory, Path.GetFileName(outputPath));
        Directory.CreateDirectory(stagingDirectory);
        progress?.Report(5);

        try
        {
            _ = backend.Value switch
            {
                ConversionBackend.PdfText => await PdfTextConversionService.ConvertAsync(item.SourcePath, stagingPath, targetFormat.Extension, progress, cancellationToken),
                ConversionBackend.ImageMagick => await ConvertWithImageMagickAsync(executable!, item.SourcePath, stagingPath, cancellationToken),
                ConversionBackend.Ffmpeg => await ConvertWithFfmpegAsync(executable!, item.SourcePath, stagingPath, targetFormat, targetFormat.Kind, progress, cancellationToken),
                ConversionBackend.LibreOffice => await ConvertWithLibreOfficeAsync(executable!, item.SourcePath, stagingPath, targetFormat.Extension, cancellationToken),
                ConversionBackend.SevenZip => await ConvertWithSevenZipAsync(executable!, item.SourcePath, item.SourceFormat.Extension, stagingPath, targetFormat.Extension, cancellationToken),
                ConversionBackend.Calibre => await ConvertWithCalibreAsync(executable!, item.SourcePath, stagingPath, cancellationToken),
                ConversionBackend.FontForge => await ConvertWithFontForgeAsync(executable!, item.SourcePath, stagingPath, cancellationToken),
                _ => throw new InvalidOperationException("未対応の変換エンジンです.")
            };

            cancellationToken.ThrowIfCancellationRequested();
            if (!File.Exists(stagingPath))
            {
                throw new ConversionException("変換エンジンは成功を返しましたが、出力ファイルが作成されませんでした。");
            }

            if (new FileInfo(stagingPath).Length == 0)
            {
                throw new ConversionException("変換結果が空です。入力内容と出力形式を確認してください。");
            }

            await ValidateOutputAsync(backend.Value, executable, item.SourcePath, stagingPath, targetFormat, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(stagingPath, outputPath, overwrite: false);
            progress?.Report(100);
            return outputPath;
        }
        finally
        {
            TryDeleteDirectory(stagingDirectory);
        }
    }

    public static ConversionBackend? ResolveBackend(MediaFormat sourceFormat, MediaFormat targetFormat) =>
        ConversionRoutes.Resolve(sourceFormat, targetFormat);

    private static async Task<ToolRunResult> ConvertWithImageMagickAsync(
        string executable, string inputPath, string outputPath, CancellationToken cancellationToken)
    {
        var extension = Path.GetExtension(outputPath);
        var xbmOutputPath = extension.Equals(".xbm", StringComparison.OrdinalIgnoreCase)
            ? Path.Combine(Path.GetDirectoryName(outputPath)!, "converted.xbm")
            : outputPath;
        var arguments = new List<string> { inputPath };
        if (extension.Equals(".map", StringComparison.OrdinalIgnoreCase))
            arguments.AddRange(["-depth", "8", "-colors", "65536"]);
        else if (extension.Equals(".psd", StringComparison.OrdinalIgnoreCase))
            arguments.AddRange(["-type", "TrueColorAlpha"]);
        arguments.Add(xbmOutputPath);
        var result = await RunAndRequireSuccessAsync(executable, arguments, cancellationToken, "ImageMagick");
        if (!xbmOutputPath.Equals(outputPath, StringComparison.OrdinalIgnoreCase)) File.Move(xbmOutputPath, outputPath);
        return result;
    }

    private static async Task<ToolRunResult> ConvertWithFfmpegAsync(
        string executable,
        string inputPath,
        string outputPath,
        MediaFormat targetFormat,
        MediaKind targetKind,
        IProgress<int>? progress,
        CancellationToken cancellationToken)
    {
        var targetExtension = targetFormat.Extension;
        var arguments = new List<string>
        {
            "-hide_banner", "-loglevel", "error", "-nostdin", "-progress", "pipe:1",
            "-stats_period", "0.5", "-nostats", "-y", "-i", inputPath
        };
        if (targetKind == MediaKind.Audio)
        {
            arguments.AddRange(["-map", "0:a:0", "-vn"]);
        }

        if (targetFormat.VideoCodec is not null)
        {
            arguments.AddRange(["-c:v", targetFormat.VideoCodec, "-cpu-used", "8", "-crf", "32", "-b:v", "0"]);
            if (targetExtension.Equals("mp4", StringComparison.OrdinalIgnoreCase))
            {
                arguments.AddRange(["-c:a", "aac", "-b:a", "192k", "-movflags", "+faststart"]);
            }
        }
        else
        {
            switch (targetExtension.ToLowerInvariant())
            {
                case "3gp":
                case "3g2":
                    arguments.AddRange(["-c:v", "libx264", "-c:a", "aac"]);
                    break;
                case "mxf":
                    arguments.AddRange(["-ar", "48000"]);
                    break;
                case "divx":
                case "xvid":
                    arguments.AddRange(["-c:v", "mpeg4", "-vtag", targetExtension.ToUpperInvariant(), "-f", "avi"]);
                    break;
                case "m4r":
                    arguments.AddRange(["-c:a", "aac", "-f", "ipod"]);
                    break;
                case "amr":
                case "gsm":
                    arguments.AddRange(["-ar", "8000", "-ac", "1"]);
                    break;
                case "swf":
                    arguments.AddRange(["-ar", "44100"]);
                    break;
            }
        }

        arguments.Add(outputPath);
        progress?.Report(6);
        var duration = await TryGetMediaDurationAsync(executable, inputPath, cancellationToken);
        var lastReportedProgress = 5;

        void ReportProgress(string line)
        {
            if (TryGetFfmpegProgress(line, duration, out var value) && value > lastReportedProgress)
            {
                lastReportedProgress = value;
                progress?.Report(value);
            }
        }

        return await RunAndRequireSuccessAsync(
            executable, arguments, cancellationToken, "FFmpeg", standardOutputLine: ReportProgress);
    }

    private static async Task<double?> TryGetMediaDurationAsync(
        string ffmpegPath, string inputPath, CancellationToken cancellationToken)
    {
        var executableDirectory = Path.GetDirectoryName(ffmpegPath);
        if (executableDirectory is null)
        {
            return null;
        }

        var ffprobePath = Path.Combine(executableDirectory, "ffprobe.exe");
        if (!File.Exists(ffprobePath))
        {
            return null;
        }

        try
        {
            var result = await ExternalToolRunner.RunAsync(
                ffprobePath,
                ["-v", "error", "-show_entries", "format=duration", "-of", "default=noprint_wrappers=1:nokey=1", inputPath],
                cancellationToken);
            return result.ExitCode == 0
                && double.TryParse(result.StandardOutput.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var duration)
                && double.IsFinite(duration) && duration > 0
                    ? duration
                    : null;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }

    private static bool TryGetFfmpegProgress(string line, double? durationSeconds, out int progressValue)
    {
        progressValue = 0;
        if (durationSeconds is null)
        {
            return false;
        }

        var separator = line.IndexOf('=');
        if (separator <= 0 || line[..separator] is not ("out_time_us" or "out_time_ms"))
        {
            return false;
        }

        if (!long.TryParse(line[(separator + 1)..], NumberStyles.Integer, CultureInfo.InvariantCulture, out var microseconds)
            || microseconds < 0)
        {
            return false;
        }

        progressValue = Math.Clamp((int)Math.Floor(microseconds / (durationSeconds.Value * 10_000d)), 5, 99);
        return true;
    }

    private static async Task<ToolRunResult> ConvertWithLibreOfficeAsync(
        string executable,
        string inputPath,
        string outputPath,
        string targetExtension,
        CancellationToken cancellationToken)
    {
        var temporaryDirectory = Path.Combine(Path.GetTempPath(), "MediaConverter", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temporaryDirectory);
        try
        {
            var temporaryInput = Path.Combine(temporaryDirectory, "source" + Path.GetExtension(inputPath));
            File.Copy(inputPath, temporaryInput);
            var profileDirectory = Path.Combine(temporaryDirectory, "profile");
            var profileUri = new Uri(profileDirectory + Path.DirectorySeparatorChar).AbsoluteUri;
            var conversionTarget = GetLibreOfficeOutputFormat(temporaryInput, targetExtension);
            var result = await RunAndRequireSuccessAsync(
                executable,
                ["--headless", $"-env:UserInstallation={profileUri}", "--convert-to", conversionTarget, "--outdir", temporaryDirectory, temporaryInput],
                cancellationToken,
                "LibreOffice",
                Path.GetDirectoryName(executable));

            var expected = Path.Combine(temporaryDirectory, Path.GetFileNameWithoutExtension(temporaryInput) + "." + targetExtension);
            if (!File.Exists(expected) || new FileInfo(expected).Length == 0)
            {
                throw new ConversionException($"LibreOfficeは成功しましたが、.{targetExtension}の出力を確認できませんでした。{TrimOutput(result.StandardError)}");
            }

            File.Copy(expected, outputPath);
            return result;
        }
        finally
        {
            TryDeleteDirectory(temporaryDirectory);
        }
    }

    private static string GetLibreOfficeOutputFormat(string inputPath, string targetExtension)
    {
        var sourceExtension = Path.GetExtension(inputPath).TrimStart('.');
        var normalizedTarget = targetExtension.TrimStart('.').ToLowerInvariant();
        var normalizedSource = sourceExtension.ToLowerInvariant();

        var filter = normalizedTarget switch
        {
            "docx" => "Office Open XML Text",
            "docm" => "MS Word 2007 XML VBA",
            "dotx" => "Office Open XML Text Template",
            "xlsx" => "Calc Office Open XML",
            "xls" => "MS Excel 97",
            "csv" => "Text - txt - csv (StarCalc)",
            "pptx" => "Impress Office Open XML",
            "ppt" => "MS PowerPoint 97",
            "odt" => "writer8",
            "ods" => "calc8",
            "odp" => "impress8",
            "pdf" when normalizedSource is "ppt" or "pptx" or "pptm" or "pps" or "ppsx" or "ppsm" or "pot" or "potx" or "potm" or "odp" => "impress_pdf_Export",
            "pdf" when normalizedSource is "xls" or "xlsx" or "xlsm" or "xlsb" or "xlt" or "xltx" or "csv" or "ods" or "ots" => "calc_pdf_Export",
            "pdf" => "writer_pdf_Export",
            _ => null
        };

        return filter is null ? targetExtension : $"{normalizedTarget}:{filter}";
    }

    private static async Task<ToolRunResult> ConvertWithSevenZipAsync(
        string executable, string inputPath, string sourceExtension, string outputPath, string targetExtension, CancellationToken cancellationToken)
    {
        var temporaryDirectory = Path.Combine(Path.GetTempPath(), "MediaConverter", Guid.NewGuid().ToString("N"));
        var contentsDirectory = Path.Combine(temporaryDirectory, "contents");
        Directory.CreateDirectory(contentsDirectory);
        try
        {
            var isTarWrapped = sourceExtension.StartsWith("tar.", StringComparison.OrdinalIgnoreCase)
                || sourceExtension is "tgz" or "tbz2";
            if (isTarWrapped)
            {
                var outerDirectory = Path.Combine(temporaryDirectory, "outer");
                Directory.CreateDirectory(outerDirectory);
                await RunAndRequireSuccessAsync(executable, ["x", inputPath, $"-o{outerDirectory}", "-y"], cancellationToken, "7-Zip");
                var tarFile = Directory.EnumerateFiles(outerDirectory, "*.tar", SearchOption.TopDirectoryOnly).SingleOrDefault();
                if (tarFile is null)
                {
                    throw new ConversionException("圧縮ファイル内のTARを取り出せませんでした。");
                }

                await RunAndRequireSuccessAsync(executable, ["x", tarFile, $"-o{contentsDirectory}", "-y"], cancellationToken, "7-Zip");
            }
            else
            {
                await RunAndRequireSuccessAsync(executable, ["x", inputPath, $"-o{contentsDirectory}", "-y"], cancellationToken, "7-Zip");
            }
            var archiveType = targetExtension.ToLowerInvariant() switch
            {
                "7z" => "7z",
                "zip" or "jar" => "zip",
                "tar" => "tar",
                "tar.7z" => "7z",
                "tar.bz" or "tbz2" => "bzip2",
                "tar.gz" or "tgz" => "gzip",
                "tar.xz" => "xz",
                _ => throw new ConversionException("選択した圧縮形式への書き出しには対応していません。")
            };

            if (targetExtension is "7z" or "zip" or "jar" or "tar")
            {
                return await RunAndRequireSuccessAsync(
                    executable, ["a", outputPath, ".", $"-t{archiveType}", "-y"], cancellationToken, "7-Zip", contentsDirectory);
            }

            var tarPath = Path.Combine(temporaryDirectory, "contents.tar");
            await RunAndRequireSuccessAsync(
                executable, ["a", tarPath, ".", "-ttar", "-y"], cancellationToken, "7-Zip", contentsDirectory);
            return await RunAndRequireSuccessAsync(
                executable, ["a", outputPath, tarPath, $"-t{archiveType}", "-y"], cancellationToken, "7-Zip", temporaryDirectory);
        }
        finally
        {
            TryDeleteDirectory(temporaryDirectory);
        }
    }

    private static async Task<ToolRunResult> ConvertWithCalibreAsync(
        string executable, string inputPath, string outputPath, CancellationToken cancellationToken)
    {
        return await RunAndRequireSuccessAsync(executable, [inputPath, outputPath], cancellationToken, "Calibre");
    }

    private static async Task<ToolRunResult> ConvertWithFontForgeAsync(
        string executable, string inputPath, string outputPath, CancellationToken cancellationToken)
    {
        var input = EscapeFontForgePath(inputPath);
        var output = EscapeFontForgePath(outputPath);
        var script = $"Open(\"{input}\"); Generate(\"{output}\"); Close();";
        return await RunAndRequireSuccessAsync(executable, ["-lang=ff", "-c", script], cancellationToken, "FontForge");
    }

    private static async Task<ToolRunResult> RunAndRequireSuccessAsync(
        string executable,
        IEnumerable<string> arguments,
        CancellationToken cancellationToken,
        string toolName,
        string? workingDirectory = null,
        Action<string>? standardOutputLine = null)
    {
        var result = await ExternalToolRunner.RunAsync(executable, arguments, cancellationToken, workingDirectory, standardOutputLine);
        if (result.ExitCode != 0)
        {
            throw new ConversionException($"{toolName}で変換できませんでした。\n{TrimOutput(result.StandardError)}");
        }

        return result;
    }

    private static async Task ValidateOutputAsync(
        ConversionBackend backend,
        string? executable,
        string inputPath,
        string outputPath,
        MediaFormat targetFormat,
        CancellationToken cancellationToken)
    {
        if (backend == ConversionBackend.Ffmpeg && executable is not null)
        {
            var ffprobe = Path.Combine(Path.GetDirectoryName(executable) ?? string.Empty, "ffprobe.exe");
            if (!File.Exists(ffprobe)) return;

            // Request one codec type per line; CSV can append empty fields, which
            // made valid MPEG-TS outputs look as if their video stream was absent.
            var probe = await ExternalToolRunner.RunAsync(ffprobe,
                ["-v", "error", "-show_entries", "stream=codec_type", "-of", "default=noprint_wrappers=1:nokey=1", outputPath],
                cancellationToken);
            if (probe.ExitCode != 0)
            {
                throw new ConversionException("出力をffprobeで読み直せませんでした。変換結果を破棄しました。\n" + TrimOutput(probe.StandardError));
            }

            var streamTypes = probe.StandardOutput.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
            var expectedType = targetFormat.Kind == MediaKind.Video ? "video" : "audio";
            if (!streamTypes.Contains(expectedType, StringComparer.OrdinalIgnoreCase))
            {
                throw new ConversionException($"変換結果に必要な{(expectedType == "video" ? "映像" : "音声")}ストリームがありません。出力を破棄しました。");
            }
        }
        else if (backend == ConversionBackend.SevenZip && executable is not null)
        {
            var test = await RunAndRequireSuccessAsync(executable, ["t", outputPath], cancellationToken, "7-Zipによる出力検査");
            if (test.ExitCode != 0)
            {
                throw new ConversionException("圧縮ファイルの整合性検査に失敗しました。出力を破棄しました。\n" + TrimOutput(test.StandardError));
            }
        }
        else if (backend == ConversionBackend.ImageMagick && executable is not null)
        {
            if (targetFormat.Extension.Equals("map", StringComparison.OrdinalIgnoreCase))
            {
                await ValidateImageMapOutputAsync(executable, inputPath, outputPath, cancellationToken);
            }
            else if (targetFormat.Extension.Equals("sixel", StringComparison.OrdinalIgnoreCase))
            {
                await ValidateSixelOutputAsync(outputPath, cancellationToken);
            }
            else if (targetFormat.Extension.Equals("rgb", StringComparison.OrdinalIgnoreCase)
                || targetFormat.Extension.Equals("rgba", StringComparison.OrdinalIgnoreCase)
                || targetFormat.Extension.Equals("uyvy", StringComparison.OrdinalIgnoreCase)
                || targetFormat.Extension.Equals("yuv", StringComparison.OrdinalIgnoreCase))
            {
                await ValidateRawImageOutputAsync(executable, inputPath, outputPath, targetFormat.Extension, cancellationToken);
            }
            else if (targetFormat.Extension.Equals("eps", StringComparison.OrdinalIgnoreCase)
                || targetFormat.Extension.Equals("ps", StringComparison.OrdinalIgnoreCase))
            {
                await ValidatePostScriptOutputAsync(outputPath, cancellationToken);
            }
            else
            {
                _ = await RunAndRequireSuccessAsync(executable, ["identify", "-regard-warnings", outputPath], cancellationToken, "ImageMagickによる出力検査");
            }
        }
    }

    private static async Task ValidateRawImageOutputAsync(
        string executable, string inputPath, string outputPath, string format, CancellationToken cancellationToken)
    {
        var dimensions = await RunAndRequireSuccessAsync(executable,
            ["identify", "-format", "%w %h %z", inputPath], cancellationToken, "ImageMagickによる入力画像サイズ検査");
        var values = dimensions.StandardOutput.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (values.Length != 3
            || !int.TryParse(values[0], NumberStyles.None, CultureInfo.InvariantCulture, out var width) || width <= 0
            || !int.TryParse(values[1], NumberStyles.None, CultureInfo.InvariantCulture, out var height) || height <= 0
            || !int.TryParse(values[2], NumberStyles.None, CultureInfo.InvariantCulture, out var depth) || depth is < 1 or > 64)
        {
            throw new ConversionException("生画像出力の検査に必要な入力サイズ・色深度を読み取れませんでした。");
        }

        var raw = await RunAndRequireSuccessAsync(executable,
            ["identify", "-size", $"{width}x{height}", "-depth", depth.ToString(CultureInfo.InvariantCulture), $"{format}:{outputPath}"],
            cancellationToken, "ImageMagickによる生画像出力の再読込検査");
        if (!raw.StandardOutput.Contains($"{width}x{height}", StringComparison.Ordinal))
            throw new ConversionException("生画像出力を入力と同じ寸法で読み直せませんでした。");
    }

    private static async Task ValidateImageMapOutputAsync(
        string executable, string inputPath, string outputPath, CancellationToken cancellationToken)
    {
        var dimensions = await RunAndRequireSuccessAsync(executable,
            ["identify", "-format", "%w %h %k", inputPath], cancellationToken, "ImageMagickによるMAP入力サイズ検査");
        var values = dimensions.StandardOutput.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (values.Length != 3
            || !long.TryParse(values[0], NumberStyles.None, CultureInfo.InvariantCulture, out var width) || width <= 0
            || !long.TryParse(values[1], NumberStyles.None, CultureInfo.InvariantCulture, out var height) || height <= 0
            || !long.TryParse(values[2], NumberStyles.None, CultureInfo.InvariantCulture, out var sourceColors) || sourceColors <= 0
            || width > long.MaxValue / height)
            throw new ConversionException("MAP出力の検査に必要な入力画像サイズを読み取れませんでした。");

        var pixels = width * height;
        await using var stream = new FileStream(outputPath, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, useAsync: true);
        var paletteEntries = (int)Math.Min(sourceColors, 65_536);
        var indexBytes = paletteEntries > 256 ? 2 : 1;
        var paletteBytes = (long)paletteEntries * 3;
        var expectedLength = pixels * indexBytes + paletteBytes;
        if (stream.Length != expectedLength)
            throw new ConversionException("MAP出力のパレット数、ピクセル数、またはインデックス幅が不正です。");

        stream.Position = paletteBytes;
        var buffer = new byte[64 * 1024];
        if (indexBytes == 1)
        {
            while (true)
            {
                var count = await stream.ReadAsync(buffer, cancellationToken);
                if (count == 0) break;
                for (var index = 0; index < count; index++)
                    if (buffer[index] >= paletteEntries) throw new ConversionException("MAP出力にパレット範囲外のピクセル値があります。");
            }
            return;
        }

        var bigEndianValid = true;
        var littleEndianValid = true;
        var pending = -1;
        while (true)
        {
            var count = await stream.ReadAsync(buffer, cancellationToken);
            if (count == 0) break;
            for (var index = 0; index < count; index++)
            {
                if (pending < 0) pending = buffer[index];
                else
                {
                    bigEndianValid &= (pending << 8 | buffer[index]) < paletteEntries;
                    littleEndianValid &= (buffer[index] << 8 | pending) < paletteEntries;
                    pending = -1;
                }
            }
        }
        if (pending >= 0 || !bigEndianValid && !littleEndianValid)
            throw new ConversionException("MAP出力にパレット範囲外の16ビットピクセル値があります。");
    }

    private static async Task ValidateSixelOutputAsync(string outputPath, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(outputPath, FileMode.Open, FileAccess.Read, FileShare.Read, 32 * 1024, useAsync: true);
        if (stream.Length < 8)
            throw new ConversionException("SIXEL出力のサイズが不正です。");

        var start = new byte[2];
        await stream.ReadExactlyAsync(start, cancellationToken);
        if (start[0] != 0x1b || start[1] != (byte)'P')
            throw new ConversionException("SIXEL出力の開始制御シーケンスが不正です。");

        var headerLength = (int)Math.Min(stream.Length - 4, 4096);
        var header = new byte[headerLength];
        await stream.ReadExactlyAsync(header, cancellationToken);
        var imageMarker = Array.IndexOf(header, (byte)'q');
        if (imageMarker < 0)
            throw new ConversionException("SIXEL出力に画像開始マーカーがありません。");

        stream.Position = stream.Length - 2;
        var terminator = new byte[2];
        await stream.ReadExactlyAsync(terminator, cancellationToken);
        if (terminator[0] != 0x1b || terminator[1] != (byte)'\\')
            throw new ConversionException("SIXEL出力の終了制御シーケンスが不正です。");

        var bodyStart = 2L + imageMarker + 1;
        var bodyEnd = stream.Length - 2;
        if (bodyStart >= bodyEnd)
            throw new ConversionException("SIXEL出力にピクセルデータがありません。");
        stream.Position = bodyStart;
        var hasColor = false;
        var hasSixelPixels = false;
        var remaining = bodyEnd - bodyStart;
        var buffer = new byte[64 * 1024];
        while (remaining > 0)
        {
            var count = await stream.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, remaining)), cancellationToken);
            if (count == 0) break;
            for (var index = 0; index < count; index++)
            {
                hasColor |= buffer[index] == (byte)'#';
                hasSixelPixels |= buffer[index] is >= (byte)'?' and <= (byte)'~';
            }
            remaining -= count;
        }
        if (remaining != 0 || !hasColor || !hasSixelPixels)
            throw new ConversionException("SIXEL出力の制御シーケンス、色、またはピクセルデータが不正です。");
    }

    private static async Task ValidatePostScriptOutputAsync(string outputPath, CancellationToken cancellationToken)
    {
        const int maxHeaderBytes = 1024 * 1024;
        const int maxFooterBytes = 8192;
        await using var stream = new FileStream(outputPath, FileMode.Open, FileAccess.Read, FileShare.Read, 16 * 1024, useAsync: true);
        var headerLength = (int)Math.Min(stream.Length, maxHeaderBytes);
        var headerBytes = new byte[headerLength];
        await stream.ReadExactlyAsync(headerBytes, cancellationToken);
        var header = Encoding.Latin1.GetString(headerBytes);
        if (!header.StartsWith("%!PS-Adobe-", StringComparison.Ordinal)
            || !header.Contains("%%BoundingBox:", StringComparison.Ordinal))
        {
            throw new ConversionException("PostScript出力のヘッダーまたはBoundingBoxを確認できませんでした。");
        }

        var footerLength = (int)Math.Min(stream.Length, maxFooterBytes);
        stream.Position = stream.Length - footerLength;
        var footerBytes = new byte[footerLength];
        await stream.ReadExactlyAsync(footerBytes, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        if (!Encoding.Latin1.GetString(footerBytes).Contains("%%EOF", StringComparison.Ordinal))
        {
            throw new ConversionException("PostScript出力の終端マーカーを確認できませんでした。");
        }
    }

    private static string GetAvailableOutputPath(string outputDirectory, string sourcePath, string extension)
    {
        var baseName = Path.GetFileNameWithoutExtension(sourcePath);
        var candidate = Path.Combine(outputDirectory, $"{baseName}.{extension}");
        var suffix = 1;
        while (File.Exists(candidate))
        {
            candidate = Path.Combine(outputDirectory, $"{baseName}_converted{suffix}.{extension}");
            suffix++;
        }

        return candidate;
    }

    private static string GetBackendName(ConversionBackend backend) => backend switch
    {
        ConversionBackend.ImageMagick => "ImageMagick",
        ConversionBackend.Ffmpeg => "FFmpeg",
        ConversionBackend.LibreOffice => "LibreOffice",
        ConversionBackend.SevenZip => "7-Zip",
        ConversionBackend.Calibre => "Calibre",
        ConversionBackend.FontForge => "FontForge",
        _ => "変換エンジン"
    };

    private static string EscapeFontForgePath(string path) =>
        path.Replace("\\", "/", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal);

    private static string TrimOutput(string output)
    {
        var text = output.Trim();
        return text.Length > 1200 ? text[..1200] + "…" : text;
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch
        {
            // Ignore exceptions during cleanup
        }
    }
}

public sealed class ConversionException : Exception
{
    public ConversionException(string message) : base(message)
    {
    }
}
