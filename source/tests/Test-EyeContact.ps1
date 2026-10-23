param([string]$OutputDirectory=(Join-Path $env:LOCALAPPDATA 'PixelCam\eye-test-results'),[string]$PixelRoot=(Split-Path -Parent (Split-Path -Parent $PSScriptRoot)))
$ErrorActionPreference='Stop'
$pixelRoot=$PixelRoot
$pixelExe=Join-Path $pixelRoot 'PixelCam.exe'
New-Item -ItemType Directory -Force $OutputDirectory | Out-Null
function Invoke-EyeCheck([string[]]$Arguments,[string]$Report,[string]$ArPath) {
    $start=[Diagnostics.ProcessStartInfo]::new($pixelExe);$start.UseShellExecute=$false
    foreach($argument in $Arguments){$start.ArgumentList.Add($argument)}
    if($ArPath){$start.Environment['PIXELCAM_AR_PATH']=$ArPath}
    $process=[Diagnostics.Process]::Start($start);$process.WaitForExit()
    if($process.ExitCode -ne 0 -or -not((Get-Content -Raw -LiteralPath $Report | ConvertFrom-Json).passed)){throw "Eye Contact test failed. Read $Report"}
}
foreach($case in @(@('720',1280,'input_0_100_frames.mp4'),@('1080',1920,'input_100_200_frames.mp4'))) {
    $report=Join-Path $OutputDirectory ($case[0]+'.json')
    Invoke-EyeCheck @('--eye-test',(Join-Path $pixelRoot ('samples\'+$case[2])),$report,[string]$case[1]) $report
}
$clip=Join-Path $pixelRoot 'samples\input_0_100_frames.mp4'
$report=Join-Path $OutputDirectory 'recorded-eye-controls.json'
Invoke-EyeCheck @('--eye-ui-test',$clip,$report) $report
$report=Join-Path $OutputDirectory 'missing-ar.json'
Invoke-EyeCheck @('--eye-ui-test',$clip,$report,'--expect-eye-warning') $report (Join-Path $OutputDirectory ([guid]::NewGuid().ToString('N')+'-missing-ar'))
"All Eye Contact tests passed. Recorded diagnostic images and reports: $OutputDirectory"
'No phone APIs are used. Close other PixelCam/OBS virtual-camera publishers before running this suite.'
