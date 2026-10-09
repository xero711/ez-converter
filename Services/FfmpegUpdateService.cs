using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.RegularExpressions;

namespace MediaConverter.Services;

public sealed record FfmpegUpdateResult(bool Updated, bool Skipped, string Version, string Detail);

/// <summary>
/// Downloads the FFmpeg essentials build from the Windows build provider linked
/// by FFmpeg, verifies its published SHA-256, then installs it per-user.
/// </summary>
public sealed class FfmpegUpdateService
{
    private const string BuildsBaseUrl = "https://www.gyan.dev/ffmpeg/builds/";
    private const string ArchiveName = "ffmpeg-release-essentials.zip";
    private const string VersionFileName = "release-version";
    private const string HashFileName = ArchiveName + ".sha256";
    private static readonly TimeSpan CheckInterval = TimeSpan.FromHours(12);
    private static readonly HttpClient MetadataClient = new() { Timeout = TimeSpan.FromSeconds(20) };
    private static readonly HttpClient DownloadClient = new() { Timeout = TimeSpan.FromMinutes(5) };
    private static readonly SemaphoreSlim UpdateLock = new(1, 1);

    private readonly string _toolsDirectory;
    private readonly string _installDirectory;
    private readonly string _stateFilePath;

    public FfmpegUpdateService(string? toolsDirectoryOverride = null)
    {
        _toolsDirectory = toolsDirectoryOverride ?? ToolLocator.ManagedToolsRoot;
        _installDirectory = Path.Combine(_toolsDirectory, "ffmpeg");
        _stateFilePath = Path.Combine(_installDirectory, "update-state.json");
    }

    public string ExecutablePath => Path.Combine(_installDirectory, "bin", "ffmpeg.exe");

    public async Task<FfmpegUpdateResult> UpdateIfNeededAsync(
        bool force,
        CancellationToken cancellationToken = default)
    {
        await UpdateLock.WaitAsync(cancellationToken);
        try
        {
            Directory.CreateDirectory(_toolsDirectory);
            var previousState = await ReadStateAsync(cancellationToken);
            if (!force && File.Exists(ExecutablePath) && previousState is not null &&
                DateTimeOffset.UtcNow - previousState.CheckedAtUtc < CheckInterval)
            {
                return new FfmpegUpdateResult(
                    Updated: false,
                    Skipped: true,
                    previousState.Version,
                    $"前回の確認から12時間以内のためFFmpeg確認を省略しました ({previousState.Version})。");
            }

            var version = (await MetadataClient.GetStringAsync(BuildsBaseUrl + VersionFileName, cancellationToken)).Trim();
            if (!Regex.IsMatch(version, @"^\d+(?:\.\d+){1,3}$", RegexOptions.CultureInvariant))
            {
                throw new InvalidDataException("FFmpeg配布元から有効なバージョン情報を取得できませんでした。");
            }

            var checksumText = await MetadataClient.GetStringAsync(BuildsBaseUrl + HashFileName, cancellationToken);
            var checksumMatch = Regex.Match(
                checksumText.Trim(),
                @"\A([0-9a-f]{64})(?:\s+.*)?\z",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            if (!checksumMatch.Success)
            {
                throw new InvalidDataException("FFmpeg配布元のSHA-256情報を読み取れませんでした。");
            }

            var expectedHash = checksumMatch.Groups[1].Value;
            if (File.Exists(ExecutablePath) && previousState?.Version == version)
            {
                await WriteStateAsync(new UpdateState(DateTimeOffset.UtcNow, version), cancellationToken);
                return new FfmpegUpdateResult(false, false, version, $"最新のFFmpegです ({version})。");
            }

            var archivePath = Path.Combine(_toolsDirectory, $"ffmpeg-{Guid.NewGuid():N}.download");
            var stagingDirectory = Path.Combine(_toolsDirectory, $"ffmpeg-staging-{Guid.NewGuid():N}");
            try
            {
                using (var response = await DownloadClient.GetAsync(
                    BuildsBaseUrl + ArchiveName, HttpCompletionOption.ResponseHeadersRead, cancellationToken))
                {
                    response.EnsureSuccessStatusCode();
                    if (response.Content.Headers.ContentLength is long length && length is < 1_000_000 or > 500_000_000)
                    {
                        throw new InvalidDataException("FFmpegアーカイブのサイズが想定範囲外です。");
                    }

                    await using var destination = new FileStream(
                        archivePath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 128 * 1024, useAsync: true);
                    await response.Content.CopyToAsync(destination, cancellationToken);
                }

                var archiveInfo = new FileInfo(archivePath);
                if (archiveInfo.Length is < 1_000_000 or > 500_000_000)
                {
                    throw new InvalidDataException("ダウンロードしたFFmpegアーカイブのサイズが不正です。");
                }

                var actualHash = await GetSha256Async(archivePath, cancellationToken);
                if (!string.Equals(actualHash, expectedHash, StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidDataException("FFmpegのSHA-256検証に失敗しました。現在のファイルは変更していません。");
                }

                var stagingBinDirectory = Path.Combine(stagingDirectory, "bin");
                Directory.CreateDirectory(stagingBinDirectory);
                await ExtractExecutableAsync(archivePath, stagingBinDirectory, "ffmpeg.exe", cancellationToken);
                await ExtractExecutableAsync(archivePath, stagingBinDirectory, "ffprobe.exe", cancellationToken);

                var versionCheck = await ExternalToolRunner.RunAsync(
                    Path.Combine(stagingBinDirectory, "ffmpeg.exe"), ["-version"], cancellationToken);
                if (versionCheck.ExitCode != 0 || !versionCheck.StandardOutput.Contains(version, StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidDataException("検証後のFFmpeg実行ファイルが配布バージョンと一致しません。");
                }

                ReplaceInstallDirectory(stagingDirectory);
                await WriteStateAsync(new UpdateState(DateTimeOffset.UtcNow, version), cancellationToken);
                return new FfmpegUpdateResult(true, false, version, $"FFmpegを更新しました ({version})。");
            }
            finally
            {
                TryDeleteFile(archivePath);
                TryDeleteDirectory(stagingDirectory);
            }
        }
        finally
        {
            UpdateLock.Release();
        }
    }

    private static async Task ExtractExecutableAsync(
        string archivePath,
        string destinationDirectory,
        string fileName,
        CancellationToken cancellationToken)
    {
        using var archive = ZipFile.OpenRead(archivePath);
        var matchingEntries = archive.Entries
            .Where(entry => entry.FullName.Replace('\\', '/').EndsWith($"/bin/{fileName}", StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (matchingEntries.Count != 1)
        {
            throw new InvalidDataException($"FFmpegアーカイブから{fileName}を一意に特定できませんでした。");
        }

        var entry = matchingEntries[0];
        if (entry.Length is < 1_000_000 or > 200_000_000)
        {
            throw new InvalidDataException($"{fileName}のサイズが不正です。");
        }

        var destinationPath = Path.Combine(destinationDirectory, fileName);
        await using (var source = entry.Open())
        await using (var destination = new FileStream(
            destinationPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 128 * 1024, useAsync: true))
        {
            await source.CopyToAsync(destination, cancellationToken);
        }

        await using var executable = File.OpenRead(destinationPath);
        var signature = new byte[2];
        var read = await executable.ReadAsync(signature, cancellationToken);
        if (read != 2 || signature[0] != (byte)'M' || signature[1] != (byte)'Z')
        {
            throw new InvalidDataException($"{fileName}がWindows実行ファイルではありません。");
        }
    }

    private void ReplaceInstallDirectory(string stagingDirectory)
    {
        var backupDirectory = Path.Combine(_toolsDirectory, $"ffmpeg-backup-{Guid.NewGuid():N}");
        var movedOldDirectory = false;
        try
        {
            if (Directory.Exists(_installDirectory))
            {
                Directory.Move(_installDirectory, backupDirectory);
                movedOldDirectory = true;
            }

            Directory.Move(stagingDirectory, _installDirectory);
            if (movedOldDirectory)
            {
                TryDeleteDirectory(backupDirectory);
            }
        }
        catch
        {
            if (!Directory.Exists(_installDirectory) && movedOldDirectory && Directory.Exists(backupDirectory))
            {
                Directory.Move(backupDirectory, _installDirectory);
            }

            throw;
        }
    }

    private async Task<UpdateState?> ReadStateAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(_stateFilePath))
        {
            return null;
        }

        try
        {
            await using var stream = File.OpenRead(_stateFilePath);
            return await System.Text.Json.JsonSerializer.DeserializeAsync<UpdateState>(
                stream, cancellationToken: cancellationToken);
        }
        catch (System.Text.Json.JsonException)
        {
            return null;
        }
        catch (IOException)
        {
            return null;
        }
    }

    private async Task WriteStateAsync(UpdateState state, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(_installDirectory);
        var temporaryPath = _stateFilePath + ".tmp";
        await using (var stream = new FileStream(
            temporaryPath, FileMode.Create, FileAccess.Write, FileShare.None, 4096, useAsync: true))
        {
            await System.Text.Json.JsonSerializer.SerializeAsync(stream, state, cancellationToken: cancellationToken);
        }

        File.Move(temporaryPath, _stateFilePath, overwrite: true);
    }

    private static async Task<string> GetSha256Async(string path, CancellationToken cancellationToken)
    {
        await using var stream = File.OpenRead(path);
        var hash = await SHA256.HashDataAsync(stream, cancellationToken);
        return Convert.ToHexString(hash);
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException)
        {
            // Retry cleanup on a later run if Windows still has a handle open.
        }
        catch (UnauthorizedAccessException)
        {
            // Preserve the installed version if a temporary file is locked.
        }
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
        catch (IOException)
        {
            // Retry cleanup on a later run if Windows still has a handle open.
        }
        catch (UnauthorizedAccessException)
        {
            // Preserve the installed version if a temporary file is locked.
        }
    }

    private sealed record UpdateState(DateTimeOffset CheckedAtUtc, string Version);
}
