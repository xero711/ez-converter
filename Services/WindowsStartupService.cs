using System.IO;
using Microsoft.Win32;

namespace MediaConverter.Services;

public readonly record struct WindowsStartupOptions(bool Enabled, bool StartMinimized);

public static class WindowsStartupService
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "EZConverter";

    public static WindowsStartupOptions GetCurrent()
    {
        try
        {
            using var runKey = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: false);
            var command = runKey?.GetValue(ValueName) as string;
            if (string.IsNullOrWhiteSpace(command)
                || !TryParseCommand(command, out _, out var arguments))
            {
                return default;
            }

            var startMinimized = arguments
                .Split(' ', StringSplitOptions.RemoveEmptyEntries)
                .Any(argument => string.Equals(argument, "--minimized", StringComparison.OrdinalIgnoreCase));
            return new WindowsStartupOptions(true, startMinimized);
        }
        catch
        {
            return default;
        }
    }

    public static void Set(bool enabled, bool startMinimized)
    {
        using var runKey = Registry.CurrentUser.CreateSubKey(RunKeyPath, writable: true)
            ?? throw new InvalidOperationException("Windowsのスタートアップ設定を開けませんでした。");

        if (!enabled)
        {
            runKey.DeleteValue(ValueName, throwOnMissingValue: false);
            return;
        }

        var executablePath = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(executablePath) || !File.Exists(executablePath))
        {
            throw new InvalidOperationException("起動中のアプリの実行ファイルを特定できません。");
        }

        var command = $"\"{Path.GetFullPath(executablePath)}\"" + (startMinimized ? " --minimized" : string.Empty);
        runKey.SetValue(ValueName, command, RegistryValueKind.String);
    }

    private static bool TryParseCommand(string command, out string executablePath, out string arguments)
    {
        executablePath = string.Empty;
        arguments = string.Empty;

        if (command.StartsWith('"'))
        {
            var closingQuote = command.IndexOf('"', 1);
            if (closingQuote <= 1)
            {
                return false;
            }

            executablePath = command[1..closingQuote];
            arguments = command[(closingQuote + 1)..].Trim();
            return true;
        }

        var separator = command.IndexOf(' ');
        executablePath = separator < 0 ? command : command[..separator];
        arguments = separator < 0 ? string.Empty : command[(separator + 1)..].Trim();
        return executablePath.Length > 0;
    }
}
