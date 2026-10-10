using System.IO;

namespace MediaConverter.Services;

public sealed record LocatedTool(string Name, string? Path, string? Version, string Detail)
{
    public bool IsAvailable => !string.IsNullOrWhiteSpace(Path) && !string.IsNullOrWhiteSpace(Version);
}

public static class ToolLocator
{
    public static string ManagedToolsRoot => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "EZConverter", "Tools");

    public static string? FindImageMagick() => FindExecutable(
        ["magick.exe"], ["ImageMagick\\magick.exe", "ImageMagick-*\\magick.exe"]);

    public static string? FindFfmpeg() => FindExecutable(
        ["ffmpeg.exe"], ["ffmpeg\\bin\\ffmpeg.exe", "FFmpeg\\bin\\ffmpeg.exe", "ffmpeg-*-essentials_build\\bin\\ffmpeg.exe"]);

    public static string? FindLibreOffice() => FindExecutable(
        ["soffice.com", "soffice.exe"], [
            "LibreOffice\\program\\soffice.com", "LibreOffice\\program\\soffice.exe",
            "LibreOffice-*\\program\\soffice.com", "LibreOffice-*\\program\\soffice.exe",
            "LibreOffice *\\program\\soffice.com", "LibreOffice *\\program\\soffice.exe"
        ]);

    public static string? FindSevenZip() => FindExecutable(
        ["7z.exe", "7zz.exe"], ["7-Zip\\7z.exe", "7-Zip\\7zz.exe"]);

    public static string? FindCalibre() => FindExecutable(
        ["ebook-convert.exe"], ["Calibre2\\ebook-convert.exe", "Calibre\\ebook-convert.exe", "Calibre\\Calibre Portable\\Calibre\\ebook-convert.exe"]);

    public static string? FindFontForge() => FindExecutable(
        ["fontforge.exe"], ["FontForgeBuilds\\bin\\fontforge.exe", "FontForge\\bin\\fontforge.exe", "FontForge-*\\bin\\fontforge.exe"]);

    public static string? FindYtDlp()
    {
        var managedPath = Path.Combine(ManagedToolsRoot, "yt-dlp", "yt-dlp.exe");
        return File.Exists(managedPath)
            ? managedPath
            : FindExecutable(["yt-dlp.exe"], ["yt-dlp\\yt-dlp.exe", "yt-dlp.exe"]);
    }

    public static string? FindDeno()
    {
        var managedPath = Path.Combine(ManagedToolsRoot, "deno", "deno.exe");
        if (File.Exists(managedPath))
        {
            return managedPath;
        }

        var userProfilePath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".deno", "bin", "deno.exe");
        return File.Exists(userProfilePath)
            ? userProfilePath
            : FindExecutable(["deno.exe"], ["deno\\deno.exe", "Deno\\deno.exe", "deno.exe"]);
    }

    private static string? FindExecutable(IReadOnlyList<string> fileNames, IReadOnlyList<string> relativePatterns)
    {
        var candidates = new List<string>();

        var managedRoots = new[]
        {
            Path.Combine(AppContext.BaseDirectory, "Tools"),
            ManagedToolsRoot
        }.Distinct(StringComparer.OrdinalIgnoreCase);
        foreach (var root in managedRoots.Where(Directory.Exists))
        {
            AddRelativeCandidates(candidates, root, relativePatterns);
        }

        var pathEntries = Environment.GetEnvironmentVariable("PATH")?.Split(Path.PathSeparator) ?? [];
        foreach (var entry in pathEntries.Where(entry => !string.IsNullOrWhiteSpace(entry)))
        {
            candidates.AddRange(fileNames.Select(fileName => Path.Combine(entry.Trim(), fileName)));
        }

        var roots = new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86)
        }.Where(Directory.Exists).Distinct(StringComparer.OrdinalIgnoreCase);

        foreach (var root in roots)
        {
            AddRelativeCandidates(candidates, root, relativePatterns);
        }

        foreach (var fileName in fileNames)
        {
            candidates.Add(Path.Combine(AppContext.BaseDirectory, fileName));
        }

        return candidates.Where(File.Exists).Select(Path.GetFullPath).Distinct(StringComparer.OrdinalIgnoreCase).FirstOrDefault();
    }

    private static void AddRelativeCandidates(List<string> candidates, string root, IReadOnlyList<string> relativePatterns)
    {
        foreach (var pattern in relativePatterns)
        {
            var separator = pattern.IndexOf('\\');
            var directoryPattern = separator >= 0 ? pattern[..separator] : pattern;
            var filePart = separator >= 0 ? pattern[(separator + 1)..] : pattern;
            try
            {
                if (directoryPattern.Contains('*'))
                {
                    candidates.AddRange(Directory.EnumerateDirectories(root, directoryPattern, SearchOption.TopDirectoryOnly)
                        .Select(directory => Path.Combine(directory, filePart)));
                }
                else
                {
                    candidates.Add(Path.Combine(root, pattern));
                }
            }
            catch (UnauthorizedAccessException)
            {
                // Optional installation directories may be inaccessible.
            }
            catch (DirectoryNotFoundException)
            {
                // An optional installation directory is not present.
            }
        }
    }
}
