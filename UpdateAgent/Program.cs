using System.Diagnostics;
using System.IO.Compression;

if (args.Any(argument => string.Equals(argument, "--help", StringComparison.OrdinalIgnoreCase)))
{
    Console.WriteLine("MediaConverter.UpdateAgent --parent-pid PID --archive ZIP --install-dir DIR --app EXE");
    return 0;
}

try
{
    var options = ParseArguments(args);
    await WaitForParentAsync(options.ParentProcessId);
    var stagingDirectory = Path.Combine(Path.GetTempPath(), "EZConverter", $"update-{Guid.NewGuid():N}");
    Directory.CreateDirectory(stagingDirectory);
    try
    {
        ExtractArchiveSafely(options.ArchivePath, stagingDirectory);
        var stagedAppPath = Directory.EnumerateFiles(stagingDirectory, "MediaConverter.exe", SearchOption.AllDirectories).SingleOrDefault()
            ?? throw new InvalidDataException("更新アーカイブにMediaConverter.exeがありません。");
        var stagedRoot = Path.GetDirectoryName(stagedAppPath)
            ?? throw new InvalidDataException("更新アーカイブの構成を確認できません。");
        var installDirectory = options.InstallDirectory;
        var backupDirectory = installDirectory + ".update-backup-" + DateTime.UtcNow.ToString("yyyyMMddHHmmss", System.Globalization.CultureInfo.InvariantCulture) + "-" + Guid.NewGuid().ToString("N");

        Directory.Move(installDirectory, backupDirectory);
        try
        {
            Directory.CreateDirectory(installDirectory);
            CopyDirectory(stagedRoot, installDirectory);
            if (!File.Exists(options.ApplicationPath))
            {
                throw new IOException("更新後のアプリケーション本体が見つかりません。");
            }

            var restarted = Process.Start(new ProcessStartInfo
            {
                FileName = options.ApplicationPath,
                WorkingDirectory = installDirectory,
                UseShellExecute = true
            });
            if (restarted is null)
            {
                throw new InvalidOperationException("更新後のアプリケーションを再起動できませんでした。");
            }

            TryDeleteFile(options.ArchivePath);
        }
        catch
        {
            TryDeleteDirectory(installDirectory);
            if (Directory.Exists(backupDirectory))
            {
                Directory.Move(backupDirectory, installDirectory);
            }

            throw;
        }
    }
    finally
    {
        TryDeleteDirectory(stagingDirectory);
    }

    return 0;
}
catch (Exception exception)
{
    Console.Error.WriteLine($"EZConverterの更新に失敗しました: {exception.Message}");
    return 1;
}

static UpdateArguments ParseArguments(string[] args)
{
    var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    for (var index = 0; index < args.Length; index++)
    {
        if (!args[index].StartsWith("--", StringComparison.Ordinal) || index + 1 >= args.Length)
        {
            throw new ArgumentException("更新引数が不正です。");
        }

        values[args[index]] = args[++index];
    }

    var parentPid = GetRequired(values, "--parent-pid");
    if (!int.TryParse(parentPid, out var processId) || processId <= 0)
    {
        throw new ArgumentException("親プロセスIDが不正です。");
    }

    var archive = Path.GetFullPath(GetRequired(values, "--archive"));
    var installDirectory = Path.GetFullPath(GetRequired(values, "--install-dir")).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    var applicationPath = Path.GetFullPath(GetRequired(values, "--app"));
    if (!File.Exists(archive) || !Directory.Exists(installDirectory))
    {
        throw new FileNotFoundException("更新に必要なファイルまたはインストール先が見つかりません。");
    }

    var installPrefix = installDirectory + Path.DirectorySeparatorChar;
    if (!applicationPath.StartsWith(installPrefix, StringComparison.OrdinalIgnoreCase))
    {
        throw new ArgumentException("アプリケーションパスがインストール先の外にあります。");
    }

    return new UpdateArguments(processId, archive, installDirectory, applicationPath);
}

static string GetRequired(IReadOnlyDictionary<string, string> values, string key)
{
    return values.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value)
        ? value
        : throw new ArgumentException($"更新引数がありません: {key}");
}

static async Task WaitForParentAsync(int processId)
{
    for (var attempt = 0; attempt < 1200; attempt++)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            if (process.HasExited)
            {
                return;
            }
        }
        catch (ArgumentException)
        {
            return;
        }

        await Task.Delay(250);
    }

    throw new TimeoutException("起動中のEZConverterが終了するまで待機できませんでした。");
}

static void ExtractArchiveSafely(string archivePath, string stagingDirectory)
{
    var fullStagingDirectory = Path.GetFullPath(stagingDirectory).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
    using var archive = ZipFile.OpenRead(archivePath);
    foreach (var entry in archive.Entries)
    {
        var destination = Path.GetFullPath(Path.Combine(stagingDirectory, entry.FullName));
        if (!destination.StartsWith(fullStagingDirectory, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("更新アーカイブに不正なパスが含まれています。");
        }

        if (string.IsNullOrEmpty(entry.Name))
        {
            Directory.CreateDirectory(destination);
            continue;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        entry.ExtractToFile(destination, overwrite: true);
    }
}

static void CopyDirectory(string sourceDirectory, string destinationDirectory)
{
    Directory.CreateDirectory(destinationDirectory);
    foreach (var directory in Directory.EnumerateDirectories(sourceDirectory))
    {
        CopyDirectory(directory, Path.Combine(destinationDirectory, Path.GetFileName(directory)));
    }

    foreach (var file in Directory.EnumerateFiles(sourceDirectory))
    {
        File.Copy(file, Path.Combine(destinationDirectory, Path.GetFileName(file)), overwrite: true);
    }
}

static void TryDeleteDirectory(string path)
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
    }
    catch (UnauthorizedAccessException)
    {
    }
}

static void TryDeleteFile(string path)
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
    }
    catch (UnauthorizedAccessException)
    {
    }
}

file sealed record UpdateArguments(int ParentProcessId, string ArchivePath, string InstallDirectory, string ApplicationPath);
