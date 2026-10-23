param(
    [Parameter(Mandatory=$true)][string]$BaseUrl,
    [Parameter(Mandatory=$true)][string]$ReportPath
)
$ErrorActionPreference = 'Stop'
$pixelRoot = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$ffmpegPath = Join-Path $pixelRoot 'runtime\ffmpeg.exe'
$capabilities = Invoke-RestMethod -Uri "$BaseUrl/api/cameras" -TimeoutSec 5
$results = @()
foreach($lens in $capabilities.lenses) {
    foreach($resolution in $lens.resolutions) {
        $record = [ordered]@{Lens=$lens.label;LensId=$lens.id;Width=$resolution.width;Height=$resolution.height;Passed=$false}
        try {
            $configuration = @{lens=$lens.id;width=$resolution.width;height=$resolution.height;fps=30;enabled=$true} | ConvertTo-Json
            Invoke-RestMethod -Uri "$BaseUrl/api/config" -Method Post -ContentType 'application/json' -Body $configuration -TimeoutSec 5 | Out-Null
            $ready = $false
            for($attempt=0;$attempt -lt 60;$attempt++) {
                Start-Sleep -Milliseconds 150
                $status = Invoke-RestMethod -Uri "$BaseUrl/api/status" -TimeoutSec 5
                if($status.state -eq 'error') { throw $status.error }
                if($status.state -eq 'streaming' -and $status.lens -eq $lens.id -and $status.width -eq $resolution.width -and $status.height -eq $resolution.height) { $ready=$true; break }
            }
            if(-not $ready) { throw 'Camera switch did not complete.' }
            $decodeLog = & $ffmpegPath -hide_banner -fflags nobuffer -flags low_delay -probesize 32 -analyzeduration 0 -rw_timeout 8000000 -f h264 -i "$BaseUrl/video.h264" -frames:v 20 -an -f null - 2>&1
            $decodeExit = $LASTEXITCODE
            $decodeText = $decodeLog -join "`n"
            $frameMatches = [regex]::Matches($decodeText,'frame=\s*(\d+)')
            $frameCount = if($frameMatches.Count -gt 0) { [int]$frameMatches[$frameMatches.Count-1].Groups[1].Value } else { 0 }
            $record.DecodedFrames = $frameCount
            $record.DecodeExitCode = $decodeExit
            # With minimal live probing, input headers can report an unknown size until
            # the next SPS/keyframe. The decoded output describes actual recovered frames.
            $dimensions=[regex]::Match($decodeText,'Video: wrapped_avframe[^\r\n]*?,\s+(\d{2,5})x(\d{2,5})(?:[ ,\[])')
            $record.DecodedWidth=if($dimensions.Success){[int]$dimensions.Groups[1].Value}else{0}
            $record.DecodedHeight=if($dimensions.Success){[int]$dimensions.Groups[2].Value}else{0}
            $record.Passed = ($decodeExit -eq 0 -and $frameCount -ge 20 -and $record.DecodedWidth -eq $resolution.width -and $record.DecodedHeight -eq $resolution.height)
            if(-not $record.Passed) { $record.Error=$decodeText }
        } catch { $record.Error=$_.Exception.Message }
        $results += [pscustomobject]$record
        $results | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $ReportPath -Encoding utf8
        [pscustomobject]$record | Select-Object Lens,Width,Height,DecodedFrames,Passed | ConvertTo-Json -Compress
    }
}
if(@($results | Where-Object { -not $_.Passed }).Count -gt 0) { exit 1 }
