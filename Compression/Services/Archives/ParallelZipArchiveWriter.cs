using System.Buffers;
using System.Diagnostics;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Text;
using EZConverter.Compression.Models;
using EZConverter.Compression.Services.Compression;

namespace EZConverter.Compression.Services.Archives;

internal sealed class ParallelZipArchiveWriter
{
    private const int BufferSize = 1024 * 1024;
    private const long MaximumInMemoryEntryBytes = 8L * 1024 * 1024;
    private const long InMemoryEntrySlackBytes = 64L * 1024;
    private const uint LocalFileHeaderSignature = 0x04034b50;
    private const uint CentralDirectoryHeaderSignature = 0x02014b50;
    private const uint EndOfCentralDirectorySignature = 0x06054b50;
    private const uint Zip64EndOfCentralDirectorySignature = 0x06064b50;
    private const uint Zip64EndOfCentralDirectoryLocatorSignature = 0x07064b50;
    private const ushort Utf8Flag = 0x0800;
    private const ushort StoreMethod = 0;
    private const ushort DeflateMethod = 8;
    private const long BatchValidationSampleBytes = 8L * 1024 * 1024;
    private readonly string _tempDirectory;

    public ParallelZipArchiveWriter()
    {
        _tempDirectory = Path.Combine(Path.GetTempPath(), "Ziper", Guid.NewGuid().ToString("N"));
    }

    public async Task<ParallelZipArchiveWriterResult> CreateAsync(
        ArchiveCreateRequest request,
        string outputPath,
        ICompressionBackend? compressionBackend,
        IProgress<ArchiveProgress> progress,
        IProgress<string> log,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(_tempDirectory);
        var compressedFiles = new PreparedZipFile?[request.SourceManifest.Files.Count];
        var maxDegreeOfParallelism = CalculateMaxDegreeOfParallelism(request.SourceManifest.Files.Count);
        var memoryBudget = new RetainedMemoryBudget(ArchiveResourceLimits.GetRetainedCompressionBytesLimit());
        var storageStats = new PreparationStorageStats();

        log.Report(compressionBackend is null
            ? $"高速化: Parallel CPU ZIP writer を使用します。並列度: {maxDegreeOfParallelism}"
            : $"高速化: {compressionBackend.Name} ZIP writer を使用します。並列度: {maxDegreeOfParallelism}");
        log.Report($"メモリ保護: 圧縮済みデータのメモリ保持上限 {FormatByteCount(memoryBudget.LimitBytes)}");

        try
        {
            var prepareStopwatch = Stopwatch.StartNew();
            var progressState = new PrepareProgressState();
            var batchPreparationResult = BatchBackendPreparationResult.None;
            if (compressionBackend is ICompressionBatchBackend { SupportsBatchCompression: true } batchBackend)
            {
                log.Report($"高速化: {batchBackend.Name} batch ZIP writer を使用します。");
                batchPreparationResult = await PrepareFilesWithBatchBackendAsync(
                    request,
                    batchBackend,
                    compressedFiles,
                    progressState,
                    progress,
                    log,
                    maxDegreeOfParallelism,
                    memoryBudget,
                    storageStats,
                    cancellationToken).ConfigureAwait(false);
            }
            else
            {
                await PrepareFilesWithPerFileBackendAsync(
                    request,
                    compressionBackend,
                    compressedFiles,
                    progressState,
                    progress,
                    maxDegreeOfParallelism,
                    memoryBudget,
                    storageStats,
                    cancellationToken).ConfigureAwait(false);
            }

            prepareStopwatch.Stop();
            log.Report($"高速化計測: prepare {request.SourceManifest.TotalFiles:N0} files / {FormatByteCount(request.SourceManifest.TotalBytes)} {prepareStopwatch.Elapsed:hh\\:mm\\:ss\\.fff}");
            log.Report(
                $"メモリ保護: 最大保持 {FormatByteCount(memoryBudget.PeakReservedBytes)} / {FormatByteCount(memoryBudget.LimitBytes)}, " +
                $"一時ファイル {storageStats.TempFileCount:N0} 件 / {FormatByteCount(storageStats.TempFileBytes)}");

            cancellationToken.ThrowIfCancellationRequested();
            var writeStopwatch = Stopwatch.StartNew();

            await using var archiveStream = new FileStream(
                outputPath,
                FileMode.CreateNew,
                FileAccess.ReadWrite,
                FileShare.None,
                BufferSize,
                FileOptions.Asynchronous | FileOptions.SequentialScan);

            var centralDirectory = new List<CentralDirectoryEntry>(
                request.SourceManifest.DirectoryEntries.Count + request.SourceManifest.Files.Count);

            foreach (var directoryEntry in request.SourceManifest.DirectoryEntries)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!directoryEntry.EndsWith("/", StringComparison.Ordinal))
                {
                    continue;
                }

                var metadata = ZipEntryMetadata.FromDirectory(directoryEntry, DateTimeOffset.Now, archiveStream.Position);
                WriteLocalHeader(archiveStream, metadata);
                centralDirectory.Add(new CentralDirectoryEntry(metadata, archiveStream.Position));
            }

            for (var index = 0; index < compressedFiles.Length; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var prepared = compressedFiles[index];
                if (prepared is null)
                {
                    throw new InvalidOperationException("内部エラー: 圧縮済みエントリが不足しています。");
                }

                try
                {
                    var metadata = prepared.Metadata with { LocalHeaderOffset = archiveStream.Position };
                    WriteLocalHeader(archiveStream, metadata);
                    if (prepared.HasCompressedBytes)
                    {
                        await archiveStream.WriteAsync(prepared.CompressedBytes, cancellationToken).ConfigureAwait(false);
                    }
                    else if (prepared.TempFilePath is not null)
                    {
                        await using var compressedStream = new FileStream(
                            prepared.TempFilePath,
                            FileMode.Open,
                            FileAccess.Read,
                            FileShare.Read,
                            BufferSize,
                            FileOptions.Asynchronous | FileOptions.SequentialScan);
                        await compressedStream.CopyToAsync(archiveStream, BufferSize, cancellationToken).ConfigureAwait(false);
                    }
                    else
                    {
                        throw new InvalidOperationException("内部エラー: 圧縮済みデータがありません。");
                    }

                    centralDirectory.Add(new CentralDirectoryEntry(metadata, archiveStream.Position));
                }
                finally
                {
                    memoryBudget.Release(prepared.RetainedMemoryBytes);
                    if (prepared.TempFilePath is not null)
                    {
                        TryDelete(prepared.TempFilePath);
                    }

                    compressedFiles[index] = null;
                }
            }

            WriteCentralDirectoryAndFooter(archiveStream, centralDirectory);
            await archiveStream.FlushAsync(cancellationToken).ConfigureAwait(false);
            writeStopwatch.Stop();
            log.Report($"高速化計測: zip write {centralDirectory.Count:N0} entries {writeStopwatch.Elapsed:hh\\:mm\\:ss\\.fff}");
            return new ParallelZipArchiveWriterResult(
                batchPreparationResult.UsedBatchBackend,
                batchPreparationResult.UsedCpuFallback,
                batchPreparationResult.FallbackReason);
        }
        catch
        {
            if (File.Exists(outputPath))
            {
                TryDelete(outputPath);
            }

            throw;
        }
        finally
        {
            foreach (var prepared in compressedFiles)
            {
                if (prepared is not null)
                {
                    if (prepared.TempFilePath is not null)
                    {
                        TryDelete(prepared.TempFilePath);
                    }

                    memoryBudget.Release(prepared.RetainedMemoryBytes);
                }
            }

            TryDeleteDirectory(_tempDirectory);
        }
    }

    private async Task<PreparedZipFile> PrepareFileAsync(
        ArchiveSourceFile sourceFile,
        ArchiveCompressionLevel compressionLevel,
        ICompressionBackend? compressionBackend,
        RetainedMemoryBudget memoryBudget,
        Action<int> bytesRead,
        CancellationToken cancellationToken)
    {
        var crc32 = new Crc32();
        var method = ZipCompressionMethodSelector.ShouldStoreForSpeed(sourceFile, compressionLevel)
            ? StoreMethod
            : DeflateMethod;
        var memoryReservationBytes = CalculateInMemoryReservation(sourceFile.Length);
        var useMemoryBuffer = memoryReservationBytes > 0 && memoryBudget.TryReserve(memoryReservationBytes);
        var createdTempFilePath = useMemoryBuffer
            ? null
            : Path.Combine(_tempDirectory, Guid.NewGuid().ToString("N") + ".deflate");

        try
        {
            await using var sourceStream = new FileStream(
                sourceFile.FullPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                BufferSize,
                FileOptions.Asynchronous | FileOptions.SequentialScan);

            await using Stream destinationOwner = useMemoryBuffer
                ? new MemoryStream(checked((int)memoryReservationBytes))
                : new FileStream(
                    createdTempFilePath!,
                    FileMode.CreateNew,
                    FileAccess.ReadWrite,
                    FileShare.None,
                    BufferSize,
                    FileOptions.Asynchronous | FileOptions.SequentialScan);

            if (method == StoreMethod)
            {
                await CopyAndHashAsync(sourceStream, destinationOwner, crc32, bytesRead, cancellationToken).ConfigureAwait(false);
            }
            else if (compressionBackend is not null)
            {
                await using var hashingSourceStream = new HashingProgressReadStream(sourceStream, crc32, bytesRead);
                await compressionBackend.CompressAsync(
                    hashingSourceStream,
                    destinationOwner,
                    new CompressionJobOptions(compressionLevel),
                    cancellationToken).ConfigureAwait(false);
            }
            else
            {
                using var deflateStream = new DeflateStream(
                    destinationOwner,
                    CompressionLevelMapper.ToSystemCompressionLevel(compressionLevel),
                    leaveOpen: true);
                await CopyAndHashAsync(sourceStream, deflateStream, crc32, bytesRead, cancellationToken).ConfigureAwait(false);
            }

            await destinationOwner.FlushAsync(cancellationToken).ConfigureAwait(false);
            ReadOnlyMemory<byte> compressedBytes = ReadOnlyMemory<byte>.Empty;
            string? tempFilePath = null;
            long compressedLength;
            if (destinationOwner is MemoryStream memoryStream)
            {
                if (!memoryStream.TryGetBuffer(out var segment) || segment.Array is null)
                {
                    throw new InvalidOperationException("内部エラー: メモリ圧縮バッファーを取得できません。");
                }

                compressedLength = memoryStream.Length;
                compressedBytes = new ReadOnlyMemory<byte>(
                    segment.Array,
                    segment.Offset,
                    checked((int)compressedLength));
            }
            else if (destinationOwner is FileStream fileStream)
            {
                tempFilePath = fileStream.Name;
                compressedLength = fileStream.Length;
            }
            else
            {
                throw new InvalidOperationException("内部エラー: 未対応の圧縮出力先です。");
            }

            var metadata = ZipEntryMetadata.FromFile(
                sourceFile,
                method,
                crc32.GetCurrentHash(),
                compressedLength,
                localHeaderOffset: 0);

            return new PreparedZipFile(
                tempFilePath,
                compressedBytes,
                useMemoryBuffer,
                useMemoryBuffer ? memoryReservationBytes : 0,
                metadata);
        }
        catch
        {
            if (useMemoryBuffer)
            {
                memoryBudget.Release(memoryReservationBytes);
            }

            if (createdTempFilePath is not null)
            {
                TryDelete(createdTempFilePath);
            }

            throw;
        }
    }

    private static int CalculateMaxDegreeOfParallelism(int fileCount)
    {
        if (fileCount <= 1)
        {
            return 1;
        }

        return ArchiveResourceLimits.GetResponsiveParallelism(fileCount);
    }

    private static long CalculateInMemoryReservation(long sourceLength)
    {
        if (sourceLength < 0 || sourceLength > MaximumInMemoryEntryBytes)
        {
            return 0;
        }

        return Math.Max(4 * 1024, checked(sourceLength + InMemoryEntrySlackBytes));
    }

    private async Task PrepareFilesWithPerFileBackendAsync(
        ArchiveCreateRequest request,
        ICompressionBackend? compressionBackend,
        PreparedZipFile?[] compressedFiles,
        PrepareProgressState progressState,
        IProgress<ArchiveProgress> progress,
        int maxDegreeOfParallelism,
        RetainedMemoryBudget memoryBudget,
        PreparationStorageStats storageStats,
        CancellationToken cancellationToken)
    {
        var indexedFiles = request.SourceManifest.Files
            .Select((file, index) => (file, index))
            .ToArray();

        await Parallel.ForEachAsync(
            indexedFiles,
            new ParallelOptions
            {
                CancellationToken = cancellationToken,
                MaxDegreeOfParallelism = maxDegreeOfParallelism
            },
            async (item, token) =>
            {
                var prepared = await PrepareFileAsync(
                    item.file,
                    request.CompressionLevel,
                    compressionBackend,
                    memoryBudget,
                    bytes => ReportBytesRead(request, progressState, progress, item.file.EntryName, "並列圧縮中", bytes),
                    token).ConfigureAwait(false);

                compressedFiles[item.index] = prepared;
                storageStats.Record(prepared);
                ReportFilePrepared(request, progressState, progress, item.file.EntryName, "並列圧縮済み");
            }).ConfigureAwait(false);
    }

    private async Task<BatchBackendPreparationResult> PrepareFilesWithBatchBackendAsync(
        ArchiveCreateRequest request,
        ICompressionBatchBackend batchBackend,
        PreparedZipFile?[] compressedFiles,
        PrepareProgressState progressState,
        IProgress<ArchiveProgress> progress,
        IProgress<string> log,
        int maxDegreeOfParallelism,
        RetainedMemoryBudget memoryBudget,
        PreparationStorageStats storageStats,
        CancellationToken cancellationToken)
    {
        var backendBatchInputBytes = batchBackend.MaxBatchInputBytes <= 0
            ? 512L * 1024 * 1024
            : batchBackend.MaxBatchInputBytes;
        var maxBatchInputBytes = Math.Min(
            backendBatchInputBytes,
            ArchiveResourceLimits.GetBatchInputBytesLimit());
        var maxBatchItemBytes = batchBackend.MaxBatchItemBytes <= 0
            ? long.MaxValue
            : batchBackend.MaxBatchItemBytes;
        var minimumUsefulBatchBytes = Math.Max(1, batchBackend.MinimumUsefulBatchBytes);
        var minimumSpeedupOverCpu = batchBackend.MinimumSpeedupOverCpu <= 0
            ? 1.0
            : batchBackend.MinimumSpeedupOverCpu;
        var pending = new List<PendingBatchCandidate>();
        var pendingBytes = 0L;
        var usedBatchBackend = false;
        var usedCpuFallback = false;
        var fallbackReason = string.Empty;
        var validationState = BatchPerformanceValidationState.Unvalidated;

        log.Report(
            $"メモリ保護: GPU batch入力上限 {FormatByteCount(maxBatchInputBytes)} " +
            $"(backend上限 {FormatByteCount(backendBatchInputBytes)})");

        foreach (var sourceFile in request.SourceManifest.Files.Select((file, index) => (file, index)))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var method = ZipCompressionMethodSelector.ShouldStoreForSpeed(sourceFile.file, request.CompressionLevel)
                ? StoreMethod
                : DeflateMethod;

            if (method == StoreMethod ||
                sourceFile.file.Length == 0 ||
                sourceFile.file.Length > maxBatchItemBytes ||
                sourceFile.file.Length > maxBatchInputBytes)
            {
                await FlushBatchAsync().ConfigureAwait(false);
                var prepared = await PrepareFileAsync(
                    sourceFile.file,
                    request.CompressionLevel,
                    null,
                    memoryBudget,
                    bytes => ReportBytesRead(request, progressState, progress, sourceFile.file.EntryName, "並列圧縮中", bytes),
                    cancellationToken).ConfigureAwait(false);

                compressedFiles[sourceFile.index] = prepared;
                storageStats.Record(prepared);
                ReportFilePrepared(request, progressState, progress, sourceFile.file.EntryName, "並列圧縮済み");
                if (method == DeflateMethod &&
                    (sourceFile.file.Length > maxBatchItemBytes || sourceFile.file.Length > maxBatchInputBytes))
                {
                    usedCpuFallback = true;
                    var applicableLimit = Math.Min(maxBatchItemBytes, maxBatchInputBytes);
                    AppendFallbackReason($"GPU batchフォールバック: {sourceFile.file.EntryName} はGPU batch上限 {FormatByteCount(applicableLimit)} を超えたためCPUで処理しました。");
                    log.Report(fallbackReason);
                }

                continue;
            }

            if (pendingBytes > 0 && pendingBytes + sourceFile.file.Length > maxBatchInputBytes)
            {
                await FlushBatchAsync().ConfigureAwait(false);
            }

            pending.Add(new PendingBatchCandidate(sourceFile.index, sourceFile.file));
            pendingBytes += sourceFile.file.Length;
        }

        await FlushBatchAsync().ConfigureAwait(false);
        return new BatchBackendPreparationResult(usedBatchBackend, usedCpuFallback, fallbackReason);

        async Task FlushBatchAsync()
        {
            if (pending.Count == 0)
            {
                return;
            }

            cancellationToken.ThrowIfCancellationRequested();
            var pendingCandidates = pending.ToArray();
            var readStopwatch = Stopwatch.StartNew();
            var batchFiles = await ReadFilesForBatchAsync(
                pendingCandidates,
                request,
                progressState,
                progress,
                maxDegreeOfParallelism,
                cancellationToken).ConfigureAwait(false);
            readStopwatch.Stop();
            var batchInputBytes = batchFiles.Sum(file => file.SourceFile.Length);
            log.Report($"GPU batch計測: read+crc {batchFiles.Length:N0} files / {FormatByteCount(batchInputBytes)} {readStopwatch.Elapsed:hh\\:mm\\:ss\\.fff}");

            if (batchInputBytes < minimumUsefulBatchBytes)
            {
                await UseCpuForBatchAsync(
                    batchFiles,
                    $"GPU batchフォールバック: batchサイズ {FormatByteCount(batchInputBytes)} が有効しきい値 {FormatByteCount(minimumUsefulBatchBytes)} 未満です。",
                    "CPU圧縮済み").ConfigureAwait(false);
                pending.Clear();
                pendingBytes = 0;
                return;
            }

            if (validationState == BatchPerformanceValidationState.Rejected)
            {
                await UseCpuForBatchAsync(
                    batchFiles,
                    "GPU batchフォールバック: 前回のGPU batchがCPUより低速だったため、残りのbatchをCPUで処理します。",
                    "CPU圧縮済み").ConfigureAwait(false);
                pending.Clear();
                pendingBytes = 0;
                return;
            }

            if (batchBackend.RequiresCpuPerformanceValidation &&
                validationState == BatchPerformanceValidationState.Unvalidated)
            {
                var validationSampleFiles = TakeValidationSample(batchFiles);
                var remainingFiles = batchFiles
                    .Skip(validationSampleFiles.Length)
                    .ToArray();
                var validationSampleBytes = validationSampleFiles.Sum(file => file.SourceFile.Length);
                var (gpuSampleResults, gpuElapsed) = await CompressBatchWithGpuAsync(validationSampleFiles).ConfigureAwait(false);
                var cpuStopwatch = Stopwatch.StartNew();
                var cpuSampleResults = await CompressBatchWithCpuAsync(
                    validationSampleFiles,
                    request.CompressionLevel,
                    maxDegreeOfParallelism,
                    cancellationToken).ConfigureAwait(false);
                cpuStopwatch.Stop();

                var speedup = gpuElapsed.TotalSeconds <= 0
                    ? double.PositiveInfinity
                    : cpuStopwatch.Elapsed.TotalSeconds / gpuElapsed.TotalSeconds;
                log.Report($"GPU batch検証: sample {validationSampleFiles.Length:N0} files / {FormatByteCount(validationSampleBytes)}, GPU {gpuElapsed:hh\\:mm\\:ss\\.fff}, CPU {cpuStopwatch.Elapsed:hh\\:mm\\:ss\\.fff}, speedup {speedup:N2}x");
                if (speedup < minimumSpeedupOverCpu)
                {
                    validationState = BatchPerformanceValidationState.Rejected;
                    await CommitBatchResultsAsync(validationSampleFiles, cpuSampleResults, "CPU圧縮済み").ConfigureAwait(false);
                    if (remainingFiles.Length > 0)
                {
                    var remainingCpuStopwatch = Stopwatch.StartNew();
                    var remainingCpuResults = await CompressBatchWithCpuAsync(
                        remainingFiles,
                        request.CompressionLevel,
                        maxDegreeOfParallelism,
                        cancellationToken).ConfigureAwait(false);
                    remainingCpuStopwatch.Stop();
                    log.Report($"GPU batch計測: CPUフォールバックで残り {remainingFiles.Length:N0} 件 / {FormatByteCount(remainingFiles.Sum(file => file.SourceFile.Length))} を圧縮しました。{remainingCpuStopwatch.Elapsed:hh\\:mm\\:ss\\.fff}");
                    await CommitBatchResultsAsync(remainingFiles, remainingCpuResults, "CPU圧縮済み").ConfigureAwait(false);
                }

                    usedCpuFallback = true;
                    AppendFallbackReason($"GPU batchフォールバック: サンプルGPU speedup {speedup:N2}x が必要値 {minimumSpeedupOverCpu:N2}x を下回ったため、CPU出力を使用しました。");
                    log.Report(fallbackReason);
                    pending.Clear();
                    pendingBytes = 0;
                    return;
                }

                validationState = BatchPerformanceValidationState.Accepted;
                await CommitBatchResultsAsync(validationSampleFiles, gpuSampleResults, "GPU batch圧縮済み").ConfigureAwait(false);
                usedBatchBackend = true;
                if (remainingFiles.Length > 0)
                {
                    var (remainingGpuResults, _) = await CompressBatchWithGpuAsync(remainingFiles).ConfigureAwait(false);
                    await CommitBatchResultsAsync(remainingFiles, remainingGpuResults, "GPU batch圧縮済み").ConfigureAwait(false);
                }

                pending.Clear();
                pendingBytes = 0;
                return;
            }

            var (batchResults, _) = await CompressBatchWithGpuAsync(batchFiles).ConfigureAwait(false);
            await CommitBatchResultsAsync(batchFiles, batchResults, "GPU batch圧縮済み").ConfigureAwait(false);
            usedBatchBackend = true;

            pending.Clear();
            pendingBytes = 0;
        }

        PendingBatchFile[] TakeValidationSample(PendingBatchFile[] batchFiles)
        {
            var sampleBytes = 0L;
            var sampleCount = 0;
            while (sampleCount < batchFiles.Length &&
                   (sampleCount == 0 || sampleBytes < BatchValidationSampleBytes))
            {
                sampleBytes += batchFiles[sampleCount].SourceFile.Length;
                sampleCount++;
            }

            return batchFiles.Take(sampleCount).ToArray();
        }

        async Task<(IReadOnlyList<CompressionBatchResult> Results, TimeSpan Elapsed)> CompressBatchWithGpuAsync(
            PendingBatchFile[] batchFiles)
        {
            var batchItems = batchFiles
                .Select(item => new CompressionBatchItem(item.SourceFile.EntryName, item.InputBytes))
                .ToArray();
            var stopwatch = Stopwatch.StartNew();
            var results = await batchBackend.CompressBatchAsync(
                batchItems,
                new CompressionJobOptions(request.CompressionLevel),
                cancellationToken).ConfigureAwait(false);
            stopwatch.Stop();
            log.Report($"GPU batch計測: compress {batchFiles.Length:N0} files / {FormatByteCount(batchFiles.Sum(file => file.SourceFile.Length))} -> {FormatByteCount(results.Sum(result => (long)result.CompressedBytes.Length))} {stopwatch.Elapsed:hh\\:mm\\:ss\\.fff}");
            return (results, stopwatch.Elapsed);
        }

        async Task UseCpuForBatchAsync(PendingBatchFile[] batchFiles, string reason, string stage)
        {
            usedCpuFallback = true;
            AppendFallbackReason(reason);
            log.Report(reason);
            var cpuStopwatch = Stopwatch.StartNew();
            var cpuResults = await CompressBatchWithCpuAsync(
                batchFiles,
                request.CompressionLevel,
                maxDegreeOfParallelism,
                cancellationToken).ConfigureAwait(false);
            cpuStopwatch.Stop();
            log.Report($"GPU batch計測: CPUフォールバックで {batchFiles.Length:N0} 件 / {FormatByteCount(batchFiles.Sum(file => file.SourceFile.Length))} を圧縮しました。{cpuStopwatch.Elapsed:hh\\:mm\\:ss\\.fff}");
            await CommitBatchResultsAsync(batchFiles, cpuResults, stage).ConfigureAwait(false);
        }

        async Task CommitBatchResultsAsync(
            PendingBatchFile[] batchFiles,
            IReadOnlyList<CompressionBatchResult> batchResults,
            string stage)
        {
            if (batchResults.Count != batchFiles.Length)
            {
                throw new InvalidOperationException("Batch compression returned an unexpected result count.");
            }

            var commitStopwatch = Stopwatch.StartNew();
            for (var index = 0; index < batchFiles.Length; index++)
            {
                var pendingFile = batchFiles[index];
                var prepared = await CreatePreparedDeflateFileAsync(
                    pendingFile,
                    batchResults[index].CompressedBytes,
                    memoryBudget,
                    cancellationToken).ConfigureAwait(false);
                compressedFiles[pendingFile.Index] = prepared;
                storageStats.Record(prepared);
                ReportFilePrepared(request, progressState, progress, pendingFile.SourceFile.EntryName, stage);
            }

            commitStopwatch.Stop();
            log.Report($"GPU batch計測: commit {batchFiles.Length:N0} files / {FormatByteCount(batchResults.Sum(result => (long)result.CompressedBytes.Length))} {commitStopwatch.Elapsed:hh\\:mm\\:ss\\.fff}");
        }

        void AppendFallbackReason(string reason)
        {
            fallbackReason = string.IsNullOrWhiteSpace(fallbackReason)
                ? reason
                : $"{fallbackReason}; {reason}";
        }
    }

    private static async Task<IReadOnlyList<CompressionBatchResult>> CompressBatchWithCpuAsync(
        IReadOnlyList<PendingBatchFile> batchFiles,
        ArchiveCompressionLevel compressionLevel,
        int maxDegreeOfParallelism,
        CancellationToken cancellationToken)
    {
        var results = new CompressionBatchResult?[batchFiles.Count];
        await Parallel.ForEachAsync(
            batchFiles.Select((file, index) => (file, index)),
            new ParallelOptions
            {
                CancellationToken = cancellationToken,
                MaxDegreeOfParallelism = maxDegreeOfParallelism
            },
            async (item, token) =>
            {
                using var input = OpenReadOnlyMemoryStream(item.file.InputBytes);
                using var output = new MemoryStream();
                using (var deflateStream = new DeflateStream(
                           output,
                           CompressionLevelMapper.ToSystemCompressionLevel(compressionLevel),
                           leaveOpen: true))
                {
                    await input.CopyToAsync(deflateStream, token).ConfigureAwait(false);
                }

                results[item.index] = new CompressionBatchResult(output.ToArray());
            }).ConfigureAwait(false);

        return results
            .Select(result => result ?? throw new InvalidOperationException("CPU batch compression returned an empty result."))
            .ToArray();
    }

    private static async Task<PendingBatchFile[]> ReadFilesForBatchAsync(
        IReadOnlyList<PendingBatchCandidate> pendingCandidates,
        ArchiveCreateRequest request,
        PrepareProgressState progressState,
        IProgress<ArchiveProgress> progress,
        int maxDegreeOfParallelism,
        CancellationToken cancellationToken)
    {
        var totalBytes = checked(pendingCandidates.Sum(candidate => candidate.SourceFile.Length));
        if (totalBytes > int.MaxValue)
        {
            throw new InvalidOperationException($"GPU batch input is too large for one managed staging buffer: {FormatByteCount(totalBytes)}.");
        }

        var inputBuffer = new byte[checked((int)totalBytes)];
        var offsets = new int[pendingCandidates.Count];
        var offset = 0;
        for (var index = 0; index < pendingCandidates.Count; index++)
        {
            var length = pendingCandidates[index].SourceFile.Length;
            if (length > int.MaxValue)
            {
                throw new InvalidOperationException($"GPU batch item is too large for one managed staging buffer: {pendingCandidates[index].SourceFile.EntryName}");
            }

            offsets[index] = offset;
            offset = checked(offset + (int)length);
        }

        var pendingFiles = new PendingBatchFile?[pendingCandidates.Count];
        await Parallel.ForEachAsync(
            pendingCandidates.Select((candidate, index) => (candidate, index)),
            new ParallelOptions
            {
                CancellationToken = cancellationToken,
                MaxDegreeOfParallelism = maxDegreeOfParallelism
            },
            async (item, token) =>
            {
                var destination = inputBuffer.AsMemory(offsets[item.index], checked((int)item.candidate.SourceFile.Length));
                pendingFiles[item.index] = await ReadFileForBatchAsync(
                    item.candidate.Index,
                    item.candidate.SourceFile,
                    destination,
                    bytes => ReportBytesRead(
                        request,
                        progressState,
                        progress,
                        item.candidate.SourceFile.EntryName,
                        "GPU batch読込中",
                        bytes),
                    token).ConfigureAwait(false);
            }).ConfigureAwait(false);

        return pendingFiles
            .Select(file => file ?? throw new InvalidOperationException("GPU batch input preparation failed."))
            .ToArray();
    }

    private static async Task<PendingBatchFile> ReadFileForBatchAsync(
        int index,
        ArchiveSourceFile sourceFile,
        Memory<byte> destination,
        Action<int> bytesRead,
        CancellationToken cancellationToken)
    {
        var crc32 = new Crc32();
        await using var sourceStream = new FileStream(
            sourceFile.FullPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            BufferSize,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        var written = 0;
        while (written < destination.Length)
        {
            var read = await sourceStream.ReadAsync(destination[written..], cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            var memory = destination.Slice(written, read);
            crc32.Update(memory.Span);
            bytesRead(read);
            written += read;
        }

        if (written != destination.Length)
        {
            throw new EndOfStreamException($"Input file ended while preparing GPU batch input: {sourceFile.FullPath}");
        }

        return new PendingBatchFile(index, sourceFile, crc32.GetCurrentHash(), destination);
    }

    private static MemoryStream OpenReadOnlyMemoryStream(ReadOnlyMemory<byte> memory)
    {
        return MemoryMarshal.TryGetArray(memory, out var segment) && segment.Array is not null
            ? new MemoryStream(segment.Array, segment.Offset, segment.Count, writable: false)
            : new MemoryStream(memory.ToArray(), writable: false);
    }

    private async Task<PreparedZipFile> CreatePreparedDeflateFileAsync(
        PendingBatchFile pendingFile,
        ReadOnlyMemory<byte> compressedBytes,
        RetainedMemoryBudget memoryBudget,
        CancellationToken cancellationToken)
    {
        string? tempFilePath = null;
        ReadOnlyMemory<byte> inMemoryBytes = ReadOnlyMemory<byte>.Empty;
        var retainedMemoryBytes = 0L;
        var canRetainInMemory = compressedBytes.Length <= MaximumInMemoryEntryBytes &&
                                memoryBudget.TryReserve(compressedBytes.Length);
        if (canRetainInMemory)
        {
            retainedMemoryBytes = compressedBytes.Length;
            if (MemoryMarshal.TryGetArray(compressedBytes, out var segment) &&
                segment.Array is not null &&
                segment.Offset == 0 &&
                segment.Count == segment.Array.Length)
            {
                inMemoryBytes = segment.Array;
            }
            else
            {
                try
                {
                    inMemoryBytes = compressedBytes.ToArray();
                }
                catch
                {
                    memoryBudget.Release(retainedMemoryBytes);
                    throw;
                }
            }
        }
        else
        {
            tempFilePath = Path.Combine(_tempDirectory, Guid.NewGuid().ToString("N") + ".deflate");
            await WriteAllBytesAsync(tempFilePath, compressedBytes, cancellationToken).ConfigureAwait(false);
        }

        var metadata = ZipEntryMetadata.FromFile(
            pendingFile.SourceFile,
            DeflateMethod,
            pendingFile.Crc32,
            compressedBytes.Length,
            localHeaderOffset: 0);

        return new PreparedZipFile(
            tempFilePath,
            inMemoryBytes,
            canRetainInMemory,
            retainedMemoryBytes,
            metadata);
    }

    private static async Task WriteAllBytesAsync(
        string path,
        ReadOnlyMemory<byte> bytes,
        CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            path,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            BufferSize,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
    }

    private static void ReportBytesRead(
        ArchiveCreateRequest request,
        PrepareProgressState progressState,
        IProgress<ArchiveProgress> progress,
        string entryName,
        string stage,
        int bytes)
    {
        var total = Interlocked.Add(ref progressState.ProcessedBytes, bytes);
        progress.Report(new ArchiveProgress(
            request.SourceManifest.TotalBytes,
            total,
            request.SourceManifest.TotalFiles,
            Volatile.Read(ref progressState.ProcessedFiles),
            entryName,
            stage));
    }

    private static void ReportFilePrepared(
        ArchiveCreateRequest request,
        PrepareProgressState progressState,
        IProgress<ArchiveProgress> progress,
        string entryName,
        string stage)
    {
        var files = Interlocked.Increment(ref progressState.ProcessedFiles);
        progress.Report(new ArchiveProgress(
            request.SourceManifest.TotalBytes,
            Volatile.Read(ref progressState.ProcessedBytes),
            request.SourceManifest.TotalFiles,
            files,
            entryName,
            stage));
    }

    private static async Task CopyAndHashAsync(
        Stream source,
        Stream destination,
        Crc32 crc32,
        Action<int> bytesRead,
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

                var memory = buffer.AsMemory(0, read);
                crc32.Update(memory.Span);
                await destination.WriteAsync(memory, cancellationToken).ConfigureAwait(false);
                bytesRead(read);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static void WriteLocalHeader(Stream stream, ZipEntryMetadata metadata)
    {
        var nameBytes = Encoding.UTF8.GetBytes(metadata.EntryName);
        var needsZip64Sizes = metadata.UncompressedSize > uint.MaxValue || metadata.CompressedSize > uint.MaxValue;
        var extra = needsZip64Sizes
            ? CreateZip64Extra(metadata.UncompressedSize, metadata.CompressedSize, null)
            : Array.Empty<byte>();

        using var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true);
        writer.Write(LocalFileHeaderSignature);
        writer.Write((ushort)(needsZip64Sizes ? 45 : 20));
        writer.Write(Utf8Flag);
        writer.Write(metadata.Method);
        writer.Write(metadata.DosTime);
        writer.Write(metadata.DosDate);
        writer.Write(metadata.Crc32);
        writer.Write(needsZip64Sizes ? uint.MaxValue : (uint)metadata.CompressedSize);
        writer.Write(needsZip64Sizes ? uint.MaxValue : (uint)metadata.UncompressedSize);
        writer.Write((ushort)nameBytes.Length);
        writer.Write((ushort)extra.Length);
        writer.Write(nameBytes);
        writer.Write(extra);
    }

    private static void WriteCentralDirectoryAndFooter(Stream stream, IReadOnlyList<CentralDirectoryEntry> entries)
    {
        var centralDirectoryOffset = stream.Position;
        foreach (var entry in entries)
        {
            WriteCentralDirectoryHeader(stream, entry.Metadata);
        }

        var centralDirectorySize = stream.Position - centralDirectoryOffset;
        var needsZip64 = entries.Count > ushort.MaxValue ||
                         centralDirectoryOffset > uint.MaxValue ||
                         centralDirectorySize > uint.MaxValue ||
                         entries.Any(entry => entry.Metadata.RequiresZip64CentralDirectory);

        using var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true);
        if (needsZip64)
        {
            var zip64EocdOffset = stream.Position;
            writer.Write(Zip64EndOfCentralDirectorySignature);
            writer.Write(44L);
            writer.Write((ushort)45);
            writer.Write((ushort)45);
            writer.Write(0U);
            writer.Write(0U);
            writer.Write((ulong)entries.Count);
            writer.Write((ulong)entries.Count);
            writer.Write((ulong)centralDirectorySize);
            writer.Write((ulong)centralDirectoryOffset);

            writer.Write(Zip64EndOfCentralDirectoryLocatorSignature);
            writer.Write(0U);
            writer.Write((ulong)zip64EocdOffset);
            writer.Write(1U);
        }

        writer.Write(EndOfCentralDirectorySignature);
        writer.Write((ushort)0);
        writer.Write((ushort)0);
        writer.Write(entries.Count > ushort.MaxValue ? ushort.MaxValue : (ushort)entries.Count);
        writer.Write(entries.Count > ushort.MaxValue ? ushort.MaxValue : (ushort)entries.Count);
        writer.Write(centralDirectorySize > uint.MaxValue ? uint.MaxValue : (uint)centralDirectorySize);
        writer.Write(centralDirectoryOffset > uint.MaxValue ? uint.MaxValue : (uint)centralDirectoryOffset);
        writer.Write((ushort)0);
    }

    private static void WriteCentralDirectoryHeader(Stream stream, ZipEntryMetadata metadata)
    {
        var nameBytes = Encoding.UTF8.GetBytes(metadata.EntryName);
        var needsZip64Uncompressed = metadata.UncompressedSize > uint.MaxValue;
        var needsZip64Compressed = metadata.CompressedSize > uint.MaxValue;
        var needsZip64Offset = metadata.LocalHeaderOffset > uint.MaxValue;
        var needsZip64 = needsZip64Uncompressed || needsZip64Compressed || needsZip64Offset;
        var extra = needsZip64
            ? CreateZip64Extra(
                needsZip64Uncompressed ? metadata.UncompressedSize : null,
                needsZip64Compressed ? metadata.CompressedSize : null,
                needsZip64Offset ? metadata.LocalHeaderOffset : null)
            : Array.Empty<byte>();

        using var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true);
        writer.Write(CentralDirectoryHeaderSignature);
        writer.Write((ushort)45);
        writer.Write((ushort)(needsZip64 ? 45 : 20));
        writer.Write(Utf8Flag);
        writer.Write(metadata.Method);
        writer.Write(metadata.DosTime);
        writer.Write(metadata.DosDate);
        writer.Write(metadata.Crc32);
        writer.Write(needsZip64Compressed ? uint.MaxValue : (uint)metadata.CompressedSize);
        writer.Write(needsZip64Uncompressed ? uint.MaxValue : (uint)metadata.UncompressedSize);
        writer.Write((ushort)nameBytes.Length);
        writer.Write((ushort)extra.Length);
        writer.Write((ushort)0);
        writer.Write((ushort)0);
        writer.Write((ushort)0);
        writer.Write(metadata.ExternalAttributes);
        writer.Write(needsZip64Offset ? uint.MaxValue : (uint)metadata.LocalHeaderOffset);
        writer.Write(nameBytes);
        writer.Write(extra);
    }

    private static byte[] CreateZip64Extra(long? uncompressedSize, long? compressedSize, long? localHeaderOffset)
    {
        using var memory = new MemoryStream();
        using var writer = new BinaryWriter(memory, Encoding.UTF8, leaveOpen: true);
        writer.Write((ushort)0x0001);
        var dataLength = (uncompressedSize.HasValue ? 8 : 0) +
                         (compressedSize.HasValue ? 8 : 0) +
                         (localHeaderOffset.HasValue ? 8 : 0);
        writer.Write((ushort)dataLength);

        if (uncompressedSize.HasValue)
        {
            writer.Write((ulong)uncompressedSize.Value);
        }

        if (compressedSize.HasValue)
        {
            writer.Write((ulong)compressedSize.Value);
        }

        if (localHeaderOffset.HasValue)
        {
            writer.Write((ulong)localHeaderOffset.Value);
        }

        return memory.ToArray();
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
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

    private static string FormatByteCount(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        var value = (double)Math.Max(0, bytes);
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        return $"{value:N1} {units[unit]}";
    }

    private sealed record PreparedZipFile(
        string? TempFilePath,
        ReadOnlyMemory<byte> CompressedBytes,
        bool HasCompressedBytes,
        long RetainedMemoryBytes,
        ZipEntryMetadata Metadata);

    private sealed record CentralDirectoryEntry(ZipEntryMetadata Metadata, long EndOffset);

    internal sealed record ParallelZipArchiveWriterResult(
        bool UsedBatchBackend,
        bool UsedCpuFallback,
        string FallbackReason);

    private sealed record BatchBackendPreparationResult(
        bool UsedBatchBackend,
        bool UsedCpuFallback,
        string FallbackReason)
    {
        public static BatchBackendPreparationResult None { get; } = new(false, false, string.Empty);
    }

    private sealed record PendingBatchFile(
        int Index,
        ArchiveSourceFile SourceFile,
        uint Crc32,
        ReadOnlyMemory<byte> InputBytes);

    private sealed record PendingBatchCandidate(
        int Index,
        ArchiveSourceFile SourceFile);

    private enum BatchPerformanceValidationState
    {
        Unvalidated,
        Accepted,
        Rejected
    }

    private sealed class PrepareProgressState
    {
        public long ProcessedBytes;

        public int ProcessedFiles;
    }

    private sealed class PreparationStorageStats
    {
        private long _tempFileBytes;
        private int _tempFileCount;

        public long TempFileBytes => Volatile.Read(ref _tempFileBytes);

        public int TempFileCount => Volatile.Read(ref _tempFileCount);

        public void Record(PreparedZipFile prepared)
        {
            if (prepared.TempFilePath is null)
            {
                return;
            }

            Interlocked.Increment(ref _tempFileCount);
            Interlocked.Add(ref _tempFileBytes, prepared.Metadata.CompressedSize);
        }
    }

    private sealed class HashingProgressReadStream : Stream
    {
        private readonly Stream _inner;
        private readonly Crc32 _crc32;
        private readonly Action<int> _bytesRead;

        public HashingProgressReadStream(Stream inner, Crc32 crc32, Action<int> bytesRead)
        {
            _inner = inner;
            _crc32 = crc32;
            _bytesRead = bytesRead;
        }

        public override bool CanRead => _inner.CanRead;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => _inner.Length;

        public override long Position
        {
            get => _inner.Position;
            set => throw new NotSupportedException();
        }

        public override void Flush() => _inner.Flush();

        public override int Read(byte[] buffer, int offset, int count)
        {
            var read = _inner.Read(buffer, offset, count);
            UpdateHash(buffer.AsSpan(offset, read), read);
            return read;
        }

        public override int Read(Span<byte> buffer)
        {
            var read = _inner.Read(buffer);
            UpdateHash(buffer[..read], read);
            return read;
        }

        public override async ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            var read = await _inner.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            UpdateHash(buffer.Span[..read], read);
            return read;
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        private void UpdateHash(ReadOnlySpan<byte> data, int read)
        {
            if (read <= 0)
            {
                return;
            }

            _crc32.Update(data);
            _bytesRead(read);
        }
    }

    private sealed record ZipEntryMetadata(
        string EntryName,
        ushort Method,
        uint Crc32,
        long CompressedSize,
        long UncompressedSize,
        ushort DosTime,
        ushort DosDate,
        uint ExternalAttributes,
        long LocalHeaderOffset)
    {
        public bool RequiresZip64CentralDirectory =>
            UncompressedSize > uint.MaxValue ||
            CompressedSize > uint.MaxValue ||
            LocalHeaderOffset > uint.MaxValue;

        public static ZipEntryMetadata FromFile(
            ArchiveSourceFile sourceFile,
            ushort method,
            uint crc32,
            long compressedSize,
            long localHeaderOffset)
        {
            var (dosTime, dosDate) = ToDosDateTime(sourceFile.LastWriteTime);
            return new ZipEntryMetadata(
                sourceFile.EntryName,
                method,
                crc32,
                compressedSize,
                sourceFile.Length,
                dosTime,
                dosDate,
                0,
                localHeaderOffset);
        }

        public static ZipEntryMetadata FromDirectory(string entryName, DateTimeOffset lastWriteTime, long localHeaderOffset)
        {
            var (dosTime, dosDate) = ToDosDateTime(lastWriteTime);
            return new ZipEntryMetadata(
                entryName,
                StoreMethod,
                0,
                0,
                0,
                dosTime,
                dosDate,
                0x10,
                localHeaderOffset);
        }

        private static (ushort Time, ushort Date) ToDosDateTime(DateTimeOffset timestamp)
        {
            var value = timestamp.LocalDateTime;
            if (value.Year < 1980)
            {
                value = new DateTime(1980, 1, 1, 0, 0, 0, DateTimeKind.Local);
            }

            if (value.Year > 2107)
            {
                value = new DateTime(2107, 12, 31, 23, 59, 58, DateTimeKind.Local);
            }

            var time = (ushort)((value.Hour << 11) | (value.Minute << 5) | (value.Second / 2));
            var date = (ushort)(((value.Year - 1980) << 9) | (value.Month << 5) | value.Day);
            return (time, date);
        }
    }
}
