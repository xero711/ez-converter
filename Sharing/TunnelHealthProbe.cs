namespace EZConverter.Sharing;

internal static class TunnelHealthProbe
{
    public static Uri CreateUri(Uri publicOrigin) => new(publicOrigin, "/health");
}
