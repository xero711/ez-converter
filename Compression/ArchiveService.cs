using EZConverter.Compression.Models;
using EZConverter.Compression.Services.Archives;
using EZConverter.Compression.Services.Compression;
using EZConverter.Compression.Services.Gpu;

namespace EZConverter.Compression;

// Application entry point: archive engines always write to isolated staging targets.
public sealed class ArchiveService
{
    private readonly GpuCompressionBackend _gpu;
    private readonly CpuCompressionBackend _cpu = new();
    private readonly SemaphoreSlim _jobLock = new(1, 1);
    public GpuDetectionResult Detection { get; }
    public long GpuCompressionBatches => _gpu.CompressionBatches;
    public long GpuDecompressionBatches => _gpu.DecompressionBatches;

    public ArchiveService(GpuDetectionResult detection, NativeGpuCompressionBridge? bridge = null)
    {
        Detection = detection;
        _gpu = new(detection, bridge, requiresCpuPerformanceValidation: false);
    }
    public static async Task<ArchiveService> DetectAsync(CancellationToken ct = default) => new(await new GpuDetector().DetectAsync(ct));

    public async Task<ArchiveOperationResult> CreateAsync(IEnumerable<string> inputs, string outputPath,
        GpuMode mode, ArchiveCompressionLevel level, ArchiveOperationReporter reporter,
        CancellationToken ct = default, bool overwrite = false)
    {
        ArgumentNullException.ThrowIfNull(reporter);
        await _jobLock.WaitAsync(ct).ConfigureAwait(false);
        string? staged = null;
        try
        {
            string final = Path.GetFullPath(outputPath);
            ArchivePathGuard.RejectLinks(final);
            var format = GetFormat(final);
            if (File.Exists(final) && !overwrite) throw new IOException("同名のファイルが存在します。別の保存名を選んでください。");
            var manifest = new ArchiveInputScanner().BuildManifest(inputs, final, ct);
            if (manifest.TotalBytes > ArchiveSafetyLimits.MaxTotalBytes) throw new InvalidDataException("入力容量が安全上限を超えています。");
            ArchivePathGuard.ValidateNames(manifest.DirectoryEntries.Select(n => (n, true)).Concat(manifest.Files.Select(f => (f.EntryName, false))), Path.GetTempPath());
            string directory = Path.GetDirectoryName(final)!;
            Directory.CreateDirectory(directory);
            staged = Path.Combine(directory, $".ez-create-{Guid.NewGuid():N}{Path.GetExtension(final)}");
            var selection = new CompressionBackendSelector(_cpu, _gpu).SelectForArchiveCreate(mode, manifest, level, true);
            // Fast archives split large files into independent GPU-sized chunks.
            if (format is ZiperFastArchiveFormat && mode != GpuMode.CpuOnly && _gpu.IsAvailable && _gpu.SupportsZipDeflate && (mode == GpuMode.GpuPreferred || manifest.TotalBytes >= 8 * 1024 * 1024))
                selection = new(_gpu, "GPU", false, FallbackReason.None, "GPUチャンク圧縮", _gpu.Availability);
            reporter.Log.Report(selection.Reason);
            long gpuBatchesBefore = _gpu.CompressionBatches;
            var request = new ArchiveCreateRequest(manifest, staged, level, mode, false, selection.EngineLabel, selection.UsedFallback, selection.UsedFallback ? selection.Reason : "");
            var result = await format.CreateArchiveAsync(request, selection.Backend, reporter.Progress, reporter.Log, ct).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            if (selection.Backend is GpuCompressionBackend && _gpu.CompressionBatches == gpuBatchesBefore)
            {
                const string reason = "このZIPではGPU処理に対応するチャンクがありません。CPUで完了しました。";
                if (mode == GpuMode.GpuPreferred) { reporter.Log.Report(reason); result = result with { Engine = "CPU", UsedFallback = true, FallbackReason = reason }; }
                else result = result with { Engine = "CPU" };
            }
            ArchivePathGuard.RejectLinks(final);
            File.Move(staged, final, overwrite);
            staged = null;
            return result with { OutputPath = final };
        }
        finally
        {
            if (staged is not null) TryDeleteFile(staged);
            _jobLock.Release();
        }
    }

    public async Task<ArchiveOperationResult> ExtractAsync(string archivePath, string destination,
        GpuMode mode, IProgress<ArchiveProgress> progress, IProgress<string> log, CancellationToken ct = default)
    {
        await _jobLock.WaitAsync(ct).ConfigureAwait(false);
        string? stage = null;
        try
        {
            string archive = Path.GetFullPath(archivePath), final = Path.TrimEndingDirectorySeparator(Path.GetFullPath(destination));
            ArchivePathGuard.RejectLinks(archive); ArchivePathGuard.RejectLinks(final);
            var format = GetFormat(archive);
            if (Directory.Exists(final) || File.Exists(final)) throw new IOException("既存フォルダへの上書きは行いません。新しい展開先を選んでください。");
            string parent = Path.GetDirectoryName(final) ?? throw new IOException("ドライブ直下への展開はできません。");
            Directory.CreateDirectory(parent);
            stage = Path.Combine(parent, $".ez-extract-{Guid.NewGuid():N}");
            var request = new ArchiveExtractRequest(archive, stage, mode, false);
            ArchiveOperationResult result;
            if (format is ZipArchiveFormat)
                result = await SafeZipExtractor.ExtractAsync(request, _gpu, progress, log, ct).ConfigureAwait(false);
            else
                result = await format.ExtractArchiveAsync(request, _gpu, progress, log, ct).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            ArchivePathGuard.RejectLinks(final);
            Directory.Move(stage, final);
            stage = null;
            return result with { OutputPath = final };
        }
        finally
        {
            if (stage is not null) TryDeleteOwnedStage(stage);
            _jobLock.Release();
        }
    }

    private static IArchiveFormat GetFormat(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    { ".zip" => new ZipArchiveFormat(), ".ziper" => new ZiperFastArchiveFormat(), _ => throw new NotSupportedException("圧縮・展開タブではZIPとZiper Fast (.ziper)を使用できます。") };
    private static void TryDeleteFile(string path)
    {
        try { File.Delete(path); }
        catch (IOException exception) { System.Diagnostics.Trace.TraceWarning("Failed to remove staged archive file: {0}", exception.Message); }
        catch (UnauthorizedAccessException exception) { System.Diagnostics.Trace.TraceWarning("Failed to remove staged archive file: {0}", exception.Message); }
    }
    private static void TryDeleteOwnedStage(string stage)
    {
        try { if (Path.GetFileName(stage).StartsWith(".ez-extract-", StringComparison.Ordinal) && Directory.Exists(stage)) Directory.Delete(stage, true); }
        catch (IOException exception) { System.Diagnostics.Trace.TraceWarning("Failed to remove staged extraction folder: {0}", exception.Message); }
        catch (UnauthorizedAccessException exception) { System.Diagnostics.Trace.TraceWarning("Failed to remove staged extraction folder: {0}", exception.Message); }
    }
}
