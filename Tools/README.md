# Bundled conversion engines

This folder is copied into both build and publish outputs. The stable conversion engines are staged here, so users do not need to install them or browse for executable paths. A clean source checkout can populate the pinned releases by running `pwsh -File .\scripts\Prepare-BundledTools.ps1`; the GitHub release workflow runs this automatically.

| Engine | Staged location | Role |
| --- | --- | --- |
| ImageMagick | `ImageMagick\magick.exe` | Images, raster/vector and image/PDF routes |
| LibreOffice | `LibreOffice\program\soffice.com` | Office documents, spreadsheets and presentations |
| 7-Zip | `7-Zip\7z.exe` | Archive extraction and repacking |
| Calibre Portable | `Calibre\Calibre Portable\Calibre\ebook-convert.exe` | E-book conversions |
| FontForge | `FontForge\bin\fontforge.exe` | Font conversions |
| Microsoft WebView2 Evergreen Runtime installer (x64) | `WebView2\MicrosoftEdgeWebView2RuntimeInstallerX64.exe` | Offline, silent setup for P2P browser features when the runtime is missing |

The frequently updated network tools are deliberately not frozen into the application package. On first launch and every 12 hours, the app checks for updates and downloads them to `%LOCALAPPDATA%\EZConverter\Tools` automatically:

- FFmpeg: `ffmpeg\bin\ffmpeg.exe` and `ffprobe.exe`; the archive SHA-256 is checked before installation.
- yt-dlp: `yt-dlp\yt-dlp.exe`; the official Nightly release's SHA-256 and size are checked before installation.

No executable-path selection is required. A network connection is needed for the first download and periodic update checks; an existing verified copy is retained if an update server is unavailable.

The WebView2 Evergreen installer is bundled so first-time setup also works offline. The app checks its pinned SHA-256 before running it silently and only installs the runtime when a P2P feature needs it. The installed Evergreen Runtime is maintained by Microsoft Edge Update.

## Notices and redistribution

See [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md) and the license/notice files shipped inside each engine folder. Keep those files with the binaries. Before publishing or redistributing this bundle, verify the license terms and any corresponding-source obligations for the exact binaries and their bundled dependencies. Upstream project links and build sources are recorded in the notices file.

ImageMagick format support depends on its compiled coders and delegates. SVG/PSD/HEIC/HEIF and other formats may require additional codec/delegate components; the app reports conversion failures instead of pretending every upstream format is guaranteed by this bundle.

The GPU archive engine is installed separately by `pwsh -File .\scripts\Prepare-GpuCompression.ps1` into `Tools\GpuCompression`. Its pinned CUDA Runtime and nvCOMP packages are SHA-256 checked against NVIDIA's redistribution metadata; their license texts and the required VC++ runtime DLLs are included alongside them. The custom `EZConverter.NativeGpu.dll` bridge loads these libraries from its own directory with the system DLL search path restricted. `SharpZipLib` 1.4.2 supplies bounded, managed raw-DEFLATE validation and its MIT license is included in the GPU-tools folder.
