# Third-party notices

EZ Converter includes the following upstream Windows tools in its `Tools` directory. Their licenses apply to those components, not to EZ Converter. The files shipped inside each tool's folder contain additional license texts and notices where provided by upstream.

| Component | Bundled version / build | License and upstream information |
| --- | --- | --- |
| ImageMagick | 7.1.2-32 Q16-HDRI x64 portable | Apache License 2.0; [license](https://imagemagick.org/license/) · [source](https://github.com/ImageMagick/ImageMagick) |
| 7-Zip | 26.04 x64 | GNU LGPL and related terms; see `7-Zip\License.txt` · [license](https://www.7-zip.org/license.txt) · [source](https://www.7-zip.org/) |
| LibreOffice | 26.8.0.3 x86-64 | MPL 2.0 / LGPLv3+; license files are included in the installed tree · [licensing](https://www.libreoffice.org/about-us/licenses/) · [source](https://www.libreoffice.org/download/download-libreoffice/) |
| Calibre Portable | 9.15.0 | GNU GPL v3; see `Calibre\Calibre Portable\Calibre\LICENSE` · [source](https://github.com/kovidgoyal/calibre) |
| FontForge | 2025-10-09 x64 | GNU GPL v3; see `FontForge\LICENSE.txt` · [source](https://github.com/fontforge/fontforge) |
| Microsoft WebView2 Evergreen Runtime offline installer | x64 installer 1.3.275.13; Microsoft Authenticode signature and SHA-256 are verified by `scripts\Prepare-BundledTools.ps1` | Microsoft Edge WebView2 Runtime; [distribution guidance](https://learn.microsoft.com/microsoft-edge/webview2/concepts/distribution) · [official download page](https://developer.microsoft.com/microsoft-edge/webview2) |

## Application libraries and .NET runtime

The Windows x64 release is self-contained and was built against .NET 8.0.28 runtime packs. The applicable license and third-party notice texts are copied into `ThirdPartyLicenses` in the application output.

| Component | Resolved version | License | Included license / notice files |
| --- | --- | --- | --- |
| .NET Runtime | 8.0.28 | MIT | `dotnet-runtime-8.0.28-LICENSE.txt`, `dotnet-runtime-8.0.28-THIRD-PARTY-NOTICES.txt` |
| .NET Windows Desktop (WPF) | 8.0.28 | MIT | `dotnet-wpf-8.0.28-LICENSE.txt`, `dotnet-wpf-8.0.28-THIRD-PARTY-NOTICES.txt` |
| ASP.NET Core runtime | 8.0.28 | MIT | `aspnetcore-8.0.28-LICENSE.txt`, `aspnetcore-8.0.28-THIRD-PARTY-NOTICES.txt` |
| [PdfPig](https://www.nuget.org/packages/PdfPig/0.1.16) | 0.1.16 | Apache-2.0 | `PdfPig-0.1.16-LICENSE.txt` (includes component notices) |
| [ClosedXML](https://www.nuget.org/packages/ClosedXML/0.105.1) | 0.105.1 | MIT | `ClosedXML-0.105.1-LICENSE.txt` |
| [ClosedXML.Parser](https://www.nuget.org/packages/ClosedXML.Parser/2.0.0) | 2.0.0 | MIT | `ClosedXML.Parser-2.0.0-LICENSE.txt` |
| [DocumentFormat.OpenXml](https://www.nuget.org/packages/DocumentFormat.OpenXml/3.1.1) and `DocumentFormat.OpenXml.Framework` | 3.1.1 | MIT | `DocumentFormat.OpenXml-3.1.1-LICENSE.txt` |
| [ExcelNumberFormat](https://www.nuget.org/packages/ExcelNumberFormat/1.1.0) | 1.1.0 | MIT | `ExcelNumberFormat-1.1.0-LICENSE.txt` |
| [Microsoft.Web.WebView2](https://www.nuget.org/packages/Microsoft.Web.WebView2/1.0.4191.47) | 1.0.4191.47 | Microsoft license terms | `Microsoft-WebView2-LICENSE.txt`, `Microsoft-WebView2-NOTICE.txt` |
| [Microsoft.Win32.SystemEvents](https://www.nuget.org/packages/Microsoft.Win32.SystemEvents/6.0.0) | 6.0.0 | MIT | `Microsoft-Win32-SystemEvents-LICENSE.txt`, `Microsoft-Win32-SystemEvents-THIRD-PARTY-NOTICES.txt` |
| [QRCoder](https://www.nuget.org/packages/QRCoder/1.8.0) | 1.8.0 | MIT | `QRCoder-LICENSE.txt` |
| [RBush.Signed](https://www.nuget.org/packages/RBush.Signed/4.0.0) | 4.0.0 | MIT | `RBush.Signed-4.0.0-LICENSE.txt` |
| [SharpZipLib](https://www.nuget.org/packages/SharpZipLib/1.4.2) | 1.4.2 | MIT | `SharpZipLib-1.4.2-LICENSE.txt` |
| [SixLabors.Fonts](https://www.nuget.org/packages/SixLabors.Fonts/1.0.0) | 1.0.0 | Apache-2.0, as declared by the resolved package | `SixLabors.Fonts-1.0.0-LICENSE.txt` |
| [System.Drawing.Common](https://www.nuget.org/packages/System.Drawing.Common/6.0.0) | 6.0.0 | MIT | `System-Drawing-Common-LICENSE.txt`, `System-Drawing-Common-THIRD-PARTY-NOTICES.txt` |
| [System.IO.Packaging](https://www.nuget.org/packages/System.IO.Packaging/8.0.1) | 8.0.1 | MIT | `System-IO-Packaging-LICENSE.txt`, `System-IO-Packaging-THIRD-PARTY-NOTICES.txt` |

The versions above come from the resolved app dependency graph and the exact NuGet package metadata. SixLabors.Fonts 1.0.0 resolves to Apache-2.0; later releases use different terms, so do not update that row without rechecking the resolved package and its license.

QRCoder generates QR images locally; no URL is sent to a QR service.

The optional GPU archive engine uses NVIDIA's CUDA Runtime 12.9.37 and nvCOMP 5.2.0.10 packages, downloaded from NVIDIA's official redistribution endpoints with the published SHA-256 values pinned in `scripts\Prepare-GpuCompression.ps1`. The CUDA and nvCOMP license texts are preserved in `Tools\GpuCompression`; the SharpZipLib license is in `ThirdPartyLicenses`. The native bridge source is derived from [xero711/Ziper](https://github.com/xero711/Ziper) commit `80b4e03baf096d743073ce994b043bb548e40633`, is adapted for this app, and retains its MIT license in `Compression\LICENSE-Ziper.txt`.

FFmpeg and yt-dlp are downloaded directly by the application into the current user's local application data and are not included in this static bundle:

- FFmpeg `essentials` build: downloaded from [gyan.dev](https://www.gyan.dev/ffmpeg/builds/), the Windows build source linked from FFmpeg's download page; its published SHA-256 is verified. The Gyan build is GPL-3.0. FFmpeg source and license information: [FFmpeg legal](https://ffmpeg.org/legal.html) · [source](https://git.ffmpeg.org/ffmpeg.git).
- yt-dlp Nightly: downloaded from the [official Nightly releases](https://github.com/yt-dlp/yt-dlp-nightly-builds/releases) and verified against the release API's SHA-256 and size. License: [Unlicense](https://github.com/yt-dlp/yt-dlp/blob/master/LICENSE) · [source](https://github.com/yt-dlp/yt-dlp).
- Deno: downloaded from the [official Windows releases](https://github.com/denoland/deno/releases), verified against the release API's SHA-256 and size, then version-checked before installation. License: [MIT](https://github.com/denoland/deno/blob/main/LICENSE.md); a copy is in `ThirdPartyLicenses\Deno-LICENSE.txt` · [source](https://github.com/denoland/deno).

For redistribution, ensure the exact release's full license texts and any required corresponding source code or written offer accompany the binaries. Upstream URLs above identify source projects; they are not a substitute for checking the obligations that apply to the exact redistributed build and its transitive dependencies. Codec patents and third-party delegates may impose additional restrictions independently of these software licenses.
