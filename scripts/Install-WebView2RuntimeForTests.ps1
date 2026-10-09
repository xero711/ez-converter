$ErrorActionPreference = 'Stop'

$clientId = '{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}'
$registryPaths = @(
    "HKLM:\SOFTWARE\WOW6432Node\Microsoft\EdgeUpdate\Clients\$clientId",
    "HKLM:\SOFTWARE\Microsoft\EdgeUpdate\Clients\$clientId",
    "HKCU:\Software\Microsoft\EdgeUpdate\Clients\$clientId"
)

function Get-WebView2RuntimeVersion {
    foreach ($registryPath in $registryPaths) {
        $versionText = (Get-ItemProperty -LiteralPath $registryPath -Name pv -ErrorAction SilentlyContinue).pv
        if ([version]$versionText -gt [version]'0.0.0.0') { return [version]$versionText }
    }
    return $null
}

$installedVersion = Get-WebView2RuntimeVersion
if ($installedVersion) {
    Write-Host "WebView2 Runtime already installed: $installedVersion"
    exit 0
}

if ([string]::IsNullOrWhiteSpace($env:RUNNER_TEMP)) {
    throw 'This test-only installer is intended for a GitHub Actions runner.'
}

$bootstrapperPath = Join-Path $env:RUNNER_TEMP 'MicrosoftEdgeWebView2Setup.exe'
Invoke-WebRequest -Uri 'https://go.microsoft.com/fwlink/p/?LinkId=2124703' -OutFile $bootstrapperPath
$signature = Get-AuthenticodeSignature -FilePath $bootstrapperPath
if ($signature.Status -ne [System.Management.Automation.SignatureStatus]::Valid -or
    $signature.SignerCertificate.Subject -notmatch 'CN=Microsoft Corporation') {
    throw 'The downloaded WebView2 installer does not have a valid Microsoft signature.'
}

$installer = Start-Process -FilePath $bootstrapperPath -ArgumentList '/silent', '/install' -Wait -PassThru
if ($installer.ExitCode -ne 0) {
    throw "WebView2 Runtime installation failed with exit code $($installer.ExitCode)."
}

$installedVersion = Get-WebView2RuntimeVersion
if (-not $installedVersion) {
    throw 'WebView2 Runtime installation completed, but its registry registration was not found.'
}
Write-Host "Installed WebView2 Runtime: $installedVersion"
