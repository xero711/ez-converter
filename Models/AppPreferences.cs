using System.Text.Json.Serialization;

namespace MediaConverter.Models;

public sealed class AppPreferences
{
    public bool CheckForAppUpdatesOnStartup { get; set; } = true;
    public bool AutoUpdateMediaTools { get; set; } = true;
    public bool StartWithWindows { get; set; }
    public bool StartMinimized { get; set; }
    public bool AddExplorerContextMenu { get; set; } = true;
    public bool EnableTurnRelay { get; set; }
    public string TurnServerUrl { get; set; } = string.Empty;
    public bool UseNamedTunnel { get; set; }
    public string NamedTunnelHostname { get; set; } = string.Empty;
    public int NamedTunnelPort { get; set; } = 53318;

    [JsonIgnore]
    public string TurnSharedSecret { get; set; } = string.Empty;

    public string ProtectedTurnSharedSecret { get; set; } = string.Empty;

    [JsonIgnore]
    public string NamedTunnelToken { get; set; } = string.Empty;

    public string ProtectedNamedTunnelToken { get; set; } = string.Empty;
}
