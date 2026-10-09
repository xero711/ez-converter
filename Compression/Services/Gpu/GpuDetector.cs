using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using EZConverter.Compression.Models;
using EZConverter.Compression.Services.Compression;

namespace EZConverter.Compression.Services.Gpu;

public sealed class GpuDetector
{
    private static readonly string[] CudaRuntimeDllNames =
    {
        "cudart64_13.dll",
        "cudart64_12.dll",
        "cudart64_11.dll",
        "cudart64_10.dll"
    };

    private static readonly string[] NvcompDllNames =
    {
        "nvcomp.dll",
        "nvcomp64.dll",
        "nvcomp64_5.dll",
        "nvcomp64_4.dll",
        "nvcomp64_3.dll",
        "nvcomp64_2.dll"
    };

    public Task<GpuDetectionResult> DetectAsync(CancellationToken cancellationToken = default) =>
        Task.Run(() => Detect(cancellationToken), cancellationToken);

    private static GpuDetectionResult Detect(CancellationToken cancellationToken)
    {
        var smi = TryQueryNvidiaSmi(cancellationToken);
        var hasNvidiaGpu = smi.HasNvidiaGpu;
        var cudaDriverAvailable = TryLoadNativeLibrary("nvcuda.dll");
        var cudaRuntimeAvailable = IsAnyNativeLibraryLoadable(CudaRuntimeDllNames, "cudart64_*.dll", GetCudaRuntimeSearchDirectories());
        var nativeBridge = NativeGpuCompressionBridge.TryLoadDefault();
        var nativeZipDeflateAvailable =
            nativeBridge.IsLoaded &&
            (nativeBridge.SupportsZipCompatibleDeflate || nativeBridge.SupportsBatchZipCompatibleDeflate);
        var nvcompAvailable =
            nativeZipDeflateAvailable ||
            IsAnyNativeLibraryLoadable(NvcompDllNames, "nvcomp*.dll", GetNvcompSearchDirectories());

        hasNvidiaGpu = hasNvidiaGpu || cudaDriverAvailable;

        var backends = new List<string> { "CPU" };
        if (hasNvidiaGpu)
        {
            backends.Add("NVIDIA GPU検出");
        }

        if (cudaRuntimeAvailable)
        {
            backends.Add("CUDA Runtime");
        }

        if (nvcompAvailable)
        {
            backends.Add("nvCOMP");
        }

        if (nativeBridge.IsLoaded)
        {
            backends.Add(nativeZipDeflateAvailable
                ? "EZConverter.NativeGpu ZIP Deflate"
                : "EZConverter.NativeGpu読込済み");
        }

        var notes = BuildNotes(hasNvidiaGpu, cudaRuntimeAvailable, nvcompAvailable, nativeBridge, nativeZipDeflateAvailable);

        return new GpuDetectionResult(
            hasNvidiaGpu,
            cudaDriverAvailable,
            cudaRuntimeAvailable,
            nvcompAvailable,
            smi.GpuName,
            smi.VramBytes,
            backends,
            notes);
    }

    private static string BuildNotes(
        bool hasNvidiaGpu,
        bool cudaRuntimeAvailable,
        bool nvcompAvailable,
        NativeGpuCompressionBridge nativeBridge,
        bool nativeZipDeflateAvailable)
    {
        if (!hasNvidiaGpu)
        {
            return "NVIDIA GPU は検出されませんでした。CPUモードで動作します。";
        }

        if (nativeZipDeflateAvailable)
        {
            return "NVIDIA GPU とZIP互換GPUバックエンドを検出しました。Auto/GPU優先では条件に合う入力をGPUで処理します。";
        }

        if (nativeBridge.IsLoaded)
        {
            return "NVIDIA GPU とネイティブブリッジを検出しましたが、ZIP互換DEFLATEサポートは無効です。CPUフォールバックを使用します。";
        }

        var notes = "NVIDIA GPU を検出しました。ZIP互換GPUバックエンドは未接続のため、CPUフォールバックを使用します。";
        if (!cudaRuntimeAvailable)
        {
            notes += " CUDA Runtime DLL が見つかりません。";
        }

        if (!nvcompAvailable)
        {
            notes += " nvCOMP DLL が見つかりません。";
        }

        if (!string.IsNullOrWhiteSpace(nativeBridge.LoadError))
        {
            notes += $" ネイティブブリッジ: {nativeBridge.LoadError}";
        }

        return notes;
    }

    private static (bool HasNvidiaGpu, string? GpuName, long? VramBytes) TryQueryNvidiaSmi(CancellationToken cancellationToken)
    {
        (bool HasNvidiaGpu, string? GpuName, long? VramBytes) bestResult = (false, null, null);
        foreach (var executablePath in GetNvidiaSmiCandidates())
        {
            var result = TryQueryNvidiaSmiExecutable(executablePath, cancellationToken);
            if (result.HasNvidiaGpu && result.VramBytes.HasValue)
            {
                return result;
            }

            if (result.HasNvidiaGpu && !bestResult.HasNvidiaGpu)
            {
                bestResult = result;
            }
        }

        return bestResult;
    }

    private static IEnumerable<string> GetNvidiaSmiCandidates()
    {
        if (Environment.Is64BitOperatingSystem && !Environment.Is64BitProcess)
        {
            yield return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Sysnative", "nvidia-smi.exe");
        }

        yield return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "nvidia-smi.exe");
        yield return "nvidia-smi";
    }

    private static (bool HasNvidiaGpu, string? GpuName, long? VramBytes) TryQueryNvidiaSmiExecutable(
        string executablePath,
        CancellationToken cancellationToken)
    {
        try
        {
            using var process = new Process();
            process.StartInfo = new ProcessStartInfo
            {
                FileName = executablePath,
                Arguments = "--query-gpu=name,memory.total --format=csv,noheader,nounits",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };

            process.Start();
            if (!process.WaitForExit(3000))
            {
                try
                {
                    process.Kill(entireProcessTree: true);
                }
                catch
                {
                    // 終了できない場合でも検出処理は続行する。
                }

                return (false, null, null);
            }

            cancellationToken.ThrowIfCancellationRequested();
            if (process.ExitCode != 0)
            {
                return (false, null, null);
            }

            var firstLine = process.StandardOutput.ReadToEnd()
                .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                .FirstOrDefault();

            if (string.IsNullOrWhiteSpace(firstLine))
            {
                return (false, null, null);
            }

            var parts = firstLine.Split(',', 2, StringSplitOptions.TrimEntries);
            var name = parts.ElementAtOrDefault(0);
            long? vramBytes = null;
            var mib = parts.Length > 1 ? ParseMemoryTotalMiB(parts[1]) : null;
            if (mib.HasValue)
            {
                vramBytes = mib.Value * 1024L * 1024L;
            }

            return (true, name, vramBytes);
        }
        catch
        {
            return (false, null, null);
        }
    }

    private static long? ParseMemoryTotalMiB(string value)
    {
        if (long.TryParse(value.Trim(), out var mib))
        {
            return mib;
        }

        var match = Regex.Match(value, @"\d+");
        return match.Success && long.TryParse(match.Value, out mib) ? mib : null;
    }

    private static bool TryLoadNativeLibrary(string name)
    {
        try
        {
            if (!NativeLibrary.TryLoad(name, out var handle))
            {
                return false;
            }

            NativeLibrary.Free(handle);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static bool IsAnyNativeLibraryLoadable(
        IReadOnlyCollection<string> libraryNames,
        string searchPattern,
        IEnumerable<string> extraDirectories)
    {
        if (libraryNames.Any(TryLoadNativeLibrary))
        {
            return true;
        }

        foreach (var directory in extraDirectories)
        {
            if (TryLoadNativeLibraryFromDirectory(directory, libraryNames, searchPattern))
            {
                return true;
            }
        }

        return FindDllOnPath(searchPattern);
    }

    private static bool TryLoadNativeLibraryFromDirectory(
        string? directory,
        IReadOnlyCollection<string> libraryNames,
        string searchPattern)
    {
        if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
        {
            return false;
        }

        foreach (var libraryName in libraryNames)
        {
            if (TryLoadNativeLibrary(Path.Combine(directory, libraryName)))
            {
                return true;
            }
        }

        try
        {
            return Directory.EnumerateFiles(directory, searchPattern).Any(TryLoadNativeLibrary);
        }
        catch
        {
            return false;
        }
    }

    private static IEnumerable<string> GetCudaRuntimeSearchDirectories()
    {
        foreach (var directory in GetApplicationNativeSearchDirectories())
        {
            yield return directory;
        }

        if (!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("CUDA_PATH")))
        {
            yield return Path.Combine(Environment.GetEnvironmentVariable("CUDA_PATH")!, "bin");
        }
    }

    private static IEnumerable<string> GetNvcompSearchDirectories()
    {
        foreach (var directory in GetApplicationNativeSearchDirectories())
        {
            yield return directory;
        }

        var nvcompRoot = Environment.GetEnvironmentVariable("NVCOMP_ROOT");
        if (!string.IsNullOrWhiteSpace(nvcompRoot))
        {
            yield return nvcompRoot;
            yield return Path.Combine(nvcompRoot, "bin");
            yield return Path.Combine(nvcompRoot, "lib");
            yield return Path.Combine(nvcompRoot, "lib", "x64");
        }
    }

    private static IEnumerable<string> GetApplicationNativeSearchDirectories()
    {
        yield return AppContext.BaseDirectory;
        if (Environment.Is64BitProcess)
        {
            yield return Path.Combine(AppContext.BaseDirectory, "runtimes", "win-x64", "native");
        }
    }

    private static bool FindDllOnPath(string searchPattern)
    {
        var path = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        foreach (var directory in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                if (Directory.Exists(directory) && Directory.EnumerateFiles(directory, searchPattern).Any(TryLoadNativeLibrary))
                {
                    return true;
                }
            }
            catch
            {
                // 不正なPATH要素やアクセスできないフォルダは無視する。
            }
        }

        return false;
    }
}
