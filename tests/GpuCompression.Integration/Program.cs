using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using ZipFileDotNet = System.IO.Compression.ZipFile;
using EZConverter.Compression;
using EZConverter.Compression.Models;
using ICSharpCode.SharpZipLib.Zip;

var root = Path.Combine(Path.GetTempPath(), "EZConverter-GpuTests-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
var service = await ArchiveService.DetectAsync();
Console.WriteLine($"GPU: {service.Detection.GpuName}; {service.Detection.Notes}");
var progress = new InlineProgress<ArchiveProgress>(_ => { });
var log = new InlineProgress<string>(s => { if (s.Contains("フォールバック") || s.Contains("失敗")) Console.WriteLine(s); });
var reporter = new ArchiveOperationReporter(progress, log);
var passed = 0;
async Task Test(string name, Func<Task> body) { await body(); passed++; Console.WriteLine("PASS " + name); }
async Task Reject(string name, Func<Task> body) { await Test(name, async () => { bool rejected = false; try { await body(); } catch (Exception ex) when (ex is not TestFailure) { Console.WriteLine("  rejected: " + ex.GetType().Name); rejected = true; } if (!rejected) throw new TestFailure("Expected rejection"); }); }
void Check(bool yes, string error) { if (!yes) throw new TestFailure(error); }
void EqualTrees(string a, string b) { foreach (var file in Directory.EnumerateFiles(a, "*", SearchOption.AllDirectories)) { var target = Path.Combine(b, Path.GetRelativePath(a, file)); Check(File.Exists(target) && SHA256.HashData(File.ReadAllBytes(file)).SequenceEqual(SHA256.HashData(File.ReadAllBytes(target))), "Roundtrip mismatch: " + file); } }

try
{
    var inputs = Path.Combine(root, "資料"); Directory.CreateDirectory(inputs); Directory.CreateDirectory(Path.Combine(inputs, "空フォルダ"));
    var text = new byte[12 * 1024 * 1024];
    var pattern = Encoding.UTF8.GetBytes("EZ Converter GPU圧縮・展開の整合性確認0123456789\n");
    for (int i = 0; i < text.Length; i++) text[i] = pattern[i % pattern.Length];
    await File.WriteAllBytesAsync(Path.Combine(inputs, "大きな日本語.txt"), text);
    await File.WriteAllBytesAsync(Path.Combine(inputs, "ランダム.bin"), RandomNumberGenerator.GetBytes(1024 * 1024 + 13));
    await File.WriteAllBytesAsync(Path.Combine(inputs, "empty.bin"), []);
    for (int i = 0; i < 256; i++) await File.WriteAllBytesAsync(Path.Combine(inputs, $"small-{i:D3}.txt"), text.AsSpan(0, 65536).ToArray());
    foreach (var format in new[] { "zip", "ziper" })
    {
        string archive = Path.Combine(root, "GPU." + format);
        await Test(format + " GPU create / GPU extract / SHA256", async () => {
            await service.CreateAsync([inputs], archive, GpuMode.GpuPreferred, ArchiveCompressionLevel.Fast, reporter);
            string output = Path.Combine(root, "out-gpu-" + format);
            var result = await service.ExtractAsync(archive, output, GpuMode.GpuPreferred, progress, log);
            EqualTrees(inputs, Path.Combine(output, "資料"));
            Check(Directory.Exists(Path.Combine(output, "資料", "空フォルダ")), "Empty directory lost");
            Console.WriteLine($"  engine={result.Engine}; input={result.InputBytes}; restored={result.OutputBytes}; ms={result.Duration.TotalMilliseconds:F0}");
        });
        await Test(format + " GPU create / CPU extract", async () => { string output = Path.Combine(root, "out-cpu-" + format); await service.ExtractAsync(archive, output, GpuMode.CpuOnly, progress, log); EqualTrees(inputs, Path.Combine(output, "資料")); });
        await Test(format + " CPU create / GPU extract", async () => {
            string cpu = Path.Combine(root, "CPU." + format); await service.CreateAsync([inputs], cpu, GpuMode.CpuOnly, ArchiveCompressionLevel.Fast, reporter);
            string output = Path.Combine(root, "out-cross-" + format); await service.ExtractAsync(cpu, output, GpuMode.GpuPreferred, progress, log); EqualTrees(inputs, Path.Combine(output, "資料"));
        });
        if (format == "zip") await Test("Standard ZIP / .NET interoperability", async () => {
            string dest = Path.Combine(root, "dotnet"); ZipFileDotNet.ExtractToDirectory(archive, dest); EqualTrees(inputs, Path.Combine(dest, "資料")); await Task.CompletedTask;
        });
    }
    await Test("No GPU fallback", async () => {
        var cpuOnly = new ArchiveService(GpuDetectionResult.CpuOnly("GPUなしのテスト"));
        var result = await cpuOnly.CreateAsync([inputs], Path.Combine(root,"fallback.ziper"), GpuMode.GpuPreferred, ArchiveCompressionLevel.Fast, reporter);
        Check(result.UsedFallback && cpuOnly.GpuCompressionBatches == 0, "Fallback not reported");
    });
    await Reject("Existing extraction target protected", () => service.ExtractAsync(Path.Combine(root,"GPU.zip"), inputs, GpuMode.Auto, progress, log));
    foreach (string bad in new[] { "../outside.txt", "C:/outside.txt", "CON.txt", "safe/file:stream", "safe/trailing.", "safe/../x" })
    {
        string archive = Path.Combine(root, Guid.NewGuid() + ".zip");
        using (var zip = ZipFileDotNet.Open(archive, ZipArchiveMode.Create)) { var e = zip.CreateEntry(bad); using var output = e.Open(); output.WriteByte(7); }
        string destination = Path.Combine(root, "bad-" + Guid.NewGuid());
        await Reject("Unsafe name " + bad, () => service.ExtractAsync(archive, destination, GpuMode.GpuPreferred, progress, log));
        Check(!Directory.Exists(destination), "Unsafe archive left published files");
    }
    await Test("Cancelled creation preserves old archive", async () => {
        string target = Path.Combine(root,"preserve.zip"); await File.WriteAllBytesAsync(target, [1,2,3,4]);
        using var cts = new CancellationTokenSource();
        var cancelProgress = new InlineProgress<ArchiveProgress>(p => { if (p.ProcessedBytes > 0) cts.Cancel(); });
        bool cancelled = false;
        try { await service.CreateAsync([inputs],target,GpuMode.CpuOnly,ArchiveCompressionLevel.Fast,new ArchiveOperationReporter(cancelProgress, log),cts.Token,overwrite:true); } catch(OperationCanceledException){cancelled=true;}
        Check(cancelled && File.ReadAllBytes(target).SequenceEqual(new byte[]{1,2,3,4}), "Old archive changed");
    });
    await Test("Corrupt Fast archive rejected without output", async () => {
        string bad=Path.Combine(root,"corrupt.ziper"); var bytes=await File.ReadAllBytesAsync(Path.Combine(root,"GPU.ziper")); bytes[^7]^=0x55; await File.WriteAllBytesAsync(bad,bytes);
        string dest=Path.Combine(root,"corrupt-output"); await Reject("Fast CRC",()=>service.ExtractAsync(bad,dest,GpuMode.GpuPreferred,progress,log)); Check(!Directory.Exists(dest),"Corrupt output published");
    });
    await Test("Malformed ZIP DEFLATE is rejected before GPU execution", async () => {
        string source = Path.Combine(root,"malformed-source.bin"); await File.WriteAllBytesAsync(source, text);
        string archive = Path.Combine(root,"malformed-source.zip"); await service.CreateAsync([source],archive,GpuMode.CpuOnly,ArchiveCompressionLevel.Normal,reporter);
        using (var stream = new FileStream(archive,FileMode.Open,FileAccess.ReadWrite)) {
            var header=new byte[30]; await stream.ReadExactlyAsync(header); int name=BitConverter.ToUInt16(header,26), extra=BitConverter.ToUInt16(header,28); long payload=30+name+extra; stream.Position=payload+Math.Max(0,BitConverter.ToUInt32(header,18)/2); int original=stream.ReadByte(); stream.Position--; stream.WriteByte((byte)(original^0x80));
        }
        long before=service.GpuDecompressionBatches; string destination=Path.Combine(root,"malformed-output"); await Reject("Malformed raw DEFLATE",()=>service.ExtractAsync(archive,destination,GpuMode.GpuPreferred,progress,log)); Check(service.GpuDecompressionBatches==before && !Directory.Exists(destination),"Malformed bytes reached GPU or were published");
    });
    Console.WriteLine($"GPU_CALLS compress={service.GpuCompressionBatches}, decompress={service.GpuDecompressionBatches}");
    if(args.Contains("--require-gpu")) Check(service.GpuCompressionBatches>0 && service.GpuDecompressionBatches>0,"Actual GPU calls required, CPU fallback is not a pass");
    Check(!Directory.EnumerateFileSystemEntries(root,".ez-*").Any(),"Staging residue");
    Console.WriteLine($"PASS {passed} checks; artifacts={root}");
}
catch(Exception ex) { Console.Error.WriteLine(ex); Environment.ExitCode=1; }

sealed class TestFailure(string message):Exception(message);
sealed class InlineProgress<T>(Action<T> action):IProgress<T> { public void Report(T value)=>action(value); }
