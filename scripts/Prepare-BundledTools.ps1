[CmdletBinding()]
param(
    [switch]$ValidateOnly
)

$ErrorActionPreference = 'Stop'
$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$toolsRoot = Join-Path $repoRoot 'Tools'
$workRoot = Join-Path (Join-Path $repoRoot 'work') ("dependency-preparation-{0}" -f [guid]::NewGuid().ToString('N'))

$packages = @{
    ImageMagick = @{
        Uri = 'https://github.com/ImageMagick/ImageMagick/releases/download/7.1.2-32/ImageMagick-7.1.2-32-portable-Q16-HDRI-x64.7z'
        FileName = 'ImageMagick.7z'
        Sha256 = 'BAC9155ACAC3147C460082282E01646596C054C613C77E6616F7E3AD0ED38E54'
        Executable = 'magick.exe'
        Arguments = @('-version')
    }
    SevenZip = @{
        ExtraUri = 'https://www.7-zip.org/a/7z2604-extra.7z'
        ExtraFileName = '7-Zip-extra.7z'
        ExtraSha256 = 'DC4B11D3399DB18B063630137145F5585D8F7AC847BF3639BD1185D7D1F7CEE0'
        Uri = 'https://www.7-zip.org/a/7z2604-x64.exe'
        FileName = '7-Zip-x64.exe'
        Sha256 = 'D54BF805F9F3704D1E8DB2FA3498AE7EF2DF0312B40B558E7C71C734430A665D'
        Executable = '7z.exe'
        Arguments = @('i')
    }
    LibreOffice = @{
        Uri = 'https://download.documentfoundation.org/libreoffice/stable/26.8.0/win/x86_64/LibreOffice_26.8.0_Win_x86-64.msi'
        FileName = 'LibreOffice.msi'
        Sha256 = '4AA6C6E1895F4055104EFFCB556BD3362D20C6AD707C149543304F395EF9DB95'
        Executable = 'program\soffice.com'
        Arguments = @('--headless', '--version')
    }
    Calibre = @{
        Uri = 'https://download.calibre-ebook.com/9.15.0/calibre-portable-installer-9.15.0.exe'
        FileName = 'Calibre-portable.exe'
        Sha256 = '55A8C89BC0739A2DC6D496742EA625FCCC6DFDEC1413EB805805A28E7227536C'
        Executable = 'Calibre Portable\Calibre\ebook-convert.exe'
        Arguments = @('--version')
    }
    FontForge = @{
        Uri = 'https://github.com/fontforge/fontforge/releases/download/20251009/FontForge-2025-10-09-Windows-x64.exe'
        FileName = 'FontForge.exe'
        Sha256 = '548523F08834E344BDA69ABB759E30C0F84A1A5EF9A5E965EB946D86A11118A3'
        Executable = 'bin\fontforge.exe'
        Arguments = @('--version')
    }
    WebView2 = @{
        Uri = 'https://msedge.sf.dl.delivery.mp.microsoft.com/filestreamingservice/files/7c7c0e6f-8cb5-406a-8e51-df0c62011e55/MicrosoftEdgeWebView2RuntimeInstallerX64.exe'
        FileName = 'MicrosoftEdgeWebView2RuntimeInstallerX64.exe'
        Sha256 = '2A6ADD76C37BFA872EB8C2B22D45B3216B8E2A1F193E0774006340FF62AC5FA0'
    }
}

function Invoke-CheckedProcess {
    param(
        [Parameter(Mandatory)][string]$FilePath,
        [Parameter(Mandatory)][string[]]$Arguments,
        [Parameter(Mandatory)][string]$Name
    )

    $startInfo = [System.Diagnostics.ProcessStartInfo]::new()
    $startInfo.FileName = $FilePath
    $startInfo.UseShellExecute = $false
    $startInfo.CreateNoWindow = $true
    foreach ($argument in $Arguments) {
        [void]$startInfo.ArgumentList.Add($argument)
    }

    $process = [System.Diagnostics.Process]::Start($startInfo)
    if ($null -eq $process) {
        throw "$Name を起動できません: $FilePath"
    }
    $process.WaitForExit()
    if ($process.ExitCode -ne 0) {
        throw "$Name の起動確認に失敗しました (終了コード $($process.ExitCode)): $FilePath"
    }
}

function Get-VerifiedDownload {
    param(
        [Parameter(Mandatory)][string]$Uri,
        [Parameter(Mandatory)][string]$Destination,
        [Parameter(Mandatory)][string]$ExpectedSha256
    )

    Write-Host "ダウンロード中: $Uri"
    Invoke-WebRequest -Uri $Uri -OutFile $Destination -MaximumRedirection 10 -TimeoutSec 900
    $actualHash = (Get-FileHash -LiteralPath $Destination -Algorithm SHA256).Hash
    if (-not [string]::Equals($actualHash, $ExpectedSha256, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "SHA-256が一致しません。配布物は展開しません: $Destination"
    }
}

function Assert-MicrosoftSignedFile {
    param(
        [Parameter(Mandatory)][string]$Path,
        [Parameter(Mandatory)][string]$Name
    )

    $signature = Get-AuthenticodeSignature -LiteralPath $Path
    if ($signature.Status -ne 'Valid' -or $signature.SignerCertificate.Subject -notmatch 'CN=Microsoft Corporation') {
        throw "$Name のMicrosoft署名を検証できませんでした。"
    }
}

function Move-StagedEngine {
    param(
        [Parameter(Mandatory)][string]$StagePath,
        [Parameter(Mandatory)][string]$DestinationPath,
        [Parameter(Mandatory)][string]$ExecutableRelativePath,
        [Parameter(Mandatory)][string[]]$Arguments,
        [Parameter(Mandatory)][string]$Name
    )

    $executablePath = Join-Path $StagePath $ExecutableRelativePath
    if (-not (Test-Path -LiteralPath $executablePath -PathType Leaf)) {
        throw "$Name の実行ファイルが展開先にありません: $executablePath"
    }
    Invoke-CheckedProcess -FilePath $executablePath -Arguments $Arguments -Name $Name

    if (Test-Path -LiteralPath $DestinationPath) {
        throw "不完全な既存フォルダーがあるため上書きしません: $DestinationPath"
    }
    Move-Item -LiteralPath $StagePath -Destination $DestinationPath
    Write-Host "$Name を Tools に配置しました。"
}

# Calibre Portable rejects install targets of 59 characters or more.
function New-CalibreStagePath {
    $basePaths = @(
        $toolsRoot,
        (Join-Path $env:SystemRoot 'Temp'),
        [System.IO.Path]::GetTempPath()
    ) | Where-Object { -not [string]::IsNullOrWhiteSpace($_) } | Select-Object -Unique

    foreach ($basePath in $basePaths) {
        $basePath = [System.IO.Path]::GetFullPath($basePath).TrimEnd(
            [System.IO.Path]::DirectorySeparatorChar,
            [System.IO.Path]::AltDirectorySeparatorChar
        )

        for ($attempt = 0; $attempt -lt 32; $attempt++) {
            $stageName = 'c' + [guid]::NewGuid().ToString('N').Substring(0, 3)
            $stagePath = Join-Path $basePath $stageName
            $portablePath = Join-Path $stagePath 'Calibre Portable'
            if ($portablePath.Length -ge 59) {
                break
            }

            try {
                New-Item -ItemType Directory -Path $stagePath -ErrorAction Stop | Out-Null
                return $stagePath
            }
            catch {
                Write-Verbose "Calibre展開先として使用できません: $stagePath ($($_.Exception.Message))"
            }
        }
    }

    throw 'Calibre Portableの展開先を確保できませんでした。書き込み可能で、Calibre Portableまでのパスが59文字未満になる場所が必要です。'
}

New-Item -ItemType Directory -Path $toolsRoot -Force | Out-Null

if ($ValidateOnly) {
    foreach ($name in @('ImageMagick', 'SevenZip', 'LibreOffice', 'Calibre', 'FontForge')) {
        $package = $packages[$name]
        $directoryName = if ($name -eq 'SevenZip') { '7-Zip' } else { $name }
        $executablePath = Join-Path (Join-Path $toolsRoot $directoryName) $package.Executable
        if (-not (Test-Path -LiteralPath $executablePath -PathType Leaf)) {
            throw "$name が同梱フォルダーにありません: $executablePath"
        }
        Invoke-CheckedProcess -FilePath $executablePath -Arguments $package.Arguments -Name $name
        Write-Host "$name : OK"
    }
    $webView2Installer = Join-Path (Join-Path $toolsRoot 'WebView2') $packages.WebView2.FileName
    if (-not (Test-Path -LiteralPath $webView2Installer -PathType Leaf)) {
        throw "WebView2 Runtime installer が同梱フォルダーにありません: $webView2Installer"
    }
    $webView2Hash = (Get-FileHash -LiteralPath $webView2Installer -Algorithm SHA256).Hash
    if (-not [string]::Equals($webView2Hash, $packages.WebView2.Sha256, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "WebView2 Runtime installer のSHA-256が一致しません: $webView2Installer"
    }
    Assert-MicrosoftSignedFile -Path $webView2Installer -Name 'WebView2 Runtime installer'
    Write-Host 'WebView2 Runtime installer : OK (Microsoft signature and SHA-256)'
    exit 0
}

New-Item -ItemType Directory -Path $workRoot -Force | Out-Null
$tarPath = Get-Command 'tar.exe' -ErrorAction SilentlyContinue
if ($null -eq $tarPath) {
    throw 'Windows標準のtar.exeが見つかりません。Windows 10/11上でエンジン準備を実行してください。'
}

$imageDestination = Join-Path $toolsRoot 'ImageMagick'
if (-not (Test-Path -LiteralPath (Join-Path $imageDestination 'magick.exe') -PathType Leaf)) {
    $archive = Join-Path $workRoot $packages.ImageMagick.FileName
    Get-VerifiedDownload $packages.ImageMagick.Uri $archive $packages.ImageMagick.Sha256
    $stage = Join-Path $workRoot 'ImageMagick'
    New-Item -ItemType Directory -Path $stage | Out-Null
    Invoke-CheckedProcess -FilePath $tarPath.Source -Arguments @('-xf', $archive, '-C', $stage) -Name 'ImageMagickの展開'
    Move-StagedEngine $stage $imageDestination $packages.ImageMagick.Executable $packages.ImageMagick.Arguments 'ImageMagick'
}

$sevenZipDestination = Join-Path $toolsRoot '7-Zip'
if (-not (Test-Path -LiteralPath (Join-Path $sevenZipDestination '7z.exe') -PathType Leaf)) {
    $installer = Join-Path $workRoot $packages.SevenZip.FileName
    Get-VerifiedDownload $packages.SevenZip.Uri $installer $packages.SevenZip.Sha256
    $extraArchive = Join-Path $workRoot $packages.SevenZip.ExtraFileName
    Get-VerifiedDownload $packages.SevenZip.ExtraUri $extraArchive $packages.SevenZip.ExtraSha256
    $stage = Join-Path $workRoot '7-Zip'
    New-Item -ItemType Directory -Path $stage | Out-Null
    $extraStage = Join-Path $workRoot '7-Zip-extra'
    New-Item -ItemType Directory -Path $extraStage | Out-Null
    Invoke-CheckedProcess -FilePath $tarPath.Source -Arguments @('-xf', $extraArchive, '-C', $extraStage) -Name '7-Zip Extraの展開'
    Copy-Item -Path (Join-Path $extraStage '*') -Destination $stage -Recurse -Force
    $bootstrapExtractor = Join-Path $stage 'x64\7za.exe'
    Invoke-CheckedProcess -FilePath $bootstrapExtractor -Arguments @('x', $installer, "-o$stage", '-y') -Name '7-Zipの展開'
    Move-StagedEngine $stage $sevenZipDestination $packages.SevenZip.Executable $packages.SevenZip.Arguments '7-Zip'
}

$libreOfficeDestination = Join-Path $toolsRoot 'LibreOffice'
if (-not (Test-Path -LiteralPath (Join-Path $libreOfficeDestination $packages.LibreOffice.Executable) -PathType Leaf)) {
    $installer = Join-Path $workRoot $packages.LibreOffice.FileName
    Get-VerifiedDownload $packages.LibreOffice.Uri $installer $packages.LibreOffice.Sha256
    $stage = Join-Path $workRoot 'LibreOffice'
    New-Item -ItemType Directory -Path $stage | Out-Null
    $msiStartInfo = [System.Diagnostics.ProcessStartInfo]::new()
    $msiStartInfo.FileName = Join-Path $env:SystemRoot 'System32\msiexec.exe'
    $msiStartInfo.UseShellExecute = $false
    $msiStartInfo.CreateNoWindow = $true
    foreach ($argument in @('/a', $installer, '/qn', "TARGETDIR=$stage")) {
        [void]$msiStartInfo.ArgumentList.Add($argument)
    }
    $msiProcess = [System.Diagnostics.Process]::Start($msiStartInfo)
    if ($null -eq $msiProcess) {
        throw 'LibreOfficeの展開プロセスを起動できませんでした。'
    }
    $msiProcess.WaitForExit()
    if ($msiProcess.ExitCode -notin @(0, 3010)) {
        throw "LibreOfficeの展開に失敗しました (終了コード $($msiProcess.ExitCode))。"
    }
    Move-StagedEngine $stage $libreOfficeDestination $packages.LibreOffice.Executable $packages.LibreOffice.Arguments 'LibreOffice'
}

$calibreDestination = Join-Path $toolsRoot 'Calibre'
if (-not (Test-Path -LiteralPath (Join-Path $calibreDestination $packages.Calibre.Executable) -PathType Leaf)) {
    $calibrePortableDestination = Join-Path $calibreDestination 'Calibre Portable'
    if ($calibrePortableDestination.Length -ge 59) {
        throw "Tools\Calibre の配置先がCalibre Portableの上限（59文字未満）を超えています。短いパスにプロジェクトを配置してください: $calibrePortableDestination"
    }

    $installer = Join-Path $workRoot $packages.Calibre.FileName
    Get-VerifiedDownload $packages.Calibre.Uri $installer $packages.Calibre.Sha256
    $signature = Get-AuthenticodeSignature -LiteralPath $installer
    if ($signature.Status -ne 'Valid' -or $signature.SignerCertificate.Subject -notmatch 'Kovid Goyal') {
        throw 'Calibreインストーラーの署名を検証できませんでした。'
    }
    $stage = New-CalibreStagePath
    Invoke-CheckedProcess -FilePath $installer -Arguments @($stage) -Name 'Calibre Portableの展開'
    Move-StagedEngine $stage $calibreDestination $packages.Calibre.Executable $packages.Calibre.Arguments 'Calibre'
}

$fontForgeDestination = Join-Path $toolsRoot 'FontForge'
if (-not (Test-Path -LiteralPath (Join-Path $fontForgeDestination $packages.FontForge.Executable) -PathType Leaf)) {
    $installer = Join-Path $workRoot $packages.FontForge.FileName
    Get-VerifiedDownload $packages.FontForge.Uri $installer $packages.FontForge.Sha256
    $stage = Join-Path $workRoot 'FontForge'
    New-Item -ItemType Directory -Path $stage | Out-Null
    Invoke-CheckedProcess -FilePath $installer -Arguments @(
        '/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART', '/CURRENTUSER', "/DIR=$stage"
    ) -Name 'FontForgeの展開'
    Move-StagedEngine $stage $fontForgeDestination $packages.FontForge.Executable $packages.FontForge.Arguments 'FontForge'
}

$webView2Destination = Join-Path $toolsRoot 'WebView2'
$webView2Installer = Join-Path $webView2Destination $packages.WebView2.FileName
if (-not (Test-Path -LiteralPath $webView2Installer -PathType Leaf)) {
    if (Test-Path -LiteralPath $webView2Destination) {
        throw "不完全な既存フォルダーがあるため上書きしません: $webView2Destination"
    }
    $stage = Join-Path $workRoot 'WebView2'
    New-Item -ItemType Directory -Path $stage | Out-Null
    $stagedInstaller = Join-Path $stage $packages.WebView2.FileName
    Get-VerifiedDownload $packages.WebView2.Uri $stagedInstaller $packages.WebView2.Sha256
    Assert-MicrosoftSignedFile -Path $stagedInstaller -Name 'WebView2 Runtime installer'
    Move-Item -LiteralPath $stage -Destination $webView2Destination
    Write-Host 'Microsoft WebView2 Runtime installer を Tools に配置しました。'
}
$webView2Hash = (Get-FileHash -LiteralPath $webView2Installer -Algorithm SHA256).Hash
if (-not [string]::Equals($webView2Hash, $packages.WebView2.Sha256, [System.StringComparison]::OrdinalIgnoreCase)) {
    throw "WebView2 Runtime installer のSHA-256が一致しません: $webView2Installer"
}
Assert-MicrosoftSignedFile -Path $webView2Installer -Name 'WebView2 Runtime installer'

foreach ($name in @('ImageMagick', 'SevenZip', 'LibreOffice', 'Calibre', 'FontForge')) {
    $package = $packages[$name]
    $directoryName = if ($name -eq 'SevenZip') { '7-Zip' } else { $name }
    $executablePath = Join-Path (Join-Path $toolsRoot $directoryName) $package.Executable
    Invoke-CheckedProcess -FilePath $executablePath -Arguments $package.Arguments -Name $name
    Write-Host "$name : OK"
}
Write-Host 'WebView2 Runtime installer : OK (Microsoft signature and SHA-256)'
