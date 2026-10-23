# Third-party components

## OBS virtual-camera modules

The 32-bit and 64-bit modules come unmodified from OBS Studio 32.2.2's official Windows x64 portable release. Both binaries carry valid Authenticode signatures from their upstream publisher.

- Release: https://github.com/obsproject/obs-studio/releases/tag/32.2.2
- Corresponding source: https://github.com/obsproject/obs-studio/tree/32.2.2
- License: GNU General Public License version 2 or later; included in LICENSE.txt.
- Module source: plugins/win-dshow/virtualcam-module.
- Shared-memory protocol reference: shared/obs-shared-memory-queue.

PixelCam publishes NV12 frames through that shared-memory interface. Its current-user registration helpers use the modules' standard registration exports, then set the device's friendly name to PixelCam. The OBS desktop app is not bundled or needed.

## FFmpeg

The 0.7 installer downloads FFmpeg directly from its upstream publisher and verifies its pinned SHA-256. It retains the complete package, including licenses, in runtime/ffmpeg. FFmpeg binaries are not embedded in the installer. Earlier developer deliveries used a different build.

- Package: ffmpeg-n8.1.3-23-g5c4f5744cd-win64-lgpl-shared-8.1.zip (LGPL shared build).
- Project: https://ffmpeg.org/
- Source revision: https://github.com/FFmpeg/FFmpeg/tree/5c4f5744cd
- Upstream Windows build: https://github.com/BtbN/FFmpeg-Builds/releases/tag/autobuild-2026-10-22-13-06
- Build scripts: https://github.com/BtbN/FFmpeg-Builds
- Licenses are preserved in runtime/ffmpeg.

FFmpeg is run as a separate local decoder process. PixelCam does not link to its libraries.

## Android SDK Platform Tools

The 0.7 installer downloads Google's unmodified Android Platform Tools 37.0.0 directly from https://dl.google.com/android/repository/platform-tools_r37.0.0-win.zip and verifies its pinned SHA-256. The complete package, including NOTICE.txt, is retained in runtime/platform-tools. These binaries are not embedded in the installer.

- Project and distribution: https://developer.android.com/tools/releases/platform-tools

No third-party binaries have been modified.

## 7-Zip extraction helper

Unmodified 7z.exe and 7z.dll from 7-Zip 26.05 are bundled in runtime/7zip with License.txt. See that file for GNU LGPL, BSD and unRAR terms. Corresponding source is attached to the PixelCam release as 7zip-26.05-source.tar.xz.

- Upstream: https://www.7-zip.org/
- Binary: https://github.com/ip7z/7zip/releases/download/26.05/7z2605-x64.exe
- Source: https://github.com/ip7z/7zip/releases/download/26.05/7z2605-src.tar.xz

## .NET

The installer includes a self-contained .NET 10 Windows Desktop build. Microsoft and third-party notices are included as DOTNET-LICENSE.txt and DOTNET-NOTICES.txt. The runtime remains under its upstream terms.

- https://github.com/dotnet/runtime
- https://github.com/dotnet/winforms

## Corresponding OBS source

The release includes obs-studio-32.2.2-source.zip, the unmodified corresponding source for the bundled webcam modules. It comes from https://github.com/obsproject/obs-studio/archive/refs/tags/32.2.2.zip. The source and license links above remain applicable.

## NVIDIA Maxine

This software contains source code provided by NVIDIA Corporation. The unmodified SDK headers and proxy source in source/native/nvidia come from [NVIDIA-Maxine/Maxine-VFX-SDK](https://github.com/NVIDIA-Maxine/Maxine-VFX-SDK), revision f12bd18929e9065cff4f24cb93ac5f4202dc1c4a (0.7.6), under the included MIT license. The two sample MP4 files come from that revision's samples/input directory; NVIDIA's samples/NVOSSLicense.txt applies and is included as samples/NVIDIA-LICENSE.txt. See samples/SOURCES.md for provenance and hashes.

NVIDIA's proprietary runtime, dependencies, and models are installed separately under the current user's local application data. They are not part of PixelCam's GPL source license. The tested Ampere package is NVIDIA Video Effects SDK 0.7.6 from NVIDIA's [download center](https://www.nvidia.com/en-us/geforce/broadcasting/broadcast-sdk/resources/). Its NVIDIA Maxine EULA.pdf and ThirdPartyLicenses.txt are retained next to the runtime. PixelCam's original source carries the additional Maxine linking permission in MAXINE-LINKING-EXCEPTION.txt.

Eye Contact uses NVIDIA AR SDK 0.8.7, also installed separately under local application data. Its proprietary runtime/models retain their NVIDIA EULA and third-party notices. Unmodified AR headers and proxy source in source/native/nvidia-ar come from [NVIDIA-Maxine/Maxine-AR-SDK](https://github.com/NVIDIA-Maxine/Maxine-AR-SDK), revision f54db7ecaa0534b696d6194ac7252fc39e4d94a1, under the included MIT license. PROVENANCE.md records their provenance. The Background tab now selects Turing, Ampere, Ada or Blackwell packages for both SDKs; source/windows/MaxinePackages.json records URLs, pinned hashes and required-file manifests. No proprietary Maxine runtime/model files enter the repository or installer.
