param([string]$OutputDirectory=(Join-Path $env:LOCALAPPDATA 'PixelCam\test-results'),[string]$PixelRoot=(Split-Path -Parent (Split-Path -Parent $PSScriptRoot)))
$ErrorActionPreference='Stop'
$pixelRoot=$PixelRoot
$pixelExe=Join-Path $pixelRoot 'PixelCam.exe'
New-Item -ItemType Directory -Force $OutputDirectory | Out-Null
function Invoke-OfflineCheck([string[]]$Arguments,[string]$Report) {
    $quotedArguments=$Arguments | ForEach-Object {'"'+$_+'"'}
    $process=Start-Process -FilePath $pixelExe -ArgumentList $quotedArguments -PassThru -Wait
    if($process.ExitCode -ne 0){throw "Offline test failed. Read $Report"}
    if(-not((Get-Content -Raw -LiteralPath $Report | ConvertFrom-Json).passed)){throw "Offline test did not pass. Read $Report"}
}
$mathReport=Join-Path $OutputDirectory 'color-framing.json'
Invoke-OfflineCheck @('--processing-test',$mathReport) $mathReport
foreach($case in @(@('720',1280,720,'input_0_100_frames.mp4'),@('1080',1920,1080,'input_100_200_frames.mp4'),@('4k-resized',3840,2160,'input_0_100_frames.mp4'),@('480',640,480,'input_100_200_frames.mp4'))) {
    $report=Join-Path $OutputDirectory ($case[0]+'.json')
    $clip=Join-Path $pixelRoot ('samples\'+$case[3])
    Invoke-OfflineCheck @('--recorded-test',$clip,$report,[string]$case[1],[string]$case[2]) $report
}
$uiReport=Join-Path $OutputDirectory 'recorded-controls.json'
Invoke-OfflineCheck @('--recorded-ui-test',(Join-Path $pixelRoot 'samples\input_0_100_frames.mp4'),$uiReport) $uiReport
"All offline tests passed. Reports and recorded-footage diagnostic images: $OutputDirectory"
'No phone APIs or live camera sources are used by this script. Close other PixelCam/OBS virtual-camera publishers before running it.'
