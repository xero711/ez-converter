using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MediaConverter.Models;

namespace MediaConverter.Services;

public static class AppPreferencesStore
{
    private static readonly string PreferencesPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "EZConverter",
        "settings.json");

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true
    };

    public static AppPreferences Load() => LoadFromPath(PreferencesPath);

    internal static AppPreferences LoadFromPath(string preferencesPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(preferencesPath);
        preferencesPath = Path.GetFullPath(preferencesPath);
        try
        {
            if (!File.Exists(preferencesPath))
            {
                return new AppPreferences();
            }

            var preferences = JsonSerializer.Deserialize<AppPreferences>(File.ReadAllText(preferencesPath))
                ?? new AppPreferences();
            if (!string.IsNullOrWhiteSpace(preferences.ProtectedTurnSharedSecret))
            {
                try { preferences.TurnSharedSecret = DpapiStringProtector.Unprotect(preferences.ProtectedTurnSharedSecret); }
                catch (Exception e) when (e is FormatException or System.ComponentModel.Win32Exception or CryptographicException) { preferences.TurnSharedSecret = string.Empty; }
            }
            if (!string.IsNullOrWhiteSpace(preferences.ProtectedNamedTunnelToken))
            {
                try { preferences.NamedTunnelToken = DpapiStringProtector.Unprotect(preferences.ProtectedNamedTunnelToken); }
                catch (Exception e) when (e is FormatException or System.ComponentModel.Win32Exception or CryptographicException) { preferences.NamedTunnelToken = string.Empty; }
            }
            return preferences;
        }
        catch
        {
            return new AppPreferences();
        }
    }

    public static void Save(AppPreferences preferences) => SaveToPath(preferences, PreferencesPath);

    internal static void SaveToPath(AppPreferences preferences, string preferencesPath)
    {
        ArgumentNullException.ThrowIfNull(preferences);
        ArgumentException.ThrowIfNullOrWhiteSpace(preferencesPath);
        preferencesPath = Path.GetFullPath(preferencesPath);
        var directory = Path.GetDirectoryName(preferencesPath)
            ?? throw new InvalidOperationException("設定ファイルの保存先を決定できません。");
        Directory.CreateDirectory(directory);

        var temporaryPath = preferencesPath + ".tmp";
        var persisted = new AppPreferences
        {
            CheckForAppUpdatesOnStartup = preferences.CheckForAppUpdatesOnStartup,
            AutoUpdateMediaTools = preferences.AutoUpdateMediaTools,
            StartWithWindows = preferences.StartWithWindows,
            StartMinimized = preferences.StartMinimized,
            AddExplorerContextMenu = preferences.AddExplorerContextMenu,
            EnableTurnRelay = preferences.EnableTurnRelay,
            TurnServerUrl = preferences.TurnServerUrl,
            TurnSharedSecret = preferences.TurnSharedSecret,
            ProtectedTurnSharedSecret = string.IsNullOrEmpty(preferences.TurnSharedSecret)
                ? preferences.ProtectedTurnSharedSecret
                : DpapiStringProtector.Protect(preferences.TurnSharedSecret),
            UseNamedTunnel = preferences.UseNamedTunnel,
            NamedTunnelHostname = preferences.NamedTunnelHostname,
            NamedTunnelPort = preferences.NamedTunnelPort,
            NamedTunnelToken = preferences.NamedTunnelToken,
            ProtectedNamedTunnelToken = string.IsNullOrEmpty(preferences.NamedTunnelToken)
                ? preferences.ProtectedNamedTunnelToken
                : DpapiStringProtector.Protect(preferences.NamedTunnelToken)
        };
        File.WriteAllText(temporaryPath, JsonSerializer.Serialize(persisted, SerializerOptions), new UTF8Encoding(false));
        File.Move(temporaryPath, preferencesPath, overwrite: true);
    }
}
