$ErrorActionPreference='Stop'
Add-Type -AssemblyName System.Drawing
function Rounded-Path([float]$x,[float]$y,[float]$w,[float]$h,[float]$radius) {
    $path=[Drawing.Drawing2D.GraphicsPath]::new();$d=$radius*2
    $path.AddArc($x,$y,$d,$d,180,90);$path.AddArc($x+$w-$d,$y,$d,$d,270,90)
    $path.AddArc($x+$w-$d,$y+$h-$d,$d,$d,0,90);$path.AddArc($x,$y+$h-$d,$d,$d,90,90);$path.CloseFigure()
    return ,$path
}
$master=[Drawing.Bitmap]::new(1024,1024,[Drawing.Imaging.PixelFormat]::Format32bppArgb)
$graphics=[Drawing.Graphics]::FromImage($master);$graphics.SmoothingMode=[Drawing.Drawing2D.SmoothingMode]::AntiAlias
$graphics.ScaleTransform(4,4);$graphics.Clear([Drawing.Color]::Transparent)
$tile=Rounded-Path 12 12 232 232 52;$tileBrush=[Drawing.SolidBrush]::new([Drawing.Color]::FromArgb(39,104,194));$graphics.FillPath($tileBrush,$tile)
$body=Rounded-Path 43 75 170 119 22;$white=[Drawing.SolidBrush]::new([Drawing.Color]::FromArgb(248,251,255));$graphics.FillPath($white,$body)
$bump=Rounded-Path 71 59 57 35 9;$graphics.FillPath($white,$bump)
$lensBrush=[Drawing.SolidBrush]::new([Drawing.Color]::FromArgb(20,45,79));$graphics.FillEllipse($lensBrush,91,95,80,80)
$ring=[Drawing.Pen]::new([Drawing.Color]::FromArgb(96,188,239),9);$graphics.DrawEllipse($ring,103,107,56,56)
$dot=[Drawing.SolidBrush]::new([Drawing.Color]::FromArgb(39,104,194));$graphics.FillEllipse($dot,183,91,10,10)
$graphics.Dispose();$tile.Dispose();$body.Dispose();$bump.Dispose();$tileBrush.Dispose();$white.Dispose();$lensBrush.Dispose();$ring.Dispose();$dot.Dispose()
$images=[Collections.Generic.List[byte[]]]::new();$sizes=@(16,20,24,32,48,64,128,256)
foreach($size in $sizes) {
    $bitmap=[Drawing.Bitmap]::new($size,$size,[Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g=[Drawing.Graphics]::FromImage($bitmap);$g.CompositingMode=[Drawing.Drawing2D.CompositingMode]::SourceCopy
    $g.InterpolationMode=[Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic;$g.PixelOffsetMode=[Drawing.Drawing2D.PixelOffsetMode]::HighQuality
    $g.DrawImage($master,0,0,$size,$size);$g.Dispose();$stream=[IO.MemoryStream]::new()
    $bitmap.Save($stream,[Drawing.Imaging.ImageFormat]::Png);$images.Add($stream.ToArray());$stream.Dispose();$bitmap.Dispose()
}
$master.Dispose()
$iconStream=[IO.MemoryStream]::new();$writer=[IO.BinaryWriter]::new($iconStream)
$writer.Write([uint16]0);$writer.Write([uint16]1);$writer.Write([uint16]$sizes.Count)
$offset=6+16*$sizes.Count
for($index=0;$index -lt $sizes.Count;$index++) {
    $encodedSize=if($sizes[$index] -eq 256){0}else{$sizes[$index]}
    $writer.Write([byte]$encodedSize);$writer.Write([byte]$encodedSize);$writer.Write([byte]0);$writer.Write([byte]0)
    $writer.Write([uint16]1);$writer.Write([uint16]32);$writer.Write([uint32]$images[$index].Length);$writer.Write([uint32]$offset);$offset+=$images[$index].Length
}
foreach($bytes in $images){$writer.Write($bytes)}
$writer.Flush();[IO.File]::WriteAllBytes((Join-Path $PSScriptRoot 'PixelCam.ico'),$iconStream.ToArray());$writer.Dispose();$iconStream.Dispose()
'Generated PixelCam.ico at16,20,24,32,48,64,128,256pixels. Original geometric artwork; no external assets.'
