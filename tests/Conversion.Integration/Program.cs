using System.Collections.Concurrent;
using System.IO;
using MediaConverter.Models;
using MediaConverter.Services;

var ffmpeg = ToolLocator.FindFfmpeg();
if (ffmpeg is null)
{
    Console.WriteLine("SKIP: FFmpeg is not installed.");
    return 0;
}

var ffprobe = Path.Combine(Path.GetDirectoryName(ffmpeg)!, "ffprobe.exe");
if (!File.Exists(ffprobe))
{
    Console.WriteLine("SKIP: ffprobe is not installed beside FFmpeg.");
    return 0;
}

var root = Path.Combine(Path.GetTempPath(), "EZConverter-AV1-Integration-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
try
{
    var sourcePath = Path.Combine(root, "source.mp4");
    var generated = await ExternalToolRunner.RunAsync(ffmpeg,
        ["-hide_banner", "-loglevel", "error", "-f", "lavfi", "-i", "testsrc2=size=256x144:rate=24",
         "-f", "lavfi", "-i", "sine=frequency=1000:sample_rate=48000", "-t", "3", "-c:v", "libx264",
         "-pix_fmt", "yuv420p", "-c:a", "aac", "-y", sourcePath], CancellationToken.None);
    Require(generated.ExitCode == 0, "Could not create an MP4 fixture: " + generated.StandardError);

    var item = new MediaItem(sourcePath);
    var av1Targets = item.AvailableOutputFormats.Where(format => format.VideoCodec == "libaom-av1").ToArray();
    Require(av1Targets.Length == 1, "MP4 does not offer exactly one AV1 MP4 output.");
    Require(item.SelectedOutputFormat?.VideoCodec is null, "AV1 unexpectedly became the default output format.");
    Require(ConversionService.ResolveBackend(item.SourceFormat!, av1Targets[0]) == ConversionBackend.Ffmpeg,
        "MP4-to-AV1 route was not resolved to FFmpeg.");

    var reportedProgress = new ConcurrentQueue<int>();
    var progress = new InlineProgress<int>(reportedProgress.Enqueue);
    var service = new ConversionService();
    var outputPath = await service.ConvertAsync(item, av1Targets[0], root,
        new Dictionary<ConversionBackend, string?> { [ConversionBackend.Ffmpeg] = ffmpeg },
        progress, CancellationToken.None);

    Require(File.Exists(outputPath) && new FileInfo(outputPath).Length > 0, "AV1 output was not created.");
    Require(await Probe(ffprobe, outputPath, "v:0") == "av1", "Output video codec is not AV1.");
    Require(await Probe(ffprobe, outputPath, "a:0") == "aac", "Output audio was not preserved as AAC.");

    var progressValues = reportedProgress.ToArray();
    Require(progressValues.Any(value => value is > 6 and < 100), "No intermediate conversion progress was reported.");
    Require(progressValues[^1] == 100, "Conversion did not report completion.");
    Require(progressValues.SequenceEqual(progressValues.Order()), "Conversion progress moved backwards.");

    Console.WriteLine($"PASS: {Path.GetFileName(outputPath)} video={await Probe(ffprobe, outputPath, "v:0")} audio={await Probe(ffprobe, outputPath, "a:0")} progress={string.Join(",", progressValues)}");
    return 0;
}
finally
{
    try
    {
        Directory.Delete(root, recursive: true);
    }
    catch (IOException)
    {
        // A failed integration run should still retain its temporary fixture for diagnosis.
    }
}

static async Task<string> Probe(string ffprobe, string path, string stream)
{
    var result = await ExternalToolRunner.RunAsync(ffprobe,
        ["-v", "error", "-select_streams", stream, "-show_entries", "stream=codec_name",
         "-of", "default=noprint_wrappers=1:nokey=1", path], CancellationToken.None);
    Require(result.ExitCode == 0, "ffprobe failed: " + result.StandardError);
    return result.StandardOutput.Trim();
}

static void Require(bool condition, string message)
{
    if (!condition)
    {
        throw new InvalidOperationException(message);
    }
}

sealed class InlineProgress<T>(Action<T> report) : IProgress<T>
{
    public void Report(T value) => report(value);
}
