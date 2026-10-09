using System.Buffers;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using EZConverter.Compression.Models;
using EZConverter.Compression.Services.Compression;
using EZConverter.Compression.Services.Security;

namespace EZConverter.Compression.Services.Archives;

public sealed class ZiperFastArchiveFormat : IArchiveFormat
{
    private const int FormatVersion = 1;
    private const int HeaderSize = 20;
    private const int BufferSize = 1024 * 1024;
    private const int DefaultCpuChunkSize = 64 * 1024;
    private const int DefaultGpuChunkSize = 64 * 1024;
    private const int MinimumChunkSize = 16 * 1024;
    private const long DefaultGpuBatchInputBytes = 128L * 1024 * 1024;
    private const long GpuValidationSampleBytes = 8L * 1024 * 1024;
    private const long MaximumManifestBytes = 16L * 1024 * 1024;
    private const int StoreMethod = 0;
    private const int DeflateMethod = 8;
    private static readonly byte[] Magic = Encoding.ASCII.GetBytes("ZIPERF01");
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly PathSafetyValidator _pathSafetyValidator = new();

    public string Name => "Ziper Fast";

    public IReadOnlyCollection<string> Extensions { get; } = new[] { ".ziper" };

    public bool CanRead(string path) =>
        string.Equals(Path.GetExtension(path), ".ziper", StringComparison.OrdinalIgnoreCase);

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
                throw new IOException("同名のアーカイブファイルが既に存在します。");
            }

            File.Delete(outputPath);
        }

        var tempDirectory = Path.Combine(Path.GetTempPath(), "Ziper", Guid.NewGuid().ToString("N"));
        var tempPayloadPath = Path.Combine(tempDirectory, "payload.bin");
        var usedGpu = false;
        var usedFallback = request.UsedFallback;
        var fallbackReason = request.FallbackReason;
        var processedBytes = 0L;
        var processedFiles = 0;

        log.Report($"開始時刻: {startedAt:yyyy-MM-dd HH:mm:ss}");
        log.Report($"入力パス: {string.Join("; ", request.SourceManifest.InputPaths)}");
        log.Report($"出力パス: {outputPath}");
        log.Report("選択された形式: Ziper Fast (.ziper)");
        log.Report(".ziper はZiper専用の高速チャンク形式です。Windows標準/7-Zip/WinRAR互換が必要な場合はZIPを使用してください。");
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
            "Ziper Fast作成を開始しています。"));

        Directory.CreateDirectory(tempDirectory);
        try
        {
            var chunkSize = DetermineChunkSize(backend, request.CompressionLevel);
            var maxBatchInputBytes = DetermineMaxBatchInputBytes(backend);
            var useGpuBatch = request.CompressionLevel != ArchiveCompressionLevel.Store &&
                              backend is ICompressionBatchBackend { SupportsBatchCompression: true } batchBackend &&
                              backend.IsAvailable &&
                              backend.SupportsZipDeflate;

            log.Report($"高速化: チャンクサイズ {FormatBytes(chunkSize)}, batch上限 {FormatBytes(maxBatchInputBytes)}");
            log.Report(useGpuBatch
                ? "高速化: GPU batchでチャンク圧縮します。GPU失敗時はbatch単位でCPUへフォールバックします。"
                : "高速化: CPU chunk writerを使用します。");

            var document = new FastArchiveDocument
            {
                CreatedUtc = DateTimeOffset.UtcNow,
                Directories = request.SourceManifest.DirectoryEntries
                    .Where(entry => entry.EndsWith("/", StringComparison.Ordinal))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList()
            };

            var prepareStopwatch = Stopwatch.StartNew();
            await using (var payloadStream = new FileStream(
                             tempPayloadPath,
                             FileMode.CreateNew,
                             FileAccess.ReadWrite,
                             FileShare.None,
                             BufferSize,
                             FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                foreach (var sourceFile in request.SourceManifest.Files)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var result = await PrepareFileAsync(
                        sourceFile,
                        request,
                        backend,
                        payloadStream,
                        chunkSize,
                        maxBatchInputBytes,
                        useGpuBatch,
                        bytes =>
                        {
                            processedBytes += bytes;
                            progress.Report(new ArchiveProgress(
                                request.SourceManifest.TotalBytes,
                                processedBytes,
                                request.SourceManifest.TotalFiles,
                                processedFiles,
                                sourceFile.EntryName,
                                "チャンク読込中"));
                        },
                        log,
                        cancellationToken).ConfigureAwait(false);

                    document.Files.Add(result.File);
                    usedGpu |= result.UsedGpu;
                    if (result.UsedFallback)
                    {
                        usedFallback = true;
                        fallbackReason = AppendFallbackReason(fallbackReason, result.FallbackReason);
                    }

                    processedFiles++;
                    progress.Report(new ArchiveProgress(
                        request.SourceManifest.TotalBytes,
                        processedBytes,
                        request.SourceManifest.TotalFiles,
                        processedFiles,
                        sourceFile.EntryName,
                        "チャンク圧縮済み"));
                }

                await payloadStream.FlushAsync(cancellationToken).ConfigureAwait(false);
            }

            prepareStopwatch.Stop();
            log.Report($"高速化計測: chunk prepare {request.SourceManifest.TotalFiles:N0} files / {FormatBytes(request.SourceManifest.TotalBytes)} {prepareStopwatch.Elapsed:hh\\:mm\\:ss\\.fff}");

            ValidateDocumentForWrite(document);
            var manifestBytes = JsonSerializer.SerializeToUtf8Bytes(document, JsonOptions);
            if (manifestBytes.LongLength > MaximumManifestBytes)
            {
                throw new InvalidDataException("アーカイブの目録が大きすぎます。入力ファイル数またはチャンク数を減らしてください。");
            }

            var writeStopwatch = Stopwatch.StartNew();
            await using (var outputStream = new FileStream(
                             outputPath,
                             FileMode.CreateNew,
                             FileAccess.ReadWrite,
                             FileShare.None,
                             BufferSize,
                             FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                WriteHeader(outputStream, manifestBytes.LongLength);
                await outputStream.WriteAsync(manifestBytes, cancellationToken).ConfigureAwait(false);
                await using var payloadStream = new FileStream(
                    tempPayloadPath,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.Read,
                    BufferSize,
                    FileOptions.Asynchronous | FileOptions.SequentialScan);
                await payloadStream.CopyToAsync(outputStream, BufferSize, cancellationToken).ConfigureAwait(false);
                await outputStream.FlushAsync(cancellationToken).ConfigureAwait(false);
            }

            writeStopwatch.Stop();
            log.Report($"高速化計測: archive write {document.Files.Count:N0} files / {document.TotalChunkCount:N0} chunks {writeStopwatch.Elapsed:hh\\:mm\\:ss\\.fff}");
        }
        catch
        {
            TryDelete(outputPath);
            throw;
        }
        finally
        {
            TryDeleteDirectory(tempDirectory);
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
            usedFallback
                ? "自動フォールバック"
                : usedGpu
                    ? "GPU"
                    : SafeEngineLabel(request.SelectedEngine, backend.Name),
            request.SourceManifest.TotalFiles,
            request.SourceManifest.TotalBytes,
            outputBytes,
            usedFallback,
            SafeFallbackReason(fallbackReason));
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
            throw new FileNotFoundException("アーカイブファイルが存在しません。", archivePath);
        }

        Directory.CreateDirectory(destinationRoot);

        log.Report($"開始時刻: {startedAt:yyyy-MM-dd HH:mm:ss}");
        log.Report($"入力パス: {archivePath}");
        log.Report($"出力パス: {destinationRoot}");
        log.Report("選択された形式: Ziper Fast (.ziper)");
        log.Report($"GPUモード: {request.GpuMode}");
        log.Report($"使用バックエンド: {backend.Name}（入力安全検証付き）");
        log.Report("処理モード: GPU対応チャンクはGPUで展開できます。");
        log.Report("フォールバック理由: なし");

        await using var archiveStream = new FileStream(
            archivePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            BufferSize,
            FileOptions.Asynchronous | FileOptions.SequentialScan);

        var readResult = await ReadArchiveDocumentAsync(archiveStream, cancellationToken).ConfigureAwait(false);
        var document = readResult.Document;
        ValidateDocumentForRead(document, archiveStream.Length, readResult.PayloadBaseOffset);
        var plan = BuildExtractPlan(document, destinationRoot, request.OverwriteExisting);
        var totalBytes = document.Files.Sum(file => file.Length);
        var totalFiles = document.Files.Count;
        var volume = new DriveInfo(Path.GetPathRoot(Path.GetFullPath(destinationRoot))!);
        if (totalBytes > Math.Max(0, volume.AvailableFreeSpace - 64L * 1024 * 1024))
            throw new IOException("展開先の空き容量が不足しています。");
        bool usedGpuExtraction = false;
        string extractionFallback = request.FallbackReason;

        log.Report($"処理対象ファイル数: {totalFiles:N0}");
        log.Report($"合計サイズ: {FormatBytes(totalBytes)}");

        progress.Report(new ArchiveProgress(
            totalBytes,
            0,
            totalFiles,
            0,
            string.Empty,
            "Ziper Fast展開を開始しています。"));

        foreach (var directoryPath in plan.Directories)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Directory.CreateDirectory(directoryPath);
        }

        var processedBytes = 0L;
        var processedFiles = 0;
        foreach (var item in plan.Files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var parentDirectory = Path.GetDirectoryName(item.DestinationPath);
            if (!string.IsNullOrWhiteSpace(parentDirectory))
            {
                Directory.CreateDirectory(parentDirectory);
            }

            var crc32 = new Crc32();
            await using (var outputStream = new FileStream(
                             item.DestinationPath,
                             request.OverwriteExisting ? FileMode.Create : FileMode.CreateNew,
                             FileAccess.Write,
                             FileShare.None,
                             BufferSize,
                             FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                for (int batchStart = 0; batchStart < item.Entry.Chunks.Count; batchStart += 16)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var chunks = item.Entry.Chunks.Skip(batchStart).Take(16).ToArray();
                    var decoded = new ReadOnlyMemory<byte>[chunks.Length];
                    var compressed = new List<ReadOnlyMemory<byte>>();
                    var sizes = new List<int>();
                    var indices = new List<int>();
                    for (int i = 0; i < chunks.Length; i++)
                    {
                        var chunk = chunks[i];
                        archiveStream.Position = checked(readResult.PayloadBaseOffset + chunk.Offset);
                        var bytes = new byte[checked((int)chunk.CompressedSize)];
                        await archiveStream.ReadExactlyAsync(bytes, cancellationToken).ConfigureAwait(false);
                        if (chunk.Method == StoreMethod) decoded[i] = bytes;
                        else { compressed.Add(bytes); sizes.Add(chunk.UncompressedSize); indices.Add(i); }
                    }
                    if (compressed.Count > 0)
                    {
                        if (backend is GpuCompressionBackend gpu)
                        {
                            bool preferGpu = request.GpuMode == GpuMode.GpuPreferred ||
                                             (request.GpuMode == GpuMode.Auto && item.Entry.Length >= 8L * 1024 * 1024);
                            var result = gpu.DecodeBatch(compressed, sizes, preferGpu, cancellationToken);
                            usedGpuExtraction |= result.UsedGpu;
                            if (!string.IsNullOrEmpty(result.Reason)) { extractionFallback = result.Reason; log.Report(result.Reason); }
                            for (int i = 0; i < indices.Count; i++) decoded[indices[i]] = result.Bytes[i];
                        }
                        else
                        {
                            for (int i = 0; i < indices.Count; i++)
                                decoded[indices[i]] = VerifiedDeflate.Decode(compressed[i], sizes[i], cancellationToken);
                        }
                    }
                    for (int i = 0; i < chunks.Length; i++)
                    {
                        if (decoded[i].Length != chunks[i].UncompressedSize || ComputeCrc32(decoded[i].Span) != chunks[i].Crc32)
                            throw new InvalidDataException($"チャンクのCRC32またはサイズが一致しません: {item.Entry.EntryName}");
                        crc32.Update(decoded[i].Span);
                        await outputStream.WriteAsync(decoded[i], cancellationToken).ConfigureAwait(false);
                        processedBytes += decoded[i].Length;
                        progress.Report(new ArchiveProgress(totalBytes, processedBytes, totalFiles, processedFiles, item.Entry.EntryName, "展開中"));
                    }
                }

                if (outputStream.Length != item.Entry.Length)
                {
                    throw new InvalidDataException($"展開後のファイルサイズ検証に失敗しました: {item.Entry.EntryName}");
                }
            }

            if (crc32.GetCurrentHash() != item.Entry.Crc32)
            {
                throw new InvalidDataException($"CRC32検証に失敗しました: {item.Entry.EntryName}");
            }

            SetLastWriteTime(item.DestinationPath, item.Entry.LastWriteTimeUtcTicks);
            processedFiles++;
            progress.Report(new ArchiveProgress(
                totalBytes,
                processedBytes,
                totalFiles,
                processedFiles,
                item.Entry.EntryName,
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
            usedGpuExtraction ? "GPU（検証付き）" : "CPU",
            totalFiles,
            new FileInfo(archivePath).Length,
            processedBytes,
            request.UsedFallback || !string.IsNullOrEmpty(extractionFallback),
            extractionFallback);
    }

    public async Task<int> CountExtractionConflictsAsync(
        string archivePath,
        string destinationDirectory,
        CancellationToken cancellationToken)
    {
        await using var archiveStream = new FileStream(
            archivePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            BufferSize,
            FileOptions.Asynchronous | FileOptions.SequentialScan);

        var readResult = await ReadArchiveDocumentAsync(archiveStream, cancellationToken).ConfigureAwait(false);
        ValidateDocumentForRead(readResult.Document, archiveStream.Length, readResult.PayloadBaseOffset);
        var plan = BuildExtractPlan(readResult.Document, Path.GetFullPath(destinationDirectory), overwriteExisting: true);
        cancellationToken.ThrowIfCancellationRequested();
        return plan.Files.Count(item => File.Exists(item.DestinationPath));
    }

    private async Task<PreparedFastFileResult> PrepareFileAsync(
        ArchiveSourceFile sourceFile,
        ArchiveCreateRequest request,
        ICompressionBackend backend,
        FileStream payloadStream,
        int chunkSize,
        long maxBatchInputBytes,
        bool useGpuBatch,
        Action<int> bytesRead,
        IProgress<string> log,
        CancellationToken cancellationToken)
    {
        var shouldStoreFile = ZipCompressionMethodSelector.ShouldStoreForSpeed(sourceFile, request.CompressionLevel);
        if (shouldStoreFile && request.CompressionLevel != ArchiveCompressionLevel.Store)
        {
            log.Report($"高速化: 圧縮効果が見込めないためStoreでチャンク化します: {sourceFile.EntryName}");
        }

        var fileEntry = new FastFileEntry
        {
            EntryName = sourceFile.EntryName,
            Length = sourceFile.Length,
            LastWriteTimeUtcTicks = sourceFile.LastWriteTime.UtcTicks
        };

        if (sourceFile.Length == 0)
        {
            fileEntry.Crc32 = 0;
            return new PreparedFastFileResult(fileEntry, false, false, string.Empty);
        }

        var crc32 = new Crc32();
        var pending = new List<PendingChunk>();
        var pendingBytes = 0L;
        var usedGpu = false;
        var usedFallback = false;
        var fallbackReason = string.Empty;
        var gpuValidationPassed = false;
        var reportedSlowGpuFallback = false;
        var performanceValidationState = useGpuBatch &&
                                         backend is ICompressionBatchBackend { RequiresCpuPerformanceValidation: true }
            ? BatchPerformanceValidationState.Unvalidated
            : BatchPerformanceValidationState.Accepted;

        await using var sourceStream = new FileStream(
            sourceFile.FullPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            BufferSize,
            FileOptions.Asynchronous | FileOptions.SequentialScan);

        var chunkIndex = 0;
        while (sourceStream.Position < sourceFile.Length)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var remaining = sourceFile.Length - sourceStream.Position;
            var readSize = checked((int)Math.Min(chunkSize, remaining));
            var bytes = GC.AllocateUninitializedArray<byte>(readSize);
            await ReadExactlyAsync(sourceStream, bytes, cancellationToken).ConfigureAwait(false);
            crc32.Update(bytes);
            bytesRead(bytes.Length);

            pending.Add(new PendingChunk(chunkIndex++, bytes, ComputeCrc32(bytes)));
            pendingBytes += bytes.Length;
            if (pendingBytes >= maxBatchInputBytes)
            {
                await FlushPendingAsync().ConfigureAwait(false);
            }
        }

        await FlushPendingAsync().ConfigureAwait(false);
        fileEntry.Crc32 = crc32.GetCurrentHash();
        return new PreparedFastFileResult(fileEntry, usedGpu, usedFallback, fallbackReason);

        async Task FlushPendingAsync()
        {
            if (pending.Count == 0)
            {
                return;
            }

            cancellationToken.ThrowIfCancellationRequested();
            var batch = pending.ToArray();
            pending.Clear();
            pendingBytes = 0;

            IReadOnlyList<CompressionBatchResult>? gpuResults = null;
            if (!shouldStoreFile && useGpuBatch && backend is ICompressionBatchBackend batchBackend)
            {
                if (performanceValidationState == BatchPerformanceValidationState.Rejected)
                {
                    usedFallback = true;
                    if (!reportedSlowGpuFallback)
                    {
                        fallbackReason = AppendFallbackReason(
                            fallbackReason,
                            "GPU chunkフォールバック: 前回のGPU chunkサンプルがCPUより低速だったため、残りのchunkをCPUで処理します。");
                        reportedSlowGpuFallback = true;
                    }
                }
                else
                {
                    try
                    {
                        if (performanceValidationState == BatchPerformanceValidationState.Unvalidated)
                        {
                            var sample = TakeValidationSample(batch);
                            var (gpuSampleResults, gpuSampleElapsed) = await CompressBatchWithGpuAsync(
                                batchBackend,
                                sample,
                                sourceFile.EntryName,
                                request.CompressionLevel,
                                cancellationToken).ConfigureAwait(false);
                            ValidateGpuCompressedChunks(sample, gpuSampleResults);

                            var cpuSampleStopwatch = Stopwatch.StartNew();
                            _ = await CompressBatchWithCpuAsync(sample, request.CompressionLevel, cancellationToken).ConfigureAwait(false);
                            cpuSampleStopwatch.Stop();

                            var speedup = gpuSampleElapsed.TotalSeconds <= 0
                                ? double.PositiveInfinity
                                : cpuSampleStopwatch.Elapsed.TotalSeconds / gpuSampleElapsed.TotalSeconds;
                            var minimumSpeedup = batchBackend.MinimumSpeedupOverCpu <= 0
                                ? 1.0
                                : batchBackend.MinimumSpeedupOverCpu;
                            log.Report($"GPU chunk検証: sample {sample.Count:N0} chunks / {FormatBytes(sample.Sum(chunk => chunk.Bytes.Length))}, GPU {gpuSampleElapsed:hh\\:mm\\:ss\\.fff}, CPU {cpuSampleStopwatch.Elapsed:hh\\:mm\\:ss\\.fff}, speedup {speedup:N2}x");

                            if (speedup < minimumSpeedup)
                            {
                                performanceValidationState = BatchPerformanceValidationState.Rejected;
                                usedFallback = true;
                                fallbackReason = AppendFallbackReason(
                                    fallbackReason,
                                    $"GPU chunkフォールバック: サンプルspeedup {speedup:N2}x が必要値 {minimumSpeedup:N2}x を下回りました。");
                                reportedSlowGpuFallback = true;
                                log.Report(fallbackReason);
                            }
                            else
                            {
                                performanceValidationState = BatchPerformanceValidationState.Accepted;
                                gpuValidationPassed = true;
                            }
                        }

                        if (performanceValidationState != BatchPerformanceValidationState.Rejected)
                        {
                            var (results, elapsed) = await CompressBatchWithGpuAsync(
                                batchBackend,
                                batch,
                                sourceFile.EntryName,
                                request.CompressionLevel,
                                cancellationToken).ConfigureAwait(false);
                            gpuResults = results;
                            if (!gpuValidationPassed)
                            {
                                ValidateGpuCompressedChunks(batch, gpuResults);
                                gpuValidationPassed = true;
                                log.Report($"GPU chunk検証: {Math.Min(batch.Length, 8):N0} chunks OK");
                            }

                            usedGpu = true;
                            log.Report($"GPU chunk計測: {batch.Length:N0} chunks / {FormatBytes(batch.Sum(chunk => chunk.Bytes.Length))} {elapsed:hh\\:mm\\:ss\\.fff}");
                        }
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        usedFallback = true;
                        fallbackReason = AppendFallbackReason(
                            fallbackReason,
                            $"GPU chunkフォールバック: {sourceFile.EntryName} をCPUで処理しました: {ex.Message}");
                        log.Report(fallbackReason);
                        gpuResults = null;
                    }
                }
            }

            if (shouldStoreFile || request.CompressionLevel == ArchiveCompressionLevel.Store)
            {
                foreach (var chunk in batch)
                {
                    await AppendChunkAsync(fileEntry, payloadStream, chunk, StoreMethod, chunk.Bytes, cancellationToken).ConfigureAwait(false);
                }

                return;
            }

            if (gpuResults is not null)
            {
                if (gpuResults.Count != batch.Length)
                {
                    throw new InvalidDataException("GPU batch圧縮結果の件数が入力チャンク数と一致しません。");
                }

                for (var index = 0; index < batch.Length; index++)
                {
                    var compressed = gpuResults[index].CompressedBytes;
                    if (compressed.Length >= batch[index].Bytes.Length)
                    {
                        await AppendChunkAsync(fileEntry, payloadStream, batch[index], StoreMethod, batch[index].Bytes, cancellationToken).ConfigureAwait(false);
                    }
                    else
                    {
                        await AppendChunkAsync(fileEntry, payloadStream, batch[index], DeflateMethod, compressed, cancellationToken).ConfigureAwait(false);
                    }
                }

                return;
            }

            var cpuResults = await CompressBatchWithCpuAsync(batch, request.CompressionLevel, cancellationToken).ConfigureAwait(false);
            for (var index = 0; index < batch.Length; index++)
            {
                if (cpuResults[index].Length >= batch[index].Bytes.Length)
                {
                    await AppendChunkAsync(fileEntry, payloadStream, batch[index], StoreMethod, batch[index].Bytes, cancellationToken).ConfigureAwait(false);
                }
                else
                {
                    await AppendChunkAsync(fileEntry, payloadStream, batch[index], DeflateMethod, cpuResults[index], cancellationToken).ConfigureAwait(false);
                }
            }
        }
    }

    private static async Task AppendChunkAsync(
        FastFileEntry fileEntry,
        FileStream payloadStream,
        PendingChunk chunk,
        int method,
        ReadOnlyMemory<byte> payload,
        CancellationToken cancellationToken)
    {
        var offset = payloadStream.Position;
        await payloadStream.WriteAsync(payload, cancellationToken).ConfigureAwait(false);
        fileEntry.Chunks.Add(new FastChunkEntry
        {
            Offset = offset,
            Method = method,
            CompressedSize = payload.Length,
            UncompressedSize = chunk.Bytes.Length,
            Crc32 = chunk.Crc32
        });
    }

    private static async Task<ReadOnlyMemory<byte>[]> CompressBatchWithCpuAsync(
        IReadOnlyList<PendingChunk> chunks,
        ArchiveCompressionLevel compressionLevel,
        CancellationToken cancellationToken)
    {
        var results = new ReadOnlyMemory<byte>[chunks.Count];
        var maxDegreeOfParallelism = ArchiveResourceLimits.GetResponsiveParallelism(chunks.Count);
        await Parallel.ForEachAsync(
            chunks.Select((chunk, index) => (chunk, index)),
            new ParallelOptions
            {
                CancellationToken = cancellationToken,
                MaxDegreeOfParallelism = maxDegreeOfParallelism
            },
            async (item, token) =>
            {
                await using var input = new MemoryStream(item.chunk.Bytes, writable: false);
                await using var output = new MemoryStream();
                using (var deflate = new DeflateStream(
                           output,
                           CompressionLevelMapper.ToSystemCompressionLevel(compressionLevel),
                           leaveOpen: true))
                {
                    await input.CopyToAsync(deflate, token).ConfigureAwait(false);
                }

                results[item.index] = output.ToArray();
            }).ConfigureAwait(false);

        return results;
    }

    private static async Task<(IReadOnlyList<CompressionBatchResult> Results, TimeSpan Elapsed)> CompressBatchWithGpuAsync(
        ICompressionBatchBackend batchBackend,
        IReadOnlyList<PendingChunk> chunks,
        string sourceEntryName,
        ArchiveCompressionLevel compressionLevel,
        CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        var results = await batchBackend.CompressBatchAsync(
            chunks.Select(chunk => new CompressionBatchItem(
                $"{sourceEntryName}:{chunk.Index}",
                chunk.Bytes)).ToArray(),
            new CompressionJobOptions(compressionLevel),
            cancellationToken).ConfigureAwait(false);
        stopwatch.Stop();
        return (results, stopwatch.Elapsed);
    }

    private static IReadOnlyList<PendingChunk> TakeValidationSample(IReadOnlyList<PendingChunk> chunks)
    {
        var sample = new List<PendingChunk>();
        var sampleBytes = 0L;
        foreach (var chunk in chunks)
        {
            sample.Add(chunk);
            sampleBytes += chunk.Bytes.Length;
            if (sampleBytes >= GpuValidationSampleBytes)
            {
                break;
            }
        }

        return sample;
    }

    private static void ValidateGpuCompressedChunks(
        IReadOnlyList<PendingChunk> chunks,
        IReadOnlyList<CompressionBatchResult> results)
    {
        if (results.Count != chunks.Count)
        {
            throw new InvalidDataException("GPU batch圧縮結果の件数が入力チャンク数と一致しません。");
        }

        var validationCount = chunks.Count;
        for (var index = 0; index < validationCount; index++)
        {
            var decompressed = VerifiedDeflate.Decode(results[index].CompressedBytes, chunks[index].Bytes.Length, CancellationToken.None);
            if (!decompressed.AsSpan().SequenceEqual(chunks[index].Bytes))
            {
                throw new InvalidDataException("GPU圧縮結果の自己検証に失敗しました。CPUへフォールバックします。");
            }
        }
    }

    private ExtractPlan BuildExtractPlan(
        FastArchiveDocument document,
        string destinationRoot,
        bool overwriteExisting)
    {
        var directories = new List<string>();
        var files = new List<FastExtractFile>();
        var seenPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var directory in document.Directories)
        {
            if (!_pathSafetyValidator.TryGetSafeDestinationPath(destinationRoot, directory, out var destinationPath, out var reason))
            {
                throw new ArchiveSecurityException($"{reason} エントリ: {directory}");
            }

            if (!seenPaths.Add(destinationPath))
            {
                continue;
            }

            directories.Add(destinationPath);
        }

        foreach (var entry in document.Files)
        {
            if (!_pathSafetyValidator.TryGetSafeDestinationPath(destinationRoot, entry.EntryName, out var destinationPath, out var reason))
            {
                throw new ArchiveSecurityException($"{reason} エントリ: {entry.EntryName}");
            }

            if (!seenPaths.Add(destinationPath))
            {
                throw new InvalidDataException($"アーカイブ内パスが重複しています: {entry.EntryName}");
            }

            if (File.Exists(destinationPath) && !overwriteExisting)
            {
                throw new IOException($"展開先に既存ファイルがあります: {destinationPath}");
            }

            files.Add(new FastExtractFile(entry, destinationPath));
        }

        return new ExtractPlan(directories, files);
    }

    private static void ValidateDocumentForWrite(FastArchiveDocument document)
    {
        var entryNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var directory in document.Directories)
        {
            if (string.IsNullOrWhiteSpace(directory) || !directory.EndsWith("/", StringComparison.Ordinal))
            {
                throw new InvalidDataException($"不正なディレクトリエントリです: {directory}");
            }

            entryNames.Add(directory.TrimEnd('/'));
        }

        foreach (var file in document.Files)
        {
            if (!entryNames.Add(file.EntryName))
            {
                throw new InvalidDataException($"アーカイブ内パスが重複しています: {file.EntryName}");
            }
        }
    }

    private static void ValidateDocumentForRead(
        FastArchiveDocument document,
        long archiveLength,
        long payloadBaseOffset)
    {
        if (document.Version != FormatVersion)
        {
            throw new InvalidDataException($"未対応のZiper Fastバージョンです: {document.Version}");
        }

        var payloadLength = checked(archiveLength - payloadBaseOffset);
        if (document.Files is null || document.Directories is null || document.Files.Count + document.Directories.Count > ArchiveSafetyLimits.MaxEntries)
            throw new InvalidDataException("アーカイブ目録の件数が上限を超えています。");
        long totalOutput = 0;
        int totalChunks = 0;
        var entryNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var directory in document.Directories)
        {
            if (string.IsNullOrWhiteSpace(directory) || !directory.EndsWith("/", StringComparison.Ordinal))
            {
                throw new InvalidDataException($"不正なディレクトリエントリです: {directory}");
            }

            entryNames.Add(directory.TrimEnd('/'));
        }

        foreach (var file in document.Files)
        {
            if (string.IsNullOrWhiteSpace(file.EntryName))
            {
                throw new InvalidDataException("空のファイルエントリ名は使用できません。");
            }

            if (!entryNames.Add(file.EntryName))
            {
                throw new InvalidDataException($"アーカイブ内パスが重複しています: {file.EntryName}");
            }

            if (file.Length < 0 || file.Length > ArchiveSafetyLimits.MaxFileBytes || file.Chunks is null)
            {
                throw new InvalidDataException($"不正なファイルサイズです: {file.EntryName}");
            }

            var uncompressedTotal = 0L;
            totalOutput = checked(totalOutput + file.Length);
            totalChunks = checked(totalChunks + file.Chunks.Count);
            if (totalOutput > ArchiveSafetyLimits.MaxTotalBytes || totalChunks > 1_000_000)
                throw new InvalidDataException("展開容量またはチャンク数が安全上限を超えています。");
            foreach (var chunk in file.Chunks)
            {
                if (chunk.Method is not StoreMethod and not DeflateMethod)
                {
                    throw new InvalidDataException($"未対応のチャンク圧縮方式です: {chunk.Method}");
                }

                if (chunk.Offset < 0 || chunk.CompressedSize <= 0 || chunk.CompressedSize > 2 * 1024 * 1024 || chunk.UncompressedSize <= 0 || chunk.UncompressedSize > 1024 * 1024)
                {
                    throw new InvalidDataException($"不正なチャンクサイズです: {file.EntryName}");
                }

                if (chunk.Method == StoreMethod && chunk.CompressedSize != chunk.UncompressedSize)
                {
                    throw new InvalidDataException($"Storeチャンクのサイズが一致しません: {file.EntryName}");
                }

                var chunkEnd = checked(chunk.Offset + chunk.CompressedSize);
                if (chunkEnd > payloadLength)
                {
                    throw new InvalidDataException($"チャンクがアーカイブ範囲外を参照しています: {file.EntryName}");
                }

                uncompressedTotal = checked(uncompressedTotal + chunk.UncompressedSize);
            }

            if (uncompressedTotal != file.Length)
            {
                throw new InvalidDataException($"ファイルサイズとチャンク合計が一致しません: {file.EntryName}");
            }
        }
    }

    private static async Task<FastArchiveReadResult> ReadArchiveDocumentAsync(
        Stream archiveStream,
        CancellationToken cancellationToken)
    {
        var magic = new byte[Magic.Length];
        await ReadExactlyAsync(archiveStream, magic, cancellationToken).ConfigureAwait(false);
        if (!magic.SequenceEqual(Magic))
        {
            throw new InvalidDataException("Ziper Fast形式のアーカイブではありません。");
        }

        var fixedHeader = new byte[sizeof(int) + sizeof(long)];
        await ReadExactlyAsync(archiveStream, fixedHeader, cancellationToken).ConfigureAwait(false);
        var version = BitConverter.ToInt32(fixedHeader, 0);
        var manifestLength = BitConverter.ToInt64(fixedHeader, sizeof(int));
        if (version != FormatVersion)
        {
            throw new InvalidDataException($"未対応のZiper Fastバージョンです: {version}");
        }

        if (manifestLength <= 0 || manifestLength > MaximumManifestBytes)
        {
            throw new InvalidDataException("Ziper Fastアーカイブの目録サイズが不正です。");
        }

        var manifestBytes = new byte[checked((int)manifestLength)];
        await ReadExactlyAsync(archiveStream, manifestBytes, cancellationToken).ConfigureAwait(false);
        var document = JsonSerializer.Deserialize<FastArchiveDocument>(manifestBytes, JsonOptions)
                       ?? throw new InvalidDataException("Ziper Fastアーカイブの目録を読み込めません。");
        return new FastArchiveReadResult(document, HeaderSize + manifestLength);
    }

    private static void WriteHeader(Stream stream, long manifestLength)
    {
        stream.Write(Magic);
        stream.Write(BitConverter.GetBytes(FormatVersion));
        stream.Write(BitConverter.GetBytes(manifestLength));
    }

    private static async Task ReadExactlyAsync(
        Stream stream,
        Memory<byte> buffer,
        CancellationToken cancellationToken)
    {
        var readTotal = 0;
        while (readTotal < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer[readTotal..], cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                throw new EndOfStreamException("アーカイブの末尾に到達しました。");
            }

            readTotal += read;
        }
    }

    private static async Task CopyExactAsync(
        Stream source,
        Stream destination,
        long bytesToCopy,
        CancellationToken cancellationToken)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(BufferSize);
        try
        {
            var remaining = bytesToCopy;
            while (remaining > 0)
            {
                var read = await source.ReadAsync(
                    buffer.AsMemory(0, (int)Math.Min(buffer.Length, remaining)),
                    cancellationToken).ConfigureAwait(false);
                if (read == 0)
                {
                    throw new EndOfStreamException("アーカイブの末尾に到達しました。");
                }

                await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                remaining -= read;
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static int DetermineChunkSize(ICompressionBackend backend, ArchiveCompressionLevel compressionLevel)
    {
        if (compressionLevel == ArchiveCompressionLevel.Store)
        {
            return DefaultCpuChunkSize;
        }

        if (backend is ICompressionBatchBackend { SupportsBatchCompression: true } batchBackend &&
            backend.IsAvailable)
        {
            var maxItemBytes = batchBackend.MaxBatchItemBytes <= 0
                ? DefaultGpuChunkSize
                : Math.Min(batchBackend.MaxBatchItemBytes, int.MaxValue);
            return (int)Math.Clamp(maxItemBytes, MinimumChunkSize, DefaultCpuChunkSize);
        }

        return DefaultCpuChunkSize;
    }

    private static long DetermineMaxBatchInputBytes(ICompressionBackend backend)
    {
        var resourceLimit = ArchiveResourceLimits.GetBatchInputBytesLimit();
        if (backend is ICompressionBatchBackend { SupportsBatchCompression: true } batchBackend &&
            backend.IsAvailable)
        {
            var maxBatchBytes = batchBackend.MaxBatchInputBytes <= 0
                ? DefaultGpuBatchInputBytes
                : batchBackend.MaxBatchInputBytes;
            return Math.Clamp(
                Math.Min(maxBatchBytes, resourceLimit),
                MinimumChunkSize,
                DefaultGpuBatchInputBytes);
        }

        return resourceLimit;
    }

    private static MemoryStream OpenReadOnlyMemoryStream(ReadOnlyMemory<byte> memory)
    {
        return MemoryMarshal.TryGetArray(memory, out var segment) && segment.Array is not null
            ? new MemoryStream(segment.Array, segment.Offset, segment.Count, writable: false)
            : new MemoryStream(memory.ToArray(), writable: false);
    }

    private static uint ComputeCrc32(ReadOnlySpan<byte> bytes)
    {
        var crc32 = new Crc32();
        crc32.Update(bytes);
        return crc32.GetCurrentHash();
    }

    private static void SetLastWriteTime(string path, long utcTicks)
    {
        try
        {
            File.SetLastWriteTimeUtc(path, new DateTime(utcTicks, DateTimeKind.Utc));
        }
        catch
        {
            // Timestamp restoration is best effort.
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
            // Cleanup is best effort.
        }
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch
        {
            // Cleanup is best effort.
        }
    }

    private static string SafeEngineLabel(string selectedEngine, string backendName) =>
        string.IsNullOrWhiteSpace(selectedEngine) ? backendName : selectedEngine;

    private static string SafeFallbackReason(string fallbackReason) =>
        FallbackReasonFormatter.Format(fallbackReason);

    private static string AppendFallbackReason(string existing, string next)
    {
        if (string.IsNullOrWhiteSpace(existing) || string.Equals(existing, "None", StringComparison.OrdinalIgnoreCase) || string.Equals(existing, "なし", StringComparison.Ordinal))
        {
            return next;
        }

        if (string.IsNullOrWhiteSpace(next))
        {
            return existing;
        }

        return existing + " " + next;
    }

    private static string FormatBytes(long bytes)
    {
        string[] units = { "B", "KB", "MB", "GB", "TB" };
        var value = (double)Math.Max(bytes, 0);
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        return $"{value:N1} {units[unit]}";
    }

    private sealed class FastArchiveDocument
    {
        public string Format { get; set; } = "ZiperFast";

        public int Version { get; set; } = FormatVersion;

        public DateTimeOffset CreatedUtc { get; set; }

        public List<string> Directories { get; set; } = new();

        public List<FastFileEntry> Files { get; set; } = new();

        public int TotalChunkCount => Files.Sum(file => file.Chunks.Count);
    }

    private sealed class FastFileEntry
    {
        public string EntryName { get; set; } = string.Empty;

        public long Length { get; set; }

        public long LastWriteTimeUtcTicks { get; set; }

        public uint Crc32 { get; set; }

        public List<FastChunkEntry> Chunks { get; set; } = new();
    }

    private sealed class FastChunkEntry
    {
        public long Offset { get; set; }

        public int Method { get; set; }

        public long CompressedSize { get; set; }

        public int UncompressedSize { get; set; }

        public uint Crc32 { get; set; }
    }

    private sealed record PreparedFastFileResult(
        FastFileEntry File,
        bool UsedGpu,
        bool UsedFallback,
        string FallbackReason);

    private sealed record PendingChunk(
        int Index,
        byte[] Bytes,
        uint Crc32);

    private sealed record FastArchiveReadResult(
        FastArchiveDocument Document,
        long PayloadBaseOffset);

    private sealed record ExtractPlan(
        IReadOnlyList<string> Directories,
        IReadOnlyList<FastExtractFile> Files);

    private sealed record FastExtractFile(
        FastFileEntry Entry,
        string DestinationPath);

    private enum BatchPerformanceValidationState
    {
        Unvalidated,
        Accepted,
        Rejected
    }

    private sealed class HashingWriteStream : Stream
    {
        private readonly Stream _inner;
        private readonly Crc32 _crc32;
        private readonly Action<int> _bytesWritten;

        public HashingWriteStream(Stream inner, Crc32 crc32, Action<int> bytesWritten)
        {
            _inner = inner;
            _crc32 = crc32;
            _bytesWritten = bytesWritten;
        }

        public override bool CanRead => false;

        public override bool CanSeek => false;

        public override bool CanWrite => true;

        public override long Length => _inner.Length;

        public override long Position
        {
            get => _inner.Position;
            set => throw new NotSupportedException();
        }

        public override void Flush() => _inner.Flush();

        public override Task FlushAsync(CancellationToken cancellationToken) =>
            _inner.FlushAsync(cancellationToken);

        public override int Read(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException();

        public override long Seek(long offset, SeekOrigin origin) =>
            throw new NotSupportedException();

        public override void SetLength(long value) =>
            throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count)
        {
            _inner.Write(buffer, offset, count);
            UpdateHash(buffer.AsSpan(offset, count), count);
        }

        public override async ValueTask WriteAsync(
            ReadOnlyMemory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            await _inner.WriteAsync(buffer, cancellationToken).ConfigureAwait(false);
            UpdateHash(buffer.Span, buffer.Length);
        }

        private void UpdateHash(ReadOnlySpan<byte> data, int bytesWritten)
        {
            if (bytesWritten <= 0)
            {
                return;
            }

            _crc32.Update(data);
            _bytesWritten(bytesWritten);
        }
    }

    private sealed class BoundedReadStream : Stream
    {
        private readonly Stream _inner;

        public BoundedReadStream(Stream inner, long bytesRemaining)
        {
            _inner = inner;
            BytesRemaining = bytesRemaining;
        }

        public long BytesRemaining { get; private set; }

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            if (BytesRemaining <= 0)
            {
                return 0;
            }

            var read = _inner.Read(buffer, offset, (int)Math.Min(count, BytesRemaining));
            BytesRemaining -= read;
            return read;
        }

        public override async ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            if (BytesRemaining <= 0)
            {
                return 0;
            }

            var read = await _inner.ReadAsync(
                buffer[..(int)Math.Min(buffer.Length, BytesRemaining)],
                cancellationToken).ConfigureAwait(false);
            BytesRemaining -= read;
            return read;
        }

        public override long Seek(long offset, SeekOrigin origin) =>
            throw new NotSupportedException();

        public override void SetLength(long value) =>
            throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException();
    }
}
