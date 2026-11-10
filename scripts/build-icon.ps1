param([string]$Preview = 'artifacts/SaruCast-icon-preview.png')
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
Set-Location (Split-Path $PSScriptRoot -Parent)
$assetDir = Join-Path (Get-Location) 'src/SaruCast/Assets'
New-Item -ItemType Directory -Force $assetDir, (Split-Path $Preview -Parent) | Out-Null
$canvas = New-Object Drawing.Bitmap 512,512,([Drawing.Imaging.PixelFormat]::Format32bppArgb)
$graphics = [Drawing.Graphics]::FromImage($canvas)
$graphics.SmoothingMode = [Drawing.Drawing2D.SmoothingMode]::AntiAlias
$graphics.Clear([Drawing.Color]::Transparent)
$tile = New-Object Drawing.Drawing2D.GraphicsPath
foreach($arc in @(@(16,16,176,176,180),@(320,16,176,176,270),@(320,320,176,176,0),@(16,320,176,176,90))) {
    $tile.AddArc($arc[0],$arc[1],$arc[2],$arc[3],$arc[4],90)
}
$tile.CloseFigure()
$white = New-Object Drawing.SolidBrush ([Drawing.Color]::White)
$edge = New-Object Drawing.Pen ([Drawing.ColorTranslator]::FromHtml('#DDE3EB')),3
$graphics.FillPath($white,$tile); $graphics.DrawPath($edge,$tile)

# The word itself is the logo. Rasterize installed Segoe UI outlines once;
# runtime users do not need a font or a graphics library beyond Windows.
$family = New-Object Drawing.FontFamily 'Segoe UI'
$font = New-Object Drawing.Font $family,180,([Drawing.FontStyle]::Bold),([Drawing.GraphicsUnit]::Pixel)
$format = [Drawing.StringFormat]::GenericTypographic.Clone()
$paths = @(); $position = 0.0
foreach($letter in @('C','a','s','t')) {
    $path = New-Object Drawing.Drawing2D.GraphicsPath
    $path.AddString($letter,$family,[int][Drawing.FontStyle]::Bold,180,[Drawing.PointF]::new($position,0),$format)
    $paths += $path
    $position += $graphics.MeasureString($letter,$font,([Drawing.PointF]::new(0,0)),$format).Width
}
$combined = New-Object Drawing.Drawing2D.GraphicsPath
foreach($path in $paths) { $combined.AddPath($path,$false) }
$bounds = $combined.GetBounds()
$scale = 428.0 / $bounds.Width
$matrix = New-Object Drawing.Drawing2D.Matrix
$matrix.Translate(256-($bounds.Left+$bounds.Width/2)*$scale,252-($bounds.Top+$bounds.Height/2)*$scale)
$matrix.Scale($scale,$scale)
$colors = @('#4285F4','#EA4335','#FBBC05','#34A853')
for($i=0;$i -lt $paths.Count;$i++) {
    $paths[$i].Transform($matrix)
    $brush = New-Object Drawing.SolidBrush ([Drawing.ColorTranslator]::FromHtml($colors[$i]))
    $graphics.FillPath($brush,$paths[$i]); $brush.Dispose(); $paths[$i].Dispose()
}
$canvas.Save([IO.Path]::GetFullPath($Preview),[Drawing.Imaging.ImageFormat]::Png)

# Small frames use classic 32-bit DIBs for installer compatibility; 256px uses PNG.
$sizes = @(16,24,32,48,64,128,256)
$frames = @()
foreach($size in $sizes) {
    $bitmap = New-Object Drawing.Bitmap $size,$size,([Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [Drawing.Graphics]::FromImage($bitmap)
    $g.InterpolationMode = [Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
    $g.PixelOffsetMode = [Drawing.Drawing2D.PixelOffsetMode]::HighQuality
    $g.Clear([Drawing.Color]::Transparent); $g.DrawImage($canvas,0,0,$size,$size); $g.Dispose()
    $stream = New-Object IO.MemoryStream
    if($size -eq 256) {
        $bitmap.Save($stream,[Drawing.Imaging.ImageFormat]::Png)
        $frames += ,$stream.ToArray()
    } else {
        $bitmap.Save($stream,[Drawing.Imaging.ImageFormat]::Bmp)
        $bmp = $stream.ToArray()
        $pixelOffset = [BitConverter]::ToInt32($bmp,10)
        if([BitConverter]::ToInt16($bmp,28) -ne 32 -or [BitConverter]::ToInt32($bmp,30) -ne 0) { throw 'Expected uncompressed 32-bit bitmap.' }
        $maskBytes = [int]([Math]::Ceiling($size/32.0)*4*$size)
        $dib = New-Object byte[] (40+$size*$size*4+$maskBytes)
        [Array]::Copy($bmp,14,$dib,0,40)
        [BitConverter]::GetBytes([int]($size*2)).CopyTo($dib,8)
        [BitConverter]::GetBytes([int]($dib.Length-40)).CopyTo($dib,20)
        [Array]::Copy($bmp,$pixelOffset,$dib,40,$size*$size*4)
        $frames += ,$dib
    }
    $stream.Dispose(); $bitmap.Dispose()
}
$file = [IO.File]::Create((Join-Path $assetDir 'SaruCast.ico'))
$writer = New-Object IO.BinaryWriter $file
try {
    $writer.Write([uint16]0); $writer.Write([uint16]1); $writer.Write([uint16]$sizes.Count)
    $offset = 6+16*$sizes.Count
    for($i=0;$i -lt $sizes.Count;$i++) {
        $dimension = if($sizes[$i] -eq 256){0}else{$sizes[$i]}
        $writer.Write([byte]$dimension); $writer.Write([byte]$dimension)
        $writer.Write([byte]0); $writer.Write([byte]0)
        $writer.Write([uint16]1); $writer.Write([uint16]32)
        $writer.Write([uint32]$frames[$i].Length); $writer.Write([uint32]$offset)
        $offset += $frames[$i].Length
    }
    foreach($frame in $frames) { $writer.Write([byte[]]$frame) }
} finally { $writer.Dispose() }
$graphics.Dispose(); $canvas.Dispose(); $tile.Dispose(); $white.Dispose(); $edge.Dispose()
$combined.Dispose(); $matrix.Dispose(); $format.Dispose(); $font.Dispose(); $family.Dispose()
Write-Output "Icon: $assetDir/SaruCast.ico"
Write-Output "Preview: $Preview"
