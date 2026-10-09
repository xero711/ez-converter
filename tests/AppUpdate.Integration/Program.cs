using System.Net;
using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MediaConverter.Services;

var root = Path.Combine(Path.GetTempPath(), "EZConverter-AppUpdate-Integration-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
var currentVersion = new Version(1, 0, 0, 0);
var passed = 0;
void Assert(bool condition, string label)
{
    if (!condition) throw new InvalidOperationException("FAIL: " + label);
    Console.WriteLine("PASS: " + label);
    passed++;
}

try
{
    var archiveBytes = Encoding.UTF8.GetBytes("test release archive bytes");
    var hash = Convert.ToHexString(SHA256.HashData(archiveBytes)).ToLowerInvariant();
    var configured = Path.Combine(root, "configured");
    var downloadRoot = Path.Combine(root, "downloads");
    Directory.CreateDirectory(configured);
    await File.WriteAllTextAsync(Path.Combine(configured, "appsettings.json"), Settings("xero711", "update-test"));

    using (var client = new HttpClient(new FakeReleaseHandler(CreateRelease("v1.0.1", hash, includeChecksum: false), archiveBytes)))
    {
        var service = new AppUpdateService(configured, client, downloadRoot, currentVersion);
        var update = await service.CheckForUpdateAsync();
        Assert(update?.Version == new Version(1, 0, 1, 0), "new GitHub release tag is parsed and compared with the running version");
        Assert(update?.ArchiveUri.Host == "github.com", "release asset URL is restricted to the configured GitHub repository");
        var downloadedPath = await service.DownloadAndVerifyAsync(update!);
        Assert((await File.ReadAllBytesAsync(downloadedPath)).AsSpan().SequenceEqual(archiveBytes), "release archive downloads and passes its SHA-256 digest");
    }

    var mismatchRoot = Path.Combine(root, "mismatch");
    using (var client = new HttpClient(new FakeReleaseHandler(CreateRelease("v1.0.1", new string('0', 64), includeChecksum: false), archiveBytes)))
    {
        var service = new AppUpdateService(configured, client, mismatchRoot, currentVersion);
        var update = await service.CheckForUpdateAsync();
        var rejected = false;
        try { _ = await service.DownloadAndVerifyAsync(update!); }
        catch (InvalidDataException) { rejected = true; }
        Assert(rejected, "corrupt release archive is rejected before installation");
        Assert(!Directory.Exists(mismatchRoot) || !Directory.EnumerateFileSystemEntries(mismatchRoot, "*", SearchOption.AllDirectories).Any(),
            "failed update download removes its partial files");
    }

    var checksumText = $"{hash} *MediaConverter-win-x64.zip\n";
    using (var client = new HttpClient(new FakeReleaseHandler(
        CreateRelease("v1.0.2", null, includeChecksum: true, checksum: checksumText), archiveBytes, checksumText)))
    {
        var fallbackRoot = Path.Combine(root, "checksum-fallback");
        var service = new AppUpdateService(configured, client, fallbackRoot, currentVersion);
        var update = await service.CheckForUpdateAsync();
        var path = await service.DownloadAndVerifyAsync(update!);
        Assert(File.Exists(path), "SHA-256 sidecar fallback verifies releases without API digest metadata");
    }

    using (var client = new HttpClient(new FakeReleaseHandler(CreateRelease("v1.0.0", hash, includeChecksum: false), archiveBytes)))
    {
        var service = new AppUpdateService(configured, client, Path.Combine(root, "stale"), currentVersion);
        Assert(await service.CheckForUpdateAsync() is null, "same-version release does not prompt an update");
    }

    using (var client = new HttpClient(new FakeReleaseHandler(CreateRelease("v1.0.1", hash, includeChecksum: false), archiveBytes)))
    {
        var service = new AppUpdateService(configured, client, Path.Combine(root, "current"), new Version(1, 0, 1, 0));
        Assert(await service.CheckForUpdateAsync() is null, "release matching the installed application version is not offered again");
    }

    using (var client = new HttpClient(new FakeReleaseHandler(CreateRelease("v1.0.1", hash, includeChecksum: false, archiveUrl: "https://attacker.example/file.zip"), archiveBytes)))
    {
        var service = new AppUpdateService(configured, client, Path.Combine(root, "malicious"), currentVersion);
        var rejected = false;
        try { _ = await service.CheckForUpdateAsync(); }
        catch (InvalidDataException) { rejected = true; }
        Assert(rejected, "release metadata cannot redirect the updater to a non-GitHub asset host");
    }

    using (var client = new HttpClient(new FakeReleaseHandler("{}", archiveBytes)))
    {
        var service = new AppUpdateService(Path.Combine(root, "unconfigured"), client, Path.Combine(root, "none"), currentVersion);
        Assert(!service.IsConfigured && await service.CheckForUpdateAsync() is null,
            "unconfigured local build skips the GitHub request cleanly");
    }

    await VerifyUpdateAgentAsync(root);
    passed++;

    Console.WriteLine($"APP UPDATE INTEGRATION PASSED: {passed}");
}
finally
{
    try { Directory.Delete(root, recursive: true); }
    catch (IOException) { }
    catch (UnauthorizedAccessException) { }
}

static string Settings(string owner, string repository) => JsonSerializer.Serialize(new
{
    GitHub = new
    {
        Owner = owner,
        Repository = repository,
        ReleaseAssetName = "MediaConverter-win-x64.zip",
        ChecksumAssetName = "MediaConverter-win-x64.zip.sha256",
        UpdateAgentName = "MediaConverter.UpdateAgent.exe"
    }
});

static string CreateRelease(string tag, string? digest, bool includeChecksum, string? archiveUrl = null, string? checksum = null)
{
    var archive = new Dictionary<string, object?>
    {
        ["name"] = "MediaConverter-win-x64.zip",
        ["browser_download_url"] = archiveUrl ?? $"https://github.com/xero711/update-test/releases/download/{tag}/MediaConverter-win-x64.zip",
        ["size"] = 26
    };
    if (digest is not null) archive["digest"] = "sha256:" + digest;
    var assets = new List<object> { archive };
    if (includeChecksum)
        assets.Add(new
        {
            name = "MediaConverter-win-x64.zip.sha256",
            browser_download_url = $"https://github.com/xero711/update-test/releases/download/{tag}/MediaConverter-win-x64.zip.sha256",
            size = Encoding.UTF8.GetByteCount(checksum ?? string.Empty)
        });

    return JsonSerializer.Serialize(new { draft = false, tag_name = tag, name = "Test release", body = "Release notes", assets });
}

static async Task VerifyUpdateAgentAsync(string testRoot)
{
    var repositoryRoot = FindRepositoryRoot();
    var configuration = "Release";
    var agentAssembly = Path.Combine(repositoryRoot, "UpdateAgent", "bin", configuration, "net8.0", "MediaConverter.UpdateAgent.dll");
    var fixtureDirectory = Path.Combine(repositoryRoot, "tests", "AppUpdate.Integration", "Fixtures", "UpdateSentinel", "bin", configuration, "net8.0");
    if (!File.Exists(agentAssembly) || !File.Exists(Path.Combine(fixtureDirectory, "UpdateSentinel.exe")))
        throw new FileNotFoundException("The updater and sentinel fixture must be built with the integration project.");

    var installDirectory = Path.Combine(testRoot, "agent-install");
    Directory.CreateDirectory(installDirectory);
    await File.WriteAllTextAsync(Path.Combine(installDirectory, "old-version.marker"), "old installation");
    await File.WriteAllTextAsync(Path.Combine(installDirectory, "MediaConverter.exe"), "old executable placeholder");
    var markerPath = Path.Combine(testRoot, "updated-app-ran.marker");
    var archivePath = Path.Combine(testRoot, "update-fixture.zip");
    using (var archive = ZipFile.Open(archivePath, ZipArchiveMode.Create))
    {
        foreach (var file in Directory.EnumerateFiles(fixtureDirectory))
        {
            var entryName = Path.GetFileName(file) == "UpdateSentinel.exe" ? "MediaConverter.exe" : Path.GetFileName(file);
            archive.CreateEntryFromFile(file, entryName, CompressionLevel.Fastest);
        }
    }

    var startInfo = new ProcessStartInfo("dotnet")
    {
        WorkingDirectory = Path.GetDirectoryName(agentAssembly)!,
        UseShellExecute = false,
        CreateNoWindow = true,
        WindowStyle = ProcessWindowStyle.Hidden
    };
    startInfo.ArgumentList.Add(agentAssembly);
    startInfo.ArgumentList.Add("--parent-pid");
    startInfo.ArgumentList.Add("2147483647");
    startInfo.ArgumentList.Add("--archive");
    startInfo.ArgumentList.Add(archivePath);
    startInfo.ArgumentList.Add("--install-dir");
    startInfo.ArgumentList.Add(installDirectory);
    startInfo.ArgumentList.Add("--app");
    startInfo.ArgumentList.Add(Path.Combine(installDirectory, "MediaConverter.exe"));
    startInfo.Environment["EZCONVERTER_UPDATE_AGENT_MARKER"] = markerPath;

    using var agent = Process.Start(startInfo) ?? throw new InvalidOperationException("The update helper did not start.");
    await agent.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30));
    if (agent.ExitCode != 0)
        throw new InvalidOperationException($"The update helper failed with exit code {agent.ExitCode}.");
    using var launchDeadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
    string? markerContents = null;
    while (!launchDeadline.IsCancellationRequested)
    {
        try
        {
            if (File.Exists(markerPath))
            {
                markerContents = await File.ReadAllTextAsync(markerPath);
                if (markerContents == "updated app launched") break;
            }
        }
        catch (IOException)
        {
            // The sentinel process may still be closing its exclusive write handle.
        }

        await Task.Delay(TimeSpan.FromMilliseconds(50));
    }
    if (markerContents != "updated app launched")
        throw new InvalidDataException("The update helper did not launch the installed application.");
    if (!File.Exists(Path.Combine(installDirectory, "UpdateSentinel.dll")) || File.Exists(archivePath))
        throw new InvalidDataException("The update helper did not install the complete application and remove its verified archive.");

    var backups = Directory.EnumerateDirectories(testRoot, "agent-install.update-backup-*", SearchOption.TopDirectoryOnly).ToArray();
    if (backups.Length != 1 || !File.Exists(Path.Combine(backups[0], "old-version.marker")))
        throw new InvalidDataException("The update helper did not preserve a recoverable backup of the previous installation.");

    Console.WriteLine("PASS: update agent replaces an isolated install, keeps the old version as backup, and starts the updated app");
}

static string FindRepositoryRoot()
{
    for (var directory = new DirectoryInfo(Environment.CurrentDirectory); directory is not null; directory = directory.Parent)
    {
        if (File.Exists(Path.Combine(directory.FullName, "MediaConverter.csproj"))) return directory.FullName;
    }
    throw new DirectoryNotFoundException("Could not find the EZ Converter project root.");
}

sealed class FakeReleaseHandler(string releaseJson, byte[] archiveBytes, string? checksumText = null) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var uri = request.RequestUri ?? throw new InvalidOperationException("Update request URI is missing.");
        if (uri.Host.Equals("api.github.com", StringComparison.OrdinalIgnoreCase) &&
            uri.AbsolutePath.Equals("/repos/xero711/update-test/releases/latest", StringComparison.Ordinal))
            return Task.FromResult(Response(HttpStatusCode.OK, "application/json", Encoding.UTF8.GetBytes(releaseJson)));

        if (uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase) && uri.AbsolutePath.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
            return Task.FromResult(Response(HttpStatusCode.OK, "application/zip", archiveBytes));

        if (uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase) && uri.AbsolutePath.EndsWith(".zip.sha256", StringComparison.OrdinalIgnoreCase))
            return Task.FromResult(Response(HttpStatusCode.OK, "text/plain", Encoding.UTF8.GetBytes(checksumText ?? string.Empty)));

        return Task.FromResult(Response(HttpStatusCode.NotFound, "text/plain", []));
    }

    private static HttpResponseMessage Response(HttpStatusCode statusCode, string contentType, byte[] bytes) => new(statusCode)
    {
        Content = new ByteArrayContent(bytes) { Headers = { ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(contentType) } }
    };
}
