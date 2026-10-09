using ICSharpCode.SharpZipLib.Zip.Compression;

namespace EZConverter.Compression.Services.Compression;

internal static class VerifiedDeflate
{
    // Validate complete raw DEFLATE grammar and bound the output before any GPU decode.
    public static byte[] Decode(ReadOnlyMemory<byte> compressed, int expected, CancellationToken ct)
    {
        if (expected < 0 || expected > 1024 * 1024 || compressed.Length > 2 * 1024 * 1024)
            throw new InvalidDataException("チャンクが安全上限を超えています。");
        var inflater = new Inflater(noHeader: true);
        inflater.SetInput(compressed.ToArray());
        var result = new byte[expected];
        int position = 0;
        while (!inflater.IsFinished && position < expected)
        {
            ct.ThrowIfCancellationRequested();
            int read = inflater.Inflate(result, position, expected - position);
            if (read == 0 && !inflater.IsFinished)
                throw new InvalidDataException("DEFLATEデータが破損、または途中で切れています。");
            position += read;
        }
        var extra = new byte[1];
        if (inflater.Inflate(extra) != 0 || position != expected || !inflater.IsFinished || inflater.RemainingInput != 0)
            throw new InvalidDataException("DEFLATEの終了位置または復元サイズが一致しません。");
        return result;
    }
}
