param([string]$BuildRoot=(Join-Path $env:LOCALAPPDATA 'PixelCam\build'),[string]$DotNet='dotnet',[switch]$SelfContained)
$ErrorActionPreference='Stop'
$pixelRoot=Split-Path -Parent $PSScriptRoot
New-Item -ItemType Directory -Force (Join-Path $pixelRoot 'runtime') | Out-Null
& cmake -S (Join-Path $PSScriptRoot 'native') -B (Join-Path $BuildRoot 'native') -G 'Visual Studio 17 2022' -A x64
if($LASTEXITCODE -ne 0){throw 'Native bridge configuration failed.'}
& cmake --build (Join-Path $BuildRoot 'native') --config Release
if($LASTEXITCODE -ne 0){throw 'Native bridge build failed.'}
Copy-Item (Join-Path $BuildRoot 'native\Release\PixelCamMaxine.dll') (Join-Path $pixelRoot 'runtime\PixelCamMaxine.dll') -Force
$intermediatePath=(Join-Path $BuildRoot 'windows\obj')+'\'
$binaryPath=(Join-Path $BuildRoot 'windows\bin')+'\'
$contained=if($SelfContained){'true'}else{'false'}
$publishRoot=Join-Path $BuildRoot 'publish'
& $DotNet publish (Join-Path $PSScriptRoot 'windows\PixelCam.csproj') -c Release -r win-x64 --self-contained $contained -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true "-p:BaseIntermediateOutputPath=$intermediatePath" "-p:BaseOutputPath=$binaryPath" -o $publishRoot
if($LASTEXITCODE -ne 0){throw 'Windows companion build failed.'}
Copy-Item (Join-Path $publishRoot '*') $pixelRoot -Force
