using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace MediaConverter.Services;

public static class ExplorerContextMenuService
{
    private const string CompressVerbPath = @"Software\Classes\AllFilesystemObjects\shell\EZConverter.GpuCompress";
    private const string ZipExtractVerbPath = @"Software\Classes\SystemFileAssociations\.zip\shell\EZConverter.GpuExtract";
    private const string ZiperExtractVerbPath = @"Software\Classes\SystemFileAssociations\.ziper\shell\EZConverter.GpuExtract";
    private const uint ShellAssociationChanged = 0x08000000;

    public static void SetEnabled(bool enabled)
    {
        if (enabled)
        {
            var launchPrefix = GetLaunchPrefix();
            RegisterVerb(CompressVerbPath, "EZ ConverterでGPU圧縮", launchPrefix, "--archive-compress", "Single");
            RegisterVerb(ZipExtractVerbPath, "EZ ConverterでGPU解凍", launchPrefix, "--archive-extract", "Single");
            RegisterVerb(ZiperExtractVerbPath, "EZ ConverterでGPU解凍", launchPrefix, "--archive-extract", "Single");
        }
        else
        {
            RemoveVerb(CompressVerbPath);
            RemoveVerb(ZipExtractVerbPath);
            RemoveVerb(ZiperExtractVerbPath);
        }

        SHChangeNotify(ShellAssociationChanged, 0, IntPtr.Zero, IntPtr.Zero);
    }

    private static string GetLaunchPrefix()
    {
        var executablePath = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(executablePath) || !File.Exists(executablePath))
        {
            throw new InvalidOperationException("EZ Converterの起動ファイルを特定できませんでした。");
        }

        var prefix = Quote(Path.GetFullPath(executablePath));
        if (!string.Equals(Path.GetFileNameWithoutExtension(executablePath), "dotnet", StringComparison.OrdinalIgnoreCase))
        {
            return prefix;
        }

        var entryAssemblyName = Assembly.GetEntryAssembly()?.GetName().Name;
        var entryAssemblyPath = string.IsNullOrWhiteSpace(entryAssemblyName)
            ? null
            : Path.Combine(AppContext.BaseDirectory, $"{entryAssemblyName}.dll");
        return !string.IsNullOrWhiteSpace(entryAssemblyPath) && File.Exists(entryAssemblyPath)
            ? $"{prefix} {Quote(Path.GetFullPath(entryAssemblyPath))}"
            : prefix;
    }

    private static void RegisterVerb(string keyPath, string label, string launchPrefix, string operation, string multiSelectModel)
    {
        using var verb = Registry.CurrentUser.CreateSubKey(keyPath, writable: true)
            ?? throw new InvalidOperationException("エクスプローラーの右クリック設定を開けませんでした。");
        verb.SetValue(null, label, RegistryValueKind.String);
        verb.SetValue("MultiSelectModel", multiSelectModel, RegistryValueKind.String);
        var iconPath = Environment.ProcessPath ?? string.Empty;
        verb.SetValue("Icon", string.IsNullOrWhiteSpace(iconPath) ? string.Empty : $"\"{iconPath}\",0", RegistryValueKind.String);

        using var command = verb.CreateSubKey("command", writable: true)
            ?? throw new InvalidOperationException("右クリック操作を登録できませんでした。");
        command.SetValue(null, $"{launchPrefix} {operation} \"%1\"", RegistryValueKind.String);
    }

    private static void RemoveVerb(string keyPath) => Registry.CurrentUser.DeleteSubKeyTree(keyPath, throwOnMissingSubKey: false);

    private static string Quote(string value) => $"\"{value.Replace("\"", "\\\"", StringComparison.Ordinal)}\"";

    [DllImport("shell32.dll", EntryPoint = "SHChangeNotify")]
    private static extern void SHChangeNotify(uint eventId, uint flags, IntPtr item1, IntPtr item2);
}
