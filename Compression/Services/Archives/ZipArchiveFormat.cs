using System.Buffers;
using System.IO;
using System.IO.Compression;
using System.Linq;
using EZConverter.Compression.Models;
using EZConverter.Compression.Services.Compression;
using EZConverter.Compression.Services.Security;

namespace EZConverter.Compression.Services.Archives;

public sealed class ZipArchiveFormat : IArchiveFormat
{
    private const int BufferSize = 1024 * 1024;
    private const long ParallelWriterThresholdBytes = 16L * 1024 * 1024;
    private const int ParallelSampleFileLimit = 2;
    private const int ParallelSampleBytesPerFile = 64 * 1024;
    private const double ParallelSampleMinimumCompressedRatio = 0.35;
    private readonly PathSafetyValidator _pathSafetyValidator = new();

    public string Name => "ZIP";

    public IReadOnlyCollection<string> Extensions { get; } = new[] { ".zip" };

    public bool CanRead(string path) =>
        string.Equals(Path.GetExtension(path), ".zip", StringComparison.OrdinalIgnoreCase);

    public bool CanWrite(string path) => CanRead(path);

    public async Task<ArchiveOperationResult> CreateArchiveAsync(
        ArchiveCreateRequest request,
        ICompressionBackend backend,
        IProgress<ArchiveProgress> progress,
        IProgress<string> log,
        CancellationToken cancellationToken)
    {
        var startedAt = DateTimeOffset.Now;
        var outputPath = Path.GetFullPath(request.OutputArchivePath);
        var outputDirectory = Path.GetDirectoryName(outputPath);

        if (string.IsNullOrWhiteSpace(outputDirectory))
        {
            throw new InvalidOperationException("出力先フォルダを特定できません。");
        }

        Directory.CreateDirectory(outputDirectory);

        if (File.Exists(outputPath))
        {
            if (!request.OverwriteExisting)
            {
                throw new IOException("同名のZIPファイルが既に存在します。");
            }

            File.Delete(outputPath);
        }

        log.Report($"開始時刻: {startedAt:yyyy-MM-dd HH:mm:ss}");
        log.Report($"入力パス: {string.Join("; ", request.SourceManifest.InputPaths)}");
        log.Report($"出力パス: {outputPath}");
        log.Report("選択された形式: ZIP");
        log.Report($"圧縮レベル: {request.CompressionLevel}");
        log.Report($"GPUモード: {request.GpuMode}");
        log.Report($"使用バックエンド: {backend.Name}");
        log.Report($"処理モード: {SafeEngineLabel(request.SelectedEngine, backend.Name)}");
        log.Report($"フォールバック理由: {SafeFallbackReason(request.FallbackReason)}");
        log.Report($"処理対象ファイル数: {request.SourceManifest.TotalFiles:N0}");
        log.Report($"合計サイズ: {FormatBytes(request.SourceManifest.TotalBytes)}");

        progress.Report(new ArchiveProgress(
            request.SourceManifest.TotalBytes,
            0,
            request.SourceManifest.TotalFiles,
            0,
            string.Empty,
            "ZIP作成を開始しています。"));

        var processedBytes = 0L;
        var processedFiles = 0;
        var usedGpuWriter = IsGpuZipBackend(backend);
        var parallelDecision = usedGpuWriter
            ? new ParallelWriterDecision(true, "GPU backend がZIP互換DEFLATEを提供しているため GPU ZIP writer を使用します。")
            : EvaluateParallelWriter(request, backend);
        var usedParallelWriter = parallelDecision.UseParallelWriter;
        var runtimeFallback = false;
        var runtimeFallbackReason = string.Empty;

        try
        {
            if (usedParallelWriter)
            {
                log.Report($"高速化判定: {parallelDecision.Reason}");
                var writer = new ParallelZipArchiveWriter();
                try
                {
                    var writerResult = await writer.CreateAsync(
                        request,
                        outputPath,
                        usedGpuWriter ? backend : null,
                        progress,
                        log,
                        cancellationToken).ConfigureAwait(false);
                    if (usedGpuWriter && writerResult.UsedCpuFallback)
                    {
                        runtimeFallback = true;
                        runtimeFallbackReason = string.IsNullOrWhiteSpace(writerResult.FallbackReason)
                            ? "GPU batchがCPUへフォールバックしました。"
                            : writerResult.FallbackReason;
                        usedGpuWriter = writerResult.UsedBatchBackend;
                    }
                }
                catch (Exception ex) when (usedGpuWriter && ex is not OperationCanceledException)
                {
                    runtimeFallback = true;
                    runtimeFallbackReason = $"GPU処理中に失敗したためCPUへフォールバックしました: {ex.Message}";
                    log.Report(runtimeFallbackReason);
                    var fallbackWriter = new ParallelZipArchiveWriter();
                    await fallbackWriter.CreateAsync(request, outputPath, null, progress, log, cancellationToken).ConfigureAwait(false);
                    usedGpuWriter = false;
                }
            }
            else
            {
                if (request.SourceManifest.TotalBytes >= ParallelWriterThresholdBytes &&
                    request.SourceManifest.Files.Count <= 1)
                {
                    log.Report("高速化: 単一ファイルはZIP互換性維持のため従来の逐次DEFLATEを使用します。");
                }
                else if (!string.IsNullOrWhiteSpace(parallelDecision.Reason))
                {
                    log.Report($"高速化判定: {parallelDecision.Reason}");
                }

                await using var archiveStream = new FileStream(
                    outputPath,
                    FileMode.CreateNew,
                    FileAccess.ReadWrite,
                    FileShare.None,
                    BufferSize,
                    FileOptions.Asynchronous | FileOptions.SequentialScan);

                using var archive = new ZipArchive(
                    archiveStream,
                    ZipArchiveMode.Create,
                    leaveOpen: false,
                    entryNameEncoding: ZipEntryNameEncoding.Utf8);
                var compressionLevel = CompressionLevelMapper.ToSystemCompressionLevel(request.CompressionLevel);
                var useFrameworkFileWriter = parallelDecision.UseFrameworkFileWriter;
                if (useFrameworkFileWriter)
                {
                    log.Report("高速化: 小規模または高圧縮率の入力のため .NET optimized file writer を使用します。");
                }

                foreach (var directoryEntry in request.SourceManifest.DirectoryEntries)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (!directoryEntry.EndsWith("/", StringComparison.Ordinal))
                    {
                        continue;
                    }

                    archive.CreateEntry(directoryEntry, CompressionLevel.NoCompression);
                }

                foreach (var sourceFile in request.SourceManifest.Files)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var entryCompressionLevel = ZipCompressionMethodSelector.ShouldStoreForSpeed(sourceFile, request.CompressionLevel)
                        ? CompressionLevel.NoCompression
                        : compressionLevel;
                    if (entryCompressionLevel == CompressionLevel.NoCompression &&
                        request.CompressionLevel != ArchiveCompressionLevel.Store)
                    {
                        log.Report($"高速化: 圧縮効果が見込めないためStoreで追加します: {sourceFile.EntryName}");
                    }

                    if (useFrameworkFileWriter)
                    {
                        archive.CreateEntryFromFile(sourceFile.FullPath, sourceFile.EntryName, entryCompressionLevel);
                        processedBytes += sourceFile.Length;
                        processedFiles++;
                        progress.Report(new ArchiveProgress(
                            request.SourceManifest.TotalBytes,
                            processedBytes,
                            request.SourceManifest.TotalFiles,
                            processedFiles,
                            sourceFile.EntryName,
                            "圧縮済み"));
                        continue;
                    }

                    var entry = archive.CreateEntry(sourceFile.EntryName, entryCompressionLevel);
                    entry.LastWriteTime = sourceFile.LastWriteTime;

                    await using var sourceStream = new FileStream(
                        sourceFile.FullPath,
                        FileMode.Open,
                        FileAccess.Read,
                        FileShare.Read,
                        BufferSize,
                        FileOptions.Asynchronous | FileOptions.SequentialScan);

                    await using var destinationStream = entry.Open();
                    await CopyWithProgressAsync(
                        sourceStream,
                        destinationStream,
                        request.SourceManifest.TotalBytes,
                        request.SourceManifest.TotalFiles,
                        sourceFile.EntryName,
                        bytes =>
                        {
                            processedBytes += bytes;
                            progress.Report(new ArchiveProgress(
                                request.SourceManifest.TotalBytes,
                                processedBytes,
                                request.SourceManifest.TotalFiles,
                                processedFiles,
                                sourceFile.EntryName,
                                "圧縮中"));
                        },
                        cancellationToken).ConfigureAwait(false);

                    processedFiles++;
                    progress.Report(new ArchiveProgress(
                        request.SourceManifest.TotalBytes,
                        processedBytes,
                        request.SourceManifest.TotalFiles,
                        processedFiles,
                        sourceFile.EntryName,
                        "圧縮済み"));
                }
            }
        }
        catch
        {
            if (File.Exists(outputPath))
            {
                TryDeletePartialFile(outputPath);
            }

            throw;
        }

        var completedAt = DateTimeOffset.Now;
        var outputBytes = new FileInfo(outputPath).Length;
        log.Report($"処理済みサイズ: {FormatBytes(request.SourceManifest.TotalBytes)}");
        log.Report($"完了時刻: {completedAt:yyyy-MM-dd HH:mm:ss}");
        log.Report($"処理時間: {(completedAt - startedAt):hh\\:mm\\:ss}");

        progress.Report(new ArchiveProgress(
            request.SourceManifest.TotalBytes,
            request.SourceManifest.TotalBytes,
            request.SourceManifest.TotalFiles,
            request.SourceManifest.TotalFiles,
            outputPath,
            "圧縮完了"));

        return new ArchiveOperationResult(
            startedAt,
            completedAt,
            string.Join("; ", request.SourceManifest.InputPaths),
            outputPath,
            Name,
            runtimeFallback
                ? "自動フォールバック"
                : usedGpuWriter
                    ? "GPU"
                    : usedParallelWriter
                        ? "Parallel CPU"
                        : SafeEngineLabel(request.SelectedEngine, backend.Name),
            request.SourceManifest.TotalFiles,
            request.SourceManifest.TotalBytes,
            outputBytes,
            request.UsedFallback || runtimeFallback,
            runtimeFallback ? runtimeFallbackReason : SafeFallbackReason(request.FallbackReason));
    }

    public async Task<ArchiveOperationResult> ExtractArchiveAsync(
        ArchiveExtractRequest request,
        ICompressionBackend backend,
        IProgress<ArchiveProgress> progress,
        IProgress<string> log,
        CancellationToken cancellationToken)
    {
        var startedAt = DateTimeOffset.Now;
        var archivePath = Path.GetFullPath(request.ArchivePath);
        var destinationRoot = Path.GetFullPath(request.DestinationDirectory);

        if (!File.Exists(archivePath))
        {
            throw new FileNotFoundException("ZIPファイルが存在しません。", archivePath);
        }

        if (ZipEncryptionDetector.HasEncryptedEntries(archivePath))
        {
            throw new UnsupportedZipException("このZIPは暗号化されているため未対応です。");
        }

        Directory.CreateDirectory(destinationRoot);

        log.Report($"開始時刻: {startedAt:yyyy-MM-dd HH:mm:ss}");
        log.Report($"入力パス: {archivePath}");
        log.Report($"出力パス: {destinationRoot}");
        log.Report("選択された形式: ZIP");
        log.Report($"GPUモード: {request.GpuMode}");
        log.Report($"使用バックエンド: {backend.Name}");
        log.Report($"処理モード: {SafeEngineLabel(request.SelectedEngine, backend.Name)}");
        log.Report($"フォールバック理由: {SafeFallbackReason(request.FallbackReason)}");

        await using var fileStream = new FileStream(
            archivePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            BufferSize,
            FileOptions.Asynchronous | FileOptions.SequentialScan);

        var entryNameEncoding = ZipEntryNameEncoding.GetReadEntryNameEncoding(fileStream);
        using var archive = new ZipArchive(
            fileStream,
            ZipArchiveMode.Read,
            leaveOpen: false,
            entryNameEncoding: entryNameEncoding);
        var entries = archive.Entries.ToArray();
        var extractPlan = BuildExtractPlan(entries, destinationRoot, request.OverwriteExisting);
        var totalBytes = extractPlan.Where(item => !item.IsDirectory).Sum(item => item.Entry.Length);
        var totalFiles = extractPlan.Count(item => !item.IsDirectory);

        log.Report($"処理対象ファイル数: {totalFiles:N0}");
        log.Report($"合計サイズ: {FormatBytes(totalBytes)}");

        progress.Report(new ArchiveProgress(
            totalBytes,
            0,
            totalFiles,
            0,
            string.Empty,
            "ZIP展開を開始しています。"));

        var processedBytes = 0L;
        var processedFiles = 0;

        foreach (var item in extractPlan)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (item.IsDirectory)
            {
                Directory.CreateDirectory(item.DestinationPath);
                continue;
            }

            var parentDirectory = Path.GetDirectoryName(item.DestinationPath);
            if (!string.IsNullOrWhiteSpace(parentDirectory))
            {
                Directory.CreateDirectory(parentDirectory);
            }

            await using var entryStream = item.Entry.Open();
            await using var outputStream = new FileStream(
                item.DestinationPath,
                request.OverwriteExisting ? FileMode.Create : FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                BufferSize,
                FileOptions.Asynchronous | FileOptions.SequentialScan);

            await CopyWithProgressAsync(
                entryStream,
                outputStream,
                totalBytes,
                totalFiles,
                item.Entry.FullName,
                bytes =>
                {
                    processedBytes += bytes;
                    progress.Report(new ArchiveProgress(
                        totalBytes,
                        processedBytes,
                        totalFiles,
                        processedFiles,
                        item.Entry.FullName,
                        "展開中"));
                },
                cancellationToken).ConfigureAwait(false);

            if (outputStream.Length != item.Entry.Length)
            {
                throw new InvalidDataException($"展開後のファイルサイズ検証に失敗しました: {item.Entry.FullName}");
            }

            processedFiles++;
            SetLastWriteTime(item.DestinationPath, item.Entry.LastWriteTime);
            progress.Report(new ArchiveProgress(
                totalBytes,
                processedBytes,
                totalFiles,
                processedFiles,
                item.Entry.FullName,
                "展開済み"));
        }

        var completedAt = DateTimeOffset.Now;
        log.Report($"処理済みサイズ: {FormatBytes(processedBytes)}");
        log.Report($"完了時刻: {completedAt:yyyy-MM-dd HH:mm:ss}");
        log.Report($"処理時間: {(completedAt - startedAt):hh\\:mm\\:ss}");

        progress.Report(new ArchiveProgress(
            totalBytes,
            totalBytes,
            totalFiles,
            totalFiles,
            destinationRoot,
            "展開完了"));

        return new ArchiveOperationResult(
            startedAt,
            completedAt,
            archivePath,
            destinationRoot,
            Name,
            SafeEngineLabel(request.SelectedEngine, backend.Name),
            totalFiles,
            new FileInfo(archivePath).Length,
            processedBytes,
            request.UsedFallback,
            SafeFallbackReason(request.FallbackReason));
    }

    public Task<int> CountExtractionConflictsAsync(
        string archivePath,
        string destinationDirectory,
        CancellationToken cancellationToken)
    {
        return Task.Run(() =>
        {
            using var fileStream = File.OpenRead(archivePath);
            var entryNameEncoding = ZipEntryNameEncoding.GetReadEntryNameEncoding(fileStream);
            using var archive = new ZipArchive(
                fileStream,
                ZipArchiveMode.Read,
                leaveOpen: false,
                entryNameEncoding: entryNameEncoding);
            var plan = BuildExtractPlan(archive.Entries, Path.GetFullPath(destinationDirectory), overwriteExisting: true);
            cancellationToken.ThrowIfCancellationRequested();
            return plan.Count(item => !item.IsDirectory && File.Exists(item.DestinationPath));
        }, cancellationToken);
    }

    private IReadOnlyList<ExtractPlanItem> BuildExtractPlan(
        IEnumerable<ZipArchiveEntry> entries,
        string destinationRoot,
        bool overwriteExisting)
    {
        var plan = new List<ExtractPlanItem>();

        foreach (var entry in entries)
        {
            var isDirectory = IsDirectoryEntry(entry);
            if (!_pathSafetyValidator.TryGetSafeDestinationPath(destinationRoot, entry.FullName, out var destinationPath, out var reason))
            {
                throw new ArchiveSecurityException($"{reason} エントリ: {entry.FullName}");
            }

            if (!isDirectory && File.Exists(destinationPath) && !overwriteExisting)
            {
                throw new IOException($"展開先に既存ファイルがあります: {destinationPath}");
            }

            plan.Add(new ExtractPlanItem(entry, destinationPath, isDirectory));
        }

        return plan;
    }

    private static bool IsDirectoryEntry(ZipArchiveEntry entry) =>
        entry.FullName.EndsWith("/", StringComparison.Ordinal) ||
        entry.FullName.EndsWith("\\", StringComparison.Ordinal);

    private static async Task CopyWithProgressAsync(
        Stream source,
        Stream destination,
        long totalBytes,
        int totalFiles,
        string currentPath,
        Action<int> bytesCopied,
        CancellationToken cancellationToken)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(BufferSize);
        try
        {
            while (true)
            {
                var read = await source.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken).ConfigureAwait(false);
                if (read == 0)
                {
                    break;
                }

                await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                bytesCopied(read);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static void SetLastWriteTime(string path, DateTimeOffset lastWriteTime)
    {
        try
        {
            File.SetLastWriteTime(path, lastWriteTime.LocalDateTime);
        }
        catch
        {
            // Timestamp restoration is best effort and should not fail the archive operation.
        }
    }

    private static void TryDeletePartialFile(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch
        {
            // The original error is more important than cleanup failure.
        }
    }

    private static string FormatBytes(long bytes)
    {
        string[] units = { "B", "KB", "MB", "GB", "TB" };
        var value = (double)bytes;
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        return $"{value:N1} {units[unit]}";
    }

    private static string SafeEngineLabel(string selectedEngine, string backendName) =>
        string.IsNullOrWhiteSpace(selectedEngine) ? backendName : selectedEngine;

    private static string SafeFallbackReason(string fallbackReason) =>
        FallbackReasonFormatter.Format(fallbackReason);

    private static ParallelWriterDecision EvaluateParallelWriter(ArchiveCreateRequest request, ICompressionBackend backend)
    {
        if (!backend.Name.Contains("CPU", StringComparison.OrdinalIgnoreCase))
        {
            return new(false, "GPU backend が選択されているため並列CPU ZIP writer は使用しません。");
        }

        if (request.CompressionLevel == ArchiveCompressionLevel.Store)
        {
            return new(false, "Store は圧縮処理がないため並列DEFLATEを使用しません。", UseFrameworkFileWriter: true);
        }

        if (request.SourceManifest.Files.Count < 2)
        {
            return new(false, "複数ファイルではないため並列ファイル圧縮は使用しません。");
        }

        if (request.SourceManifest.TotalBytes < ParallelWriterThresholdBytes)
        {
            return new(false, "入力が小さいため並列化コストを避けます。", UseFrameworkFileWriter: true);
        }

        var sampleRatio = EstimateSampleDeflateRatio(request.SourceManifest.Files);
        if (sampleRatio < ParallelSampleMinimumCompressedRatio)
        {
            return new(false, $"サンプル圧縮率が高いため最適化済み逐次ZIP writerを使用します。sample compressed/input={sampleRatio:P1}", UseFrameworkFileWriter: true);
        }

        return new(true, $"複数ファイルで圧縮負荷が高い見込みのため並列ZIP writerを使用します。sample compressed/input={sampleRatio:P1}");
    }

    private static bool IsGpuZipBackend(ICompressionBackend backend) =>
        !backend.Name.Contains("CPU", StringComparison.OrdinalIgnoreCase) &&
        backend.SupportsZipDeflate;

    private static double EstimateSampleDeflateRatio(IReadOnlyList<ArchiveSourceFile> files)
    {
        var totalInput = 0L;
        var totalCompressed = 0L;
        var buffer = ArrayPool<byte>.Shared.Rent(ParallelSampleBytesPerFile);
        try
        {
            foreach (var file in files.Take(ParallelSampleFileLimit))
            {
                using var source = new FileStream(file.FullPath, FileMode.Open, FileAccess.Read, FileShare.Read, BufferSize, FileOptions.SequentialScan);
                var read = source.Read(buffer, 0, Math.Min(buffer.Length, (int)Math.Min(file.Length, ParallelSampleBytesPerFile)));
                if (read <= 0)
                {
                    continue;
                }

                using var memory = new MemoryStream();
                using (var deflate = new DeflateStream(memory, CompressionLevel.Optimal, leaveOpen: true))
                {
                    deflate.Write(buffer, 0, read);
                }

                totalInput += read;
                totalCompressed += memory.Length;
            }
        }
        catch
        {
            return 0;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }

        return totalInput == 0 ? 0 : (double)totalCompressed / totalInput;
    }

    private sealed record ExtractPlanItem(ZipArchiveEntry Entry, string DestinationPath, bool IsDirectory);

    private sealed record ParallelWriterDecision(
        bool UseParallelWriter,
        string Reason,
        bool UseFrameworkFileWriter = false);
}
