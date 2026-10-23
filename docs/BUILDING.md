# Building PixelCam

Building requires Windows x64, .NET 10 SDK, Visual Studio 2022 C++ desktop tools, CMake, 7-Zip, and Inno Setup 6.7.3 or newer. Running the resulting installer does not require these development tools.

## Android sender

Install Java 17, Android SDK platform 35, and Gradle 8.10.2 or newer. From the repository root:

```powershell
$env:ANDROID_HOME = 'C:\path\to\Android\Sdk'
$env:JAVA_HOME = 'C:\path\to\jdk-17'
gradle -p .\source\android assembleDebug
```

Pass the resulting APK to the installer build with `-AndroidApk`. For updates to an existing Android installation, keep the same signing key. Never commit a signing key to this repository.

## Windows and installer

```powershell
.\source\installer\Build-Installer.ps1 -AndroidApk 'C:\path\to\app-debug.apk'
```

Use `-Compiler`, `-ArchiveTool`, or `-DotNet` if the development tools are in nonstandard locations. The script builds the native Maxine bridge and a self-contained Windows app, compiles both webcam-registration helpers, obtains the pinned OBS webcam modules and 7-Zip helper, stages an allowlist of application files, and compiles the installer. Installer files and `SHA256SUMS.txt` appear in `releases`.

The repository includes the .NET license/notices used for this release. Refresh those notices when updating the bundled .NET runtime. Preserve `samples/SOURCES.md` and its NVIDIA sample license if including the test clips. If clips are absent, obtain the two named samples from the exact upstream revision recorded in that file before packaging.

The installer downloads pinned FFmpeg and Google Platform Tools archives at installation time and verifies SHA-256 before extraction. Maxine packages are selected separately at runtime; their URLs, hashes, and required-file manifests are embedded from `source/windows/MaxinePackages.json`. No proprietary NVIDIA runtime/model files enter the repository or application installer.

## Recorded-video testing

```powershell
.\source\tests\Test-Recorded.ps1 -PixelRoot "$env:LOCALAPPDATA\Programs\PixelCam"
.\source\tests\Test-EyeContact.ps1 -PixelRoot "$env:LOCALAPPDATA\Programs\PixelCam"
```

The recorded suite tests processing, background effects at several sizes, and live controls/webcam publication using the supplied clips. It does not access a phone. Eye Contact needs its optional runtime installed. Close other PixelCam instances and stop OBS virtual-camera output first.

Release verification should also cover a first install, an upgrade preserving settings/models, uninstall, unsupported-GPU behavior, required-tool download failure, and starting without an externally installed .NET runtime. Package checks on other GPU families do not establish successful inference on that hardware.

When publishing, attach the installer and checksum file plus corresponding source archives for bundled OBS modules and 7-Zip to the same release. Keep release binaries out of Git. No credentials, signing keys, user settings, camera captures, or local diagnostic logs belong in the repository.
