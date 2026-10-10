using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using MediaConverter;
using MediaConverter.Services;

var root = Path.Combine(Path.GetTempPath(), "EZConverter-VideoDownload-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
var passed = 0;
try
{
    var managedTools = Path.Combine(root, "managed-tools");
    var ffmpeg = ToolLocator.FindFfmpeg();
    if (ffmpeg is null)
    {
        var updater = new FfmpegUpdateService(managedTools);
        _ = await updater.UpdateIfNeededAsync(force: true);
        ffmpeg = updater.ExecutablePath;
    }
    var ytDlp = ToolLocator.FindYtDlp();
    if (ytDlp is null)
    {
        var updater = new YtDlpUpdateService(Path.Combine(managedTools, "yt-dlp"));
        _ = await updater.UpdateIfNeededAsync(force: true);
        ytDlp = updater.ExecutablePath;
    }
    if (!File.Exists(ffmpeg) || !File.Exists(ytDlp))
        throw new FileNotFoundException("FFmpeg and yt-dlp are required for URL download verification.");

    var denoUpdater = new DenoUpdateService(Path.Combine(managedTools, "deno"));
    var denoUpdate = await denoUpdater.UpdateIfNeededAsync(force: true);
    Require(denoUpdate.Updated && File.Exists(denoUpdater.ExecutablePath),
        "Deno updater downloads and installs the verified official Windows runtime: " + denoUpdate.Detail);
    var installedDeno = await ExternalToolRunner.RunAsync(
        denoUpdater.ExecutablePath, ["--version"], CancellationToken.None, root);
    Require(installedDeno.ExitCode == 0 && installedDeno.StandardOutput.Contains(denoUpdate.Version, StringComparison.Ordinal),
        "installed Deno reports the official release version");
    var cachedDenoUpdate = await denoUpdater.UpdateIfNeededAsync(force: false);
    Require(cachedDenoUpdate.Skipped, "Deno updater uses its verified 12-hour check state");
    Pass("official Deno asset download, SHA-256, executable version, and 12-hour update cache are verified");

    var mediaRoot = Path.Combine(root, "media");
    Directory.CreateDirectory(mediaRoot);
    var fixturePath = Path.Combine(mediaRoot, "synthetic media.mp4");
    var fixture = await ExternalToolRunner.RunAsync(ffmpeg,
        ["-hide_banner", "-loglevel", "error", "-f", "lavfi", "-i", "color=c=blue:s=128x96:r=12", "-t", "1", "-an", "-c:v", "mpeg4", "-movflags", "+faststart", "-y", fixturePath],
        CancellationToken.None, root);
    Require(fixture.ExitCode == 0 && File.Exists(fixturePath), "generated a synthetic MP4 fixture");
    Pass("synthetic MP4 source is valid");

    var hlsDirectory = Path.Combine(mediaRoot, "hls");
    Directory.CreateDirectory(hlsDirectory);
    var hlsManifest = Path.Combine(hlsDirectory, "playlist.m3u8");
    var hlsSegmentPattern = Path.Combine(hlsDirectory, "segment%03d.ts");
    var hlsFixture = await ExternalToolRunner.RunAsync(ffmpeg,
        ["-hide_banner", "-loglevel", "error", "-y", "-i", fixturePath, "-an", "-c:v", "mpeg2video", "-f", "hls", "-hls_time", "1", "-hls_list_size", "0", "-hls_segment_filename", hlsSegmentPattern, hlsManifest],
        CancellationToken.None, root);
    Require(hlsFixture.ExitCode == 0 && File.Exists(hlsManifest) && Directory.GetFiles(hlsDirectory, "*.ts").Length > 0,
        "generated a local HLS manifest and MPEG-TS segment");
    Pass("synthetic HLS fixture is valid");

    using var reservation = new TcpListener(IPAddress.Loopback, 0);
    reservation.Start();
    var port = ((IPEndPoint)reservation.LocalEndpoint).Port;
    reservation.Stop();
    using var listener = new HttpListener();
    listener.Prefixes.Add($"http://127.0.0.1:{port}/");
    listener.Start();
    using var serverCancellation = new CancellationTokenSource();
    var serverTask = ServeFixtureAsync(listener, mediaRoot, serverCancellation.Token);

    var outputDirectory = Path.Combine(root, "output");
    Directory.CreateDirectory(outputDirectory);
    var signedUrl = $"http://127.0.0.1:{port}/synthetic%20media.mp4?signature=local-test-123";
    var builder = typeof(MainWindow).GetMethod("BuildMediaArguments", BindingFlags.NonPublic | BindingFlags.Static)
        ?? throw new MissingMethodException("The URL download argument builder is missing.");
    var urlValidator = typeof(MainWindow).GetMethod("IsSupportedMediaUrl", BindingFlags.NonPublic | BindingFlags.Static)
        ?? throw new MissingMethodException("The URL download validator is missing.");
    var urlNormalizer = typeof(MainWindow).GetMethod("NormalizeMediaUrl", BindingFlags.NonPublic | BindingFlags.Static)
        ?? throw new MissingMethodException("The URL download normalizer is missing.");
    foreach (var (input, expected) in new[]
    {
        ("www.youtube.com/watch?v=EZCtest1234", "https://www.youtube.com/watch?v=EZCtest1234"),
        ("//youtu.be/EZCtest1234?t=1", "https://youtu.be/EZCtest1234?t=1"),
        ("  media.example.invalid/manifest.mpd?token=local-test  ", "https://media.example.invalid/manifest.mpd?token=local-test"),
        ("http://media.example.invalid/video.mp4?signature=local-test", "http://media.example.invalid/video.mp4?signature=local-test")
    })
    {
        var normalized = urlNormalizer.Invoke(null, [input]) as string;
        Require(normalized == expected, $"URL input normalizes safely: {input}");
    }
    Pass("scheme-less, protocol-relative, and whitespace-padded URLs normalize to valid HTTP(S) URLs");

    var urlForms = new[]
    {
        "https://www.youtube.com/watch?v=EZCtest1234",
        "https://youtu.be/EZCtest1234?t=1",
        "https://www.youtube.com/embed/EZCtest1234",
        "https://www.youtube.com/shorts/EZCtest1234",
        "https://www.youtube-nocookie.com/embed/EZCtest1234",
        "https://vimeo.com/76979871",
        "https://player.vimeo.com/video/76979871",
        "https://media.example.invalid/master.m3u8?token=local-test",
        "https://media.example.invalid/manifest.mpd?token=local-test",
        signedUrl
    };
    foreach (var candidate in urlForms)
    {
        Require((bool)(urlValidator.Invoke(null, [candidate]) ?? false), $"supported HTTP(S) URL form is accepted: {candidate}");
        var candidateArguments = (IReadOnlyList<string>?)builder.Invoke(null,
            [candidate, outputDirectory, "mp4", "最高品質", false, ffmpeg, null, null])
            ?? throw new InvalidOperationException("A URL form did not produce yt-dlp arguments.");
        Require(candidateArguments.Last() == candidate, $"URL form is passed intact to yt-dlp: {candidate}");
    }
    foreach (var candidate in new[]
    {
        "ftp://media.example.invalid/video.mp4",
        "file:///C:/video.mp4",
        "https://user:password@media.example.invalid/video.mp4",
        "javascript:https://media.example.invalid/video.mp4"
    })
    {
        Require(!(bool)(urlValidator.Invoke(null, [candidate]) ?? true), $"unsafe or unsupported URL form is rejected: {candidate}");
    }
    Pass("watch, short, embed, Vimeo, HLS, DASH, and signed media URL forms are validated and preserved; unsupported schemes and embedded credentials are rejected");

    var arguments = (IReadOnlyList<string>?)builder.Invoke(null, [signedUrl, outputDirectory, "mp4", "最高品質", false, ffmpeg, null, null])
        ?? throw new InvalidOperationException("The URL download arguments were not created.");
    Require(arguments.Last() == signedUrl, "signed direct-media query is preserved as one URL argument");
    Require(arguments.Contains("--ignore-config", StringComparer.Ordinal) && arguments.Contains("--no-playlist", StringComparer.Ordinal),
        "download uses isolated yt-dlp configuration without expanding playlists");
    Pass("signed direct-media URL is accepted and passed intact to yt-dlp");

    using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(1));
    var result = await ExternalToolRunner.RunAsync(ytDlp, arguments, deadline.Token, outputDirectory);
    Require(result.ExitCode == 0, "yt-dlp completes a signed HTTP(S) direct-media URL: " + result.StandardError);
    var findDownloadedPath = typeof(MainWindow).GetMethod("FindDownloadedFilePath", BindingFlags.NonPublic | BindingFlags.Static)
        ?? throw new MissingMethodException("The URL download output resolver is missing.");
    var downloadedPath = findDownloadedPath.Invoke(null, [result, outputDirectory]) as string;
    Require(downloadedPath is not null && File.Exists(downloadedPath), "URL download returns the saved file path");
    var playable = await ExternalToolRunner.RunAsync(ffmpeg,
        ["-hide_banner", "-loglevel", "error", "-i", downloadedPath!, "-f", "null", "-"], CancellationToken.None, root);
    Require(playable.ExitCode == 0, "downloaded MP4 decodes successfully with FFmpeg");
    Pass("signed HTTP media URL downloads through yt-dlp and produces a valid playable MP4");

    var hlsUrl = $"http://127.0.0.1:{port}/hls/playlist.m3u8?signature=local-hls-test";
    var hlsOutputDirectory = Path.Combine(root, "hls-output");
    Directory.CreateDirectory(hlsOutputDirectory);
    var hlsArguments = (IReadOnlyList<string>?)builder.Invoke(null,
        [hlsUrl, hlsOutputDirectory, "mp4", "最高品質", false, ffmpeg, null, null])
        ?? throw new InvalidOperationException("HLS URL arguments were not created.");
    Require(hlsArguments.Last() == hlsUrl, "signed HLS URL is passed intact to yt-dlp");
    var hlsResult = await ExternalToolRunner.RunAsync(ytDlp, hlsArguments, deadline.Token, hlsOutputDirectory);
    Require(hlsResult.ExitCode == 0, "yt-dlp retrieves a local signed HLS stream: " + hlsResult.StandardError);
    var hlsPath = findDownloadedPath.Invoke(null, [hlsResult, hlsOutputDirectory]) as string;
    Require(hlsPath is not null && File.Exists(hlsPath), "HLS download returns the saved file path");
    var hlsPlayable = await ExternalToolRunner.RunAsync(ffmpeg,
        ["-hide_banner", "-loglevel", "error", "-i", hlsPath!, "-f", "null", "-"], CancellationToken.None, root);
    Require(hlsPlayable.ExitCode == 0, "downloaded HLS MP4 decodes successfully with FFmpeg");
    Pass("signed HLS URL downloads through yt-dlp and produces a valid playable MP4");

    var liveSmokeUrls = new List<string>();
    for (var argumentIndex = 0; argumentIndex < args.Length; argumentIndex++)
    {
        if (args[argumentIndex] != "--live-site-smoke") continue;
        if (argumentIndex + 1 >= args.Length || args[argumentIndex + 1].StartsWith("--", StringComparison.Ordinal))
            throw new ArgumentException("Each --live-site-smoke option requires a public HTTP(S) video URL.");
        liveSmokeUrls.Add(args[++argumentIndex]);
    }

    foreach (var inputUrl in liveSmokeUrls)
    {
        var liveSmokeUrl = urlNormalizer.Invoke(null, [inputUrl]) as string;
        Require(!string.IsNullOrWhiteSpace(liveSmokeUrl) && (bool)(urlValidator.Invoke(null, [liveSmokeUrl]) ?? false),
            $"live-site smoke URL is normalized and passes the app's safe HTTP(S) validation: {inputUrl}");
        var liveArguments = ((IReadOnlyList<string>?)builder.Invoke(null,
            [liveSmokeUrl!, outputDirectory, "mp4", "360p", false, ffmpeg, null, null])
            ?? throw new InvalidOperationException("The live-site URL did not produce yt-dlp arguments.")).ToList();
        liveArguments.RemoveAt(liveArguments.Count - 1);
        liveArguments.Add("--simulate");
        liveArguments.Add("--no-progress");
        liveArguments.AddRange(["--print", "%(extractor_key)s:%(id)s\t%(title)s", liveSmokeUrl!]);
        using var liveDeadline = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        var liveResult = await ExternalToolRunner.RunAsync(ytDlp, liveArguments, liveDeadline.Token, outputDirectory);
        Require(liveResult.ExitCode == 0 && !string.IsNullOrWhiteSpace(liveResult.StandardOutput),
            "yt-dlp extracts public video metadata from the supplied live-site URL without saving media: " + liveResult.StandardError);
        Pass("live-site smoke extracted public video metadata through the app's yt-dlp arguments; --simulate avoided media downloads");
    }

    serverCancellation.Cancel();
    listener.Stop();
    try { await serverTask; } catch (HttpListenerException) { }

    Console.WriteLine($"VIDEO DOWNLOAD INTEGRATION PASSED: {passed}");
}
finally
{
    var fullRoot = Path.GetFullPath(root);
    var expectedPrefix = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar + "EZConverter-VideoDownload-";
    if (fullRoot.StartsWith(expectedPrefix, StringComparison.OrdinalIgnoreCase) && Directory.Exists(fullRoot))
    {
        try { Directory.Delete(fullRoot, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}

void Require(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException("FAIL: " + message);
    passed++;
}

void Pass(string message)
{
    Console.WriteLine("PASS: " + message);
    passed++;
}

static async Task ServeFixtureAsync(HttpListener listener, string mediaRoot, CancellationToken cancellationToken)
{
    var rootPath = Path.GetFullPath(mediaRoot).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
    while (!cancellationToken.IsCancellationRequested && listener.IsListening)
    {
        HttpListenerContext context;
        try { context = await listener.GetContextAsync().WaitAsync(cancellationToken); }
        catch (OperationCanceledException) { return; }
        catch (HttpListenerException) { return; }

        try
        {
            var relativePath = Uri.UnescapeDataString(context.Request.Url?.AbsolutePath.TrimStart('/') ?? string.Empty)
                .Replace('/', Path.DirectorySeparatorChar);
            var filePath = Path.GetFullPath(Path.Combine(rootPath, relativePath));
            if (!filePath.StartsWith(rootPath, StringComparison.OrdinalIgnoreCase) || !File.Exists(filePath))
            {
                context.Response.StatusCode = (int)HttpStatusCode.NotFound;
                continue;
            }

            var bytes = await File.ReadAllBytesAsync(filePath, cancellationToken);
            context.Response.Headers["Accept-Ranges"] = "bytes";
            context.Response.ContentType = Path.GetExtension(filePath).ToLowerInvariant() switch
            {
                ".m3u8" => "application/vnd.apple.mpegurl",
                ".mp4" => "video/mp4",
                ".ts" => "video/mp2t",
                _ => "application/octet-stream"
            };
            if (context.Request.Headers["Range"] is { } range && TryParseRange(range, bytes.Length, out var start, out var end))
            {
                var count = end - start + 1;
                context.Response.StatusCode = (int)HttpStatusCode.PartialContent;
                context.Response.Headers["Content-Range"] = $"bytes {start}-{end}/{bytes.Length}";
                context.Response.ContentLength64 = count;
                if (context.Request.HttpMethod != "HEAD")
                    await context.Response.OutputStream.WriteAsync(bytes.AsMemory(start, count), cancellationToken);
            }
            else
            {
                context.Response.ContentLength64 = bytes.Length;
                if (context.Request.HttpMethod != "HEAD") await context.Response.OutputStream.WriteAsync(bytes, cancellationToken);
            }
        }
        catch (IOException) { }
        finally
        {
            context.Response.Close();
        }
    }
}

static bool TryParseRange(string header, int length, out int start, out int end)
{
    start = 0;
    end = length - 1;
    if (!header.StartsWith("bytes=", StringComparison.OrdinalIgnoreCase)) return false;
    var parts = header[6..].Split('-', 2);
    return parts.Length == 2 && int.TryParse(parts[0], out start) &&
        (string.IsNullOrWhiteSpace(parts[1]) || int.TryParse(parts[1], out end)) &&
        start >= 0 && end >= start && end < length;
}
