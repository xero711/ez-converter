namespace MediaConverter.Services;

public sealed class DependencyChecker
{
    private static readonly TimeSpan ToolProbeTimeout = TimeSpan.FromSeconds(15);

    public async Task<IReadOnlyList<LocatedTool>> CheckAsync(CancellationToken cancellationToken = default)
    {
        var checks = new[]
        {
            CheckToolAsync("ImageMagick", ToolLocator.FindImageMagick(), ["-version"], cancellationToken),
            CheckToolAsync("FFmpeg", ToolLocator.FindFfmpeg(), ["-version"], cancellationToken),
            CheckToolAsync("LibreOffice", ToolLocator.FindLibreOffice(), ["--version"], cancellationToken),
            CheckToolAsync("7-Zip", ToolLocator.FindSevenZip(), ["i"], cancellationToken),
            CheckToolAsync("Calibre", ToolLocator.FindCalibre(), ["--version"], cancellationToken),
            CheckToolAsync("FontForge", ToolLocator.FindFontForge(), ["--version"], cancellationToken),
            CheckToolAsync("yt-dlp", ToolLocator.FindYtDlp(), ["--version"], cancellationToken)
        };
        return await Task.WhenAll(checks);
    }

    private static async Task<LocatedTool> CheckToolAsync(
        string name,
        string? path,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken)
    {
        if (path is null)
        {
            var installHint = name switch
            {
                "ImageMagick" => "アプリ同梱のTools\\ImageMagick、ユーザー用Toolsフォルダー、またはImageMagickのインストール先を確認してください。",
                "FFmpeg" => "アプリ同梱のTools\\FFmpeg、ユーザー用Toolsフォルダー、またはFFmpegのインストール先を確認してください。",
                "LibreOffice" => "アプリ同梱のTools\\LibreOffice、ユーザー用Toolsフォルダー、またはLibreOfficeのインストール先を確認してください。",
                "7-Zip" => "アプリ同梱のTools\\7-Zip、ユーザー用Toolsフォルダー、または7-Zipのインストール先を確認してください。",
                "Calibre" => "アプリ同梱のTools\\Calibre、ユーザー用Toolsフォルダー、またはCalibreのインストール先を確認してください。",
                "FontForge" => "アプリ同梱のTools\\FontForge、ユーザー用Toolsフォルダー、またはFontForgeのインストール先を確認してください。",
                "yt-dlp" => "yt-dlpはアプリが公式Nightly版を自動取得・更新します。インターネット接続を確認してください。",
                _ => "必要な外部ツールをインストールしてください。"
            };
            return new LocatedTool(name, null, null, $"未検出。{installHint}");
        }

        try
        {
            using var timeoutCancellation = new CancellationTokenSource(ToolProbeTimeout);
            using var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken, timeoutCancellation.Token);
            var result = await ExternalToolRunner.RunAsync(path, arguments, linkedCancellation.Token);
            if (result.ExitCode != 0)
            {
                return new LocatedTool(name, path, null, $"実行確認に失敗しました (終了コード {result.ExitCode})。{TrimOutput(result.StandardError)}");
            }

            var combined = string.IsNullOrWhiteSpace(result.StandardOutput) ? result.StandardError : result.StandardOutput;
            var firstLine = combined
                .Split(["\r\n", "\n"], StringSplitOptions.RemoveEmptyEntries)
                .FirstOrDefault() ?? "バージョン不明";
            return new LocatedTool(name, path, firstLine.Trim(), path);
        }
        catch (OperationCanceledException)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                throw;
            }

            return new LocatedTool(name, path, null, $"起動確認が{ToolProbeTimeout.TotalSeconds:0}秒以内に終了しませんでした。");
        }
        catch (Exception exception)
        {
            return new LocatedTool(name, path, null, $"実行確認に失敗しました: {exception.Message}");
        }
    }

    private static string TrimOutput(string output)
    {
        var text = output.Trim();
        return text.Length > 180 ? text[..180] + "…" : text;
    }
}
