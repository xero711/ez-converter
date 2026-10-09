using System.IO;
using System.ComponentModel;
using System.Diagnostics;
using System.Text;

namespace MediaConverter.Services;

public sealed record ToolRunResult(int ExitCode, string StandardOutput, string StandardError);

public static class ExternalToolRunner
{
    public static async Task<ToolRunResult> RunAsync(
        string executablePath,
        IEnumerable<string> arguments,
        CancellationToken cancellationToken,
        string? workingDirectory = null,
        Action<string>? standardOutputLine = null)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = executablePath,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = workingDirectory ?? AppContext.BaseDirectory
        };

        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
        try
        {
            if (!process.Start())
            {
                throw new InvalidOperationException($"外部ツールを起動できませんでした: {executablePath}");
            }

            var outputTask = ReadStandardOutputAsync(process.StandardOutput, standardOutputLine);
            var errorTask = process.StandardError.ReadToEndAsync();

            try
            {
                await process.WaitForExitAsync(cancellationToken);
            }
            catch (OperationCanceledException)
            {
                TryKill(process);
                throw;
            }

            await Task.WhenAll(outputTask, errorTask);
            return new ToolRunResult(process.ExitCode, outputTask.Result, errorTask.Result);
        }
        catch (Win32Exception exception)
        {
            throw new InvalidOperationException($"外部ツールを起動できませんでした: {executablePath}", exception);
        }
    }

    private static async Task<string> ReadStandardOutputAsync(StreamReader reader, Action<string>? lineObserver)
    {
        if (lineObserver is null)
        {
            return await reader.ReadToEndAsync();
        }

        var output = new StringBuilder();
        while (await reader.ReadLineAsync() is { } line)
        {
            output.AppendLine(line);
            try
            {
                lineObserver(line);
            }
            catch
            {
                // Progress observers must not interrupt an external tool.
            }
        }

        return output.ToString();
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (InvalidOperationException)
        {
            // The process exited between HasExited and Kill.
        }
        catch (System.ComponentModel.Win32Exception)
        {
            // Best-effort cancellation; the original cancellation is rethrown.
        }
    }
}
