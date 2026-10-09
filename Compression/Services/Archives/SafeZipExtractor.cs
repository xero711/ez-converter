using System.Buffers.Binary;
using System.Text;
using EZConverter.Compression.Models;
using EZConverter.Compression.Services.Compression;
using EZConverter.Compression.Services.Security;
using ICSharpCode.SharpZipLib.Zip;

namespace EZConverter.Compression.Services.Archives;

internal static class SafeZipExtractor
{
    public static async Task<ArchiveOperationResult> ExtractAsync(ArchiveExtractRequest request,
        GpuCompressionBackend gpu, IProgress<ArchiveProgress> progress, IProgress<string> log, CancellationToken ct)
    {
        var started = DateTimeOffset.Now;
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        using var zip = new ZipFile(request.ArchivePath, StringCodec.FromCodePage(932));
        if (zip.Count > ArchiveSafetyLimits.MaxEntries) throw new InvalidDataException("ZIPの件数が安全上限を超えています。");
        var entries = zip.Cast<ZipEntry>().ToArray();
        ArchivePathGuard.ValidateNames(entries.Select(e => (e.Name, e.IsDirectory)), request.DestinationDirectory);
        long total = 0;
        foreach (var entry in entries)
        {
            ct.ThrowIfCancellationRequested();
            int unixType = (entry.ExternalFileAttributes >> 16) & 0xf000;
            if ((unixType != 0 && unixType != 0x8000 && unixType != 0x4000) || (entry.ExternalFileAttributes & 0x400) != 0)
                throw new ArchiveSecurityException("リンクや特殊ファイルを含むZIPは展開できません。");
            if (entry.IsCrypted) throw new NotSupportedException("暗号化ZIPはこの機能では展開できません。");
            if (entry.CompressionMethod is not CompressionMethod.Stored and not CompressionMethod.Deflated)
                throw new NotSupportedException("このZIPの圧縮方式は未対応です。通常の変換機能を使用してください。");
            if (entry.Size < 0 || entry.CompressedSize < 0 || entry.Size > ArchiveSafetyLimits.MaxFileBytes)
                throw new InvalidDataException("ZIPの復元サイズが安全上限を超えています。");
            if (entry.IsDirectory && entry.Size != 0) throw new InvalidDataException("ZIPフォルダのサイズが不正です。");
            total = checked(total + entry.Size);
            if (total > ArchiveSafetyLimits.MaxTotalBytes) throw new InvalidDataException("ZIPの合計展開容量が安全上限を超えています。");
        }
        var volume = new DriveInfo(Path.GetPathRoot(Path.GetFullPath(request.DestinationDirectory))!);
        if (total > Math.Max(0, volume.AvailableFreeSpace - 64L * 1024 * 1024))
            throw new IOException("展開先の空き容量が不足しています。");
        var files = entries.Where(e => !e.IsDirectory).ToArray();
        Directory.CreateDirectory(request.DestinationDirectory);
        var validator = new PathSafetyValidator();
        foreach (var entry in entries.Where(e => e.IsDirectory)) Directory.CreateDirectory(GetDestination(entry));
        long processed = 0;
        int count = 0;
        bool usedGpu = false;
        string fallback = request.FallbackReason;
        bool useGpu = request.GpuMode == GpuMode.GpuPreferred || (request.GpuMode == GpuMode.Auto && total >= 8 * 1024 * 1024);
        await using var rawArchive = File.OpenRead(request.ArchivePath);
        for (int index = 0; index < files.Length;)
        {
            ct.ThrowIfCancellationRequested();
            if (useGpu && IsGpuCandidate(files[index]))
            {
                var batch = new List<ZipEntry>();
                var compressed = new List<ReadOnlyMemory<byte>>();
                while (index < files.Length && batch.Count < 256 && IsGpuCandidate(files[index]))
                {
                    var entry = files[index++];
                    batch.Add(entry);
                    compressed.Add(await ReadRawAsync(rawArchive, entry, ct).ConfigureAwait(false));
                }
                var result = gpu.DecodeBatch(compressed, batch.Select(e => checked((int)e.Size)).ToArray(), true, ct);
                usedGpu |= result.UsedGpu;
                if (result.Reason.Length > 0) { fallback = result.Reason; log.Report(fallback); }
                for (int i = 0; i < batch.Count; i++)
                {
                    var crc = new Crc32(); crc.Update(result.Bytes[i].Span);
                    CheckCrc(batch[i], crc);
                    var path = GetDestination(batch[i]);
                    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                    await File.WriteAllBytesAsync(path, result.Bytes[i].ToArray(), ct).ConfigureAwait(false);
                    Finish(batch[i]);
                }
            }
            else
            {
                var entry = files[index++];
                var path = GetDestination(entry);
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                using var input = zip.GetInputStream(entry);
                await using (var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    var buffer = new byte[1024 * 1024];
                    long written = 0;
                    var crc = new Crc32();
                    while (true)
                    {
                        ct.ThrowIfCancellationRequested();
                        int read = await input.ReadAsync(buffer, ct).ConfigureAwait(false);
                        if (read == 0) break;
                        if (read > entry.Size - written) throw new InvalidDataException("ZIPが申告サイズを超えて展開されました。");
                        crc.Update(buffer.AsSpan(0, read));
                        await output.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                        written += read;
                        progress.Report(new(total, processed + written, files.Length, count, entry.Name, "CPUで展開中"));
                    }
                    if (written != entry.Size) throw new InvalidDataException("ZIPの復元サイズが一致しません。");
                    CheckCrc(entry, crc);
                }
                Finish(entry);
            }
        }
        return new(started, DateTimeOffset.Now, request.ArchivePath, request.DestinationDirectory, "ZIP",
            usedGpu ? "GPU（検証付き）" : "CPU", files.Length, rawArchive.Length, total,
            request.UsedFallback || fallback.Length > 0, fallback);

        string GetDestination(ZipEntry entry)
        {
            if (!validator.TryGetSafeDestinationPath(request.DestinationDirectory, entry.Name, out var path, out var reason))
                throw new ArchiveSecurityException(reason);
            ArchivePathGuard.RejectLinks(path);
            return path;
        }
        void Finish(ZipEntry entry)
        {
            try { File.SetLastWriteTime(GetDestination(entry), entry.DateTime); } catch (IOException) { }
            processed += entry.Size; count++;
            progress.Report(new(total, processed, files.Length, count, entry.Name, "展開中"));
        }
    }

    private static bool IsGpuCandidate(ZipEntry e) => e.Size is > 0 and <= 65536 && e.CompressedSize is > 0 and <= 131072 && e.CompressionMethod == CompressionMethod.Deflated;
    private static void CheckCrc(ZipEntry entry, Crc32 crc)
    {
        if (crc.GetCurrentHash() != (uint)entry.Crc) throw new InvalidDataException($"ZIPのCRC32が一致しません: {entry.Name}");
    }
    private static async Task<ReadOnlyMemory<byte>> ReadRawAsync(FileStream stream, ZipEntry entry, CancellationToken ct)
    {
        if (entry.Offset < 0 || entry.Offset > stream.Length - 30) throw new InvalidDataException("ZIPのオフセットが不正です。");
        stream.Position = entry.Offset;
        var header = new byte[30];
        await stream.ReadExactlyAsync(header, ct).ConfigureAwait(false);
        if (BinaryPrimitives.ReadUInt32LittleEndian(header) != 0x04034b50 || BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(8)) != 8 || (BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(6)) & 1) != 0)
            throw new InvalidDataException("ZIPのローカルヘッダーが一致しません。");
        long offset = checked(stream.Position + BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(26)) + BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(28)));
        if (offset > stream.Length || entry.CompressedSize > stream.Length - offset) throw new InvalidDataException("ZIPのデータが途中で切れています。");
        stream.Position = offset;
        var bytes = new byte[checked((int)entry.CompressedSize)];
        await stream.ReadExactlyAsync(bytes, ct).ConfigureAwait(false);
        return bytes;
    }
}
