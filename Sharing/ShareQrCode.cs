using QRCoder;

namespace EZConverter.Sharing;

public static class ShareQrCode
{
    public static byte[] CreatePng(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https") ||
            uri.UserInfo.Length != 0 || url.Length > 2048)
            throw new ArgumentException("QRコードにできる共有URLではありません。", nameof(url));

        using var data = QRCodeGenerator.GenerateQrCode(url, QRCodeGenerator.ECCLevel.Q);
        using var renderer = new PngByteQRCode(data);
        return renderer.GetGraphic(8);
    }
}
