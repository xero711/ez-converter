using MediaConverter.Models;
using MediaConverter.Services;

var root = Path.Combine(Path.GetTempPath(), "EZConverter-AudioIntegration-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
try
{
    var ffmpeg = ToolLocator.FindFfmpeg();
    if (ffmpeg is null)
    {
        var updater = new FfmpegUpdateService(Path.Combine(root, "managed-tools"));
        _ = await updater.UpdateIfNeededAsync(force: true);
        ffmpeg = updater.ExecutablePath;
    }
    if (!File.Exists(ffmpeg))
    {
        throw new FileNotFoundException("FFmpeg could not be prepared for the audio conversion integration check.");
    }

    var videoPath = Path.Combine(root, "video-with-audio.mp4");
    var fixture = await ExternalToolRunner.RunAsync(ffmpeg,
        ["-hide_banner", "-loglevel", "error", "-y", "-f", "lavfi", "-i", "testsrc=size=160x120:rate=24",
         "-f", "lavfi", "-i", "sine=frequency=440:sample_rate=44100", "-t", "2", "-c:v", "mpeg4", "-c:a", "aac", "-shortest", videoPath],
        CancellationToken.None, root);
    Ensure(fixture.ExitCode == 0 && File.Exists(videoPath), "Synthetic video-with-audio fixture was created.");

    var source = new MediaItem(videoPath);
    var mp3 = MediaFormatCatalog.FindByExtension("mp3")
        ?? throw new InvalidOperationException("MP3 format is not registered.");
    var visibleAudioTargets = source.AvailableOutputFormats.Select(format => format.Extension).ToHashSet(StringComparer.OrdinalIgnoreCase);
    Ensure(visibleAudioTargets.Contains("mp3") && visibleAudioTargets.Contains("wav") && visibleAudioTargets.Contains("flac"),
        "Video conversion candidates include common audio formats.");
    Ensure(ConversionRoutes.Resolve(source.SourceFormat!, mp3) == ConversionBackend.Ffmpeg,
        "Video-to-audio route uses FFmpeg.");

    var outputDirectory = Path.Combine(root, "converted");
    var service = new ConversionService();
    var outputPath = await service.ConvertAsync(source, mp3, outputDirectory,
        new Dictionary<ConversionBackend, string?> { [ConversionBackend.Ffmpeg] = ffmpeg },
        progress: null, CancellationToken.None);
    Ensure(File.Exists(outputPath) && new FileInfo(outputPath).Length > 0, "Audio-only output was created.");

    var ffprobe = Path.Combine(Path.GetDirectoryName(ffmpeg)!, "ffprobe.exe");
    if (!File.Exists(ffprobe))
    {
        throw new InvalidOperationException("FFprobe が見つからず、出力ストリームを検証できません。");
    }

    var probe = await ExternalToolRunner.RunAsync(ffprobe,
        ["-v", "error", "-show_entries", "stream=codec_type", "-of", "csv=p=0", outputPath],
        CancellationToken.None, root);
    Ensure(probe.ExitCode == 0 && probe.StandardOutput.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
        .SequenceEqual(["audio"], StringComparer.OrdinalIgnoreCase), "Output contains audio only, with no video stream.");

    Ensure(ConversionRoutes.Resolve(source.SourceFormat!, MediaFormatCatalog.FindByExtension("png")!) is null,
        "Video-to-image conversion remains unavailable.");
    Console.WriteLine("PASS: video-to-audio candidate, FFmpeg conversion, and audio-only stream validation.");
}
finally
{
    try { Directory.Delete(root, recursive: true); }
    catch (IOException) { }
    catch (UnauthorizedAccessException) { }
}

static void Ensure(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException("FAIL: " + message);
    Console.WriteLine("PASS: " + message);
}
