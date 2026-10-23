param(
    [string]$DotNet='dotnet',
    [string]$Compiler="${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
    [string]$ArchiveTool="$env:ProgramFiles\7-Zip\7z.exe",
    [string]$BuildRoot=(Join-Path $env:LOCALAPPDATA 'PixelCam\installer-build'),
    [string]$ReleaseDirectory,
    [string]$AndroidApk,
    [switch]$SkipBuild
)
$ErrorActionPreference='Stop'
$pixelRoot=Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
if(-not $ReleaseDirectory){$ReleaseDirectory=Join-Path $pixelRoot 'releases'}
if(-not $AndroidApk){$AndroidApk=Join-Path $pixelRoot 'PixelCam-Android.apk'}
if(-not(Test-Path -LiteralPath $AndroidApk)){throw 'Build the Android sender first, or pass -AndroidApk with its APK path.'}
if(-not(Test-Path -LiteralPath $Compiler)){throw 'Inno Setup 6.7.3 or newer is required. Pass -Compiler with the path to ISCC.exe.'}
if(-not(Test-Path -LiteralPath $ArchiveTool)){throw 'The build machine needs 7-Zip. Installed PixelCam users do not need it.'}
New-Item -ItemType Directory -Force $BuildRoot,$ReleaseDirectory | Out-Null
$cache=Join-Path $BuildRoot 'downloads';New-Item -ItemType Directory -Force $cache | Out-Null
function Get-Input([string]$Name,[string]$Url,[string]$Hash) {
    $target=Join-Path $cache $Name
    if(-not(Test-Path -LiteralPath $target) -or (Get-FileHash -LiteralPath $target -Algorithm SHA256).Hash -ne $Hash) {
        Invoke-WebRequest -Uri $Url -OutFile $target
    }
    if((Get-FileHash -LiteralPath $target -Algorithm SHA256).Hash -ne $Hash){throw "Checksum mismatch: $Name"}
    return $target
}
if(-not $SkipBuild){& (Join-Path $pixelRoot 'source\Build-Windows.ps1') -DotNet $DotNet -SelfContained -BuildRoot (Join-Path $BuildRoot 'app-build')}
$runtime=Join-Path $pixelRoot 'runtime';New-Item -ItemType Directory -Force $runtime | Out-Null
$seven=Get-Input '7z2605-x64.exe' 'https://github.com/ip7z/7zip/releases/download/26.05/7z2605-x64.exe' 'FD99E86F57D8F429EF5ED045BF98315AF962E2309B0F1BA87A147A44C71647D0'
$sevenRoot=Join-Path $BuildRoot '7zip'; & $ArchiveTool x $seven "-o$sevenRoot" -y -bso0
if($LASTEXITCODE -ne 0){throw 'Could not extract the setup helper.'}
$obsRoot=Join-Path $runtime 'virtual-camera';New-Item -ItemType Directory -Force $obsRoot | Out-Null
if(-not(Test-Path (Join-Path $obsRoot 'obs-virtualcam-module64.dll')) -or -not(Test-Path (Join-Path $obsRoot 'obs-virtualcam-module32.dll'))) {
    $obs=Get-Input 'OBS-Studio-32.2.2-Windows-x64.zip' 'https://github.com/obsproject/obs-studio/releases/download/32.2.2/OBS-Studio-32.2.2-Windows-x64.zip' '4D6E40E3AB155F56B30DE517380566A206D74B63CDF5AD49AA596924768F97E1'
    $obsExtract=Join-Path $BuildRoot 'obs'; & $ArchiveTool x $obs "-o$obsExtract" '-ir!obs-virtualcam-module*.dll' -y -bso0
    if($LASTEXITCODE -ne 0){throw 'Could not extract the webcam modules.'}
    Get-ChildItem $obsExtract -Recurse -Filter 'obs-virtualcam-module*.dll' | Copy-Item -Destination $obsRoot -Force
}
$moduleHashes=@{'32'='E9513840E2B96DB8CEEB41CF6F5FDB85E58FE7DC44B6B9AFB62B198C5505AA88';'64'='0DCDE4A969A7CE45A39472D39BD15F5BFFF242A06449B27C61E20E667358EC34'}
foreach($suffix in @('32','64')) {if((Get-FileHash "$obsRoot\obs-virtualcam-module$suffix.dll" -Algorithm SHA256).Hash -ne $moduleHashes[$suffix]){throw 'Webcam module checksum mismatch.'}}
$compilerPath=Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
foreach($architecture in @('x64','x86')) {
    $suffix=if($architecture -eq 'x64'){'64'}else{'32'}
    & $compilerPath /nologo /target:winexe "/platform:$architecture" "/out:$runtime\RegisterCamera$suffix.exe" (Join-Path $pixelRoot 'source\windows\RegisterCamera.cs')
    if($LASTEXITCODE -ne 0){throw 'Webcam helper build failed.'}
}
# Staging has a strict allowlist: no user preferences, recordings, keys, logs,
# SDK installers, or developer build directories enter the public installer.
$payload=Join-Path $BuildRoot ('payload-'+[Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Force "$payload\runtime\7zip","$payload\runtime\virtual-camera","$payload\samples" | Out-Null
foreach($name in @('PixelCam.exe','LICENSE.txt','MAXINE-LINKING-EXCEPTION.txt','THIRD-PARTY-NOTICES.md','DOTNET-LICENSE.txt','DOTNET-NOTICES.txt')) {Copy-Item -LiteralPath (Join-Path $pixelRoot $name) $payload -Force}
Copy-Item -LiteralPath $AndroidApk "$payload\PixelCam-Android.apk"
$manual=if(Test-Path "$pixelRoot\docs\MANUAL.md"){"$pixelRoot\docs\MANUAL.md"}elseif(Test-Path "$pixelRoot\MANUAL.md"){"$pixelRoot\MANUAL.md"}else{"$pixelRoot\README.md"}
Copy-Item -LiteralPath $manual "$payload\README.md"
Copy-Item "$runtime\PixelCamMaxine.dll","$runtime\RegisterCamera32.exe","$runtime\RegisterCamera64.exe" "$payload\runtime"
Copy-Item "$obsRoot\*.dll" "$payload\runtime\virtual-camera"
Copy-Item "$sevenRoot\7z.exe","$sevenRoot\7z.dll","$sevenRoot\License.txt" "$payload\runtime\7zip"
Copy-Item "$pixelRoot\samples\*" "$payload\samples"
& $Compiler /Q "/DPayloadPath=$payload" "/DReleaseDir=$ReleaseDirectory" (Join-Path $PSScriptRoot 'PixelCam.iss')
if($LASTEXITCODE -ne 0){throw 'Installer compilation failed.'}
Get-ChildItem $ReleaseDirectory -Filter '*Setup.exe' | ForEach-Object { '{0}  {1}' -f (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant(),$_.Name } | Set-Content (Join-Path $ReleaseDirectory 'SHA256SUMS.txt') -Encoding ascii
"Installer created in $ReleaseDirectory"
