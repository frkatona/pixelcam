param([string]$AndroidSdk=$env:ANDROID_HOME,[string]$JavaJdk=$env:JAVA_HOME,[string]$GradleCommand)
$ErrorActionPreference='Stop'
$pixelRoot=Split-Path -Parent $PSScriptRoot
$buildRoot=Join-Path $env:LOCALAPPDATA 'PixelCam\build'
New-Item -ItemType Directory -Path $buildRoot -Force | Out-Null
if(-not $GradleCommand) {
    $gradleTool=Get-Command gradle -ErrorAction SilentlyContinue
    if($gradleTool) { $GradleCommand=$gradleTool.Source }
    else { throw 'Gradle is required. Pass -GradleCommand with the path to Gradle 8.10.2 or newer.' }
}
if(-not (Test-Path -LiteralPath "$AndroidSdk\platforms\android-35\android.jar")) { throw 'Android SDK 35 is required. Pass -AndroidSdk with its SDK root.' }
if(-not (Test-Path -LiteralPath "$JavaJdk\bin\java.exe")) { throw 'A Java 17 JDK is required. Pass -JavaJdk with its JDK root.' }
$env:JAVA_HOME=$JavaJdk; $env:ANDROID_HOME=$AndroidSdk; $env:ANDROID_SDK_ROOT=$AndroidSdk
$env:GRADLE_USER_HOME=Join-Path $env:LOCALAPPDATA 'AndroidDevelopment\gradle-cache'
$env:ANDROID_USER_HOME=Join-Path $env:LOCALAPPDATA 'AndroidDevelopment\android-user'
$env:PIXELCAM_BUILD_DIR=Join-Path $buildRoot 'android'
$env:DOTNET_CLI_TELEMETRY_OPTOUT='1'
& $GradleCommand -p (Join-Path $PSScriptRoot 'android') --no-daemon --console=plain --project-cache-dir (Join-Path $buildRoot 'gradle-project-cache') assembleDebug
if($LASTEXITCODE -ne 0) { throw 'Android build failed.' }
Copy-Item -LiteralPath (Join-Path $env:PIXELCAM_BUILD_DIR 'app\outputs\apk\debug\app-debug.apk') -Destination (Join-Path $pixelRoot 'PixelCam-Android.apk') -Force
& (Join-Path $PSScriptRoot 'Build-Windows.ps1') -BuildRoot $buildRoot
if($LASTEXITCODE -ne 0) { throw 'Maxine bridge build failed.' }
$compilerPath=Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
foreach($architecture in @('x64','x86')) {
    $suffix=if($architecture -eq 'x64'){'64'}else{'32'}
    & $compilerPath /nologo /target:winexe "/platform:$architecture" "/out:$pixelRoot\runtime\RegisterCamera$suffix.exe" (Join-Path $PSScriptRoot 'windows\RegisterCamera.cs')
    if($LASTEXITCODE -ne 0) { throw 'Webcam helper build failed.' }
}
'Build complete.'
