param([switch]$SkipBuild)
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
$taskRepo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$taskCache = Join-Path $taskRepo 'work\gpu-packages'
$taskTarget = Join-Path $taskRepo 'Tools\GpuCompression'
New-Item -ItemType Directory -Force -Path $taskCache, $taskTarget | Out-Null

function Get-VerifiedPackage([string]$Url, [string]$Hash) {
    $archive = Join-Path $taskCache ([IO.Path]::GetFileName($Url))
    if (-not (Test-Path -LiteralPath $archive)) {
        Invoke-WebRequest -Uri $Url -OutFile $archive
    }
    if ((Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash -ne $Hash) {
        throw "GPUパッケージのSHA256が一致しません。使用しません: $archive"
    }
    $extract = Join-Path $taskCache ([IO.Path]::GetFileNameWithoutExtension($archive))
    if (-not (Test-Path -LiteralPath $extract)) {
        Expand-Archive -LiteralPath $archive -DestinationPath $extract
    }
    return $extract
}

# Pinned official NVIDIA redistributables. Update URL and hash together after reviewing the license/API.
$cuda = Get-VerifiedPackage 'https://developer.download.nvidia.com/compute/cuda/redist/cuda_cudart/windows-x86_64/cuda_cudart-windows-x86_64-12.9.37-archive.zip' 'f96afe6df898bc8510c48b44668bd9f825731efbf460f3640a922b2b8ae59ccc'
$nvcomp = Get-VerifiedPackage 'https://developer.download.nvidia.com/compute/nvcomp/redist/nvcomp/windows-x86_64/nvcomp-windows-x86_64-5.2.0.10_cuda12-archive.zip' '531670d2fea73d3d499b9ea71c56381ef6a619226510d95f7e8ed588234257e1'
foreach ($package in @(@{Root=$cuda;Pattern='cudart64_*.dll';License='LICENSE-CUDA.txt'}, @{Root=$nvcomp;Pattern='nvcomp*.dll';License='LICENSE-nvCOMP.txt'})) {
    $dlls = @(Get-ChildItem -LiteralPath $package.Root -Recurse -File -Filter $package.Pattern)
    if (-not $dlls) { throw 'GPU DLLがパッケージにありません。' }
    foreach ($dll in $dlls) { Copy-Item -LiteralPath $dll.FullName -Destination $taskTarget -Force }
    $license = Get-ChildItem -LiteralPath $package.Root -Recurse -File | Where-Object Name -in @('LICENSE','LICENSE.txt','EULA.txt') | Select-Object -First 1
    if (-not $license) { throw '再配布ライセンスが見つかりません。' }
    Copy-Item -LiteralPath $license.FullName -Destination (Join-Path $taskTarget $package.License) -Force
}
$taskSharpLicense = Join-Path $taskCache 'sharpziplib-1.4.2-LICENSE.txt'
if (-not (Test-Path -LiteralPath $taskSharpLicense)) {
    Invoke-WebRequest -Uri 'https://raw.githubusercontent.com/icsharpcode/SharpZipLib/v1.4.2/LICENSE.txt' -OutFile $taskSharpLicense
}
if ((Get-FileHash -LiteralPath $taskSharpLicense -Algorithm SHA256).Hash -ne 'd7fbbc16f871bf24fa5624c7eaf945054b778985f32676dcacba41fddca61133') {
    throw 'SharpZipLibのライセンスファイルの検証に失敗しました。'
}
Copy-Item -LiteralPath $taskSharpLicense -Destination (Join-Path $taskTarget 'LICENSE-SharpZipLib.txt') -Force

if ($SkipBuild) { return }
$vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
$vs = & $vswhere -latest -products '*' -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
if (-not $vs) { throw 'GPUブリッジのビルドにはVisual Studio C++ Build Toolsが必要です。' }
$cmake = Join-Path $vs 'Common7\IDE\CommonExtensions\Microsoft\CMake\CMake\bin\cmake.exe'
if (-not (Test-Path -LiteralPath $cmake)) {
    $command = Get-Command cmake -ErrorAction SilentlyContinue
    $cmake = if ($command) { $command.Source } else { 'C:\Program Files\JetBrains\CLion 2026.1\bin\cmake\win\x64\bin\cmake.exe' }
}
if (-not (Test-Path -LiteralPath $cmake)) { throw 'CMake 3.25以降が必要です。' }
$build = Join-Path $taskRepo 'work\gpu-native-build'
& $cmake -S (Join-Path $taskRepo 'Compression.Native') -B $build -G 'Visual Studio 17 2022' -A x64 -DZIPER_NATIVEGPU_EXPERIMENTAL_SINGLE_CHUNK_DEFLATE=ON
if ($LASTEXITCODE -ne 0) { throw 'GPUブリッジの構成に失敗しました。' }
& $cmake --build $build --config Release
if ($LASTEXITCODE -ne 0) { throw 'GPUブリッジのビルドに失敗しました。' }
Copy-Item -LiteralPath (Join-Path $build 'Release\EZConverter.NativeGpu.dll') -Destination $taskTarget -Force
$redistRoot = Join-Path $vs 'VC\Redist\MSVC'
$crt = Get-ChildItem -LiteralPath $redistRoot -Directory | Where-Object Name -Match '^\d' | Sort-Object Name -Descending | ForEach-Object { Get-ChildItem -LiteralPath (Join-Path $_.FullName 'x64') -Directory -Filter 'Microsoft.VC*.CRT' } | Select-Object -First 1
if (-not $crt) { throw 'nvCOMPに必要なVC++ランタイムの再配布DLLがありません。' }
Get-ChildItem -LiteralPath $crt.FullName -File -Filter '*.dll' | Copy-Item -Destination $taskTarget -Force
Write-Host "GPU圧縮・展開エンジンを配置しました: $taskTarget"
