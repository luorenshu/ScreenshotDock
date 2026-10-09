$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
$projectRoot = Split-Path -Parent $PSScriptRoot
$assetDirectory = Join-Path $projectRoot 'assets'
New-Item -ItemType Directory -Path $assetDirectory -Force | Out-Null

function New-RoundedPath([single]$x, [single]$y, [single]$width, [single]$height, [single]$radius) {
    $path = [System.Drawing.Drawing2D.GraphicsPath]::new()
    $diameter = $radius * 2
    $path.AddArc($x, $y, $diameter, $diameter, 180, 90)
    $path.AddArc($x + $width - $diameter, $y, $diameter, $diameter, 270, 90)
    $path.AddArc($x + $width - $diameter, $y + $height - $diameter, $diameter, $diameter, 0, 90)
    $path.AddArc($x, $y + $height - $diameter, $diameter, $diameter, 90, 90)
    $path.CloseFigure()
    return $path
}

function Fill-Rounded($graphics, $brush, $x, $y, $width, $height, $radius) {
    $path = New-RoundedPath $x $y $width $height $radius
    try { $graphics.FillPath($brush, $path) } finally { $path.Dispose() }
}

# Same geometry and palette as assets/logo.svg. Supersampling preserves small-size edges.
function New-LogoBitmap([int]$size) {
    $renderSize = [Math]::Max(1024, $size * 4)
    $source = [System.Drawing.Bitmap]::new($renderSize, $renderSize, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $graphics = [System.Drawing.Graphics]::FromImage($source)
    $white = [System.Drawing.SolidBrush]::new([System.Drawing.ColorTranslator]::FromHtml('#F8FBFF'))
    $photoFill = [System.Drawing.SolidBrush]::new([System.Drawing.ColorTranslator]::FromHtml('#DDEBFF'))
    $hill = [System.Drawing.SolidBrush]::new([System.Drawing.ColorTranslator]::FromHtml('#2563EB'))
    $sun = [System.Drawing.SolidBrush]::new([System.Drawing.ColorTranslator]::FromHtml('#3DB2FF'))
    $back = [System.Drawing.SolidBrush]::new([System.Drawing.Color]::FromArgb(56, 248, 251, 255))
    $corners = [System.Drawing.Pen]::new([System.Drawing.Color]::FromArgb(184,248,251,255), 12)
    $highlight = [System.Drawing.Pen]::new([System.Drawing.ColorTranslator]::FromHtml('#E5F5FF'), 2)
    $tile = [System.Drawing.Drawing2D.LinearGradientBrush]::new([System.Drawing.RectangleF]::new(10,10,236,236),
        [System.Drawing.ColorTranslator]::FromHtml('#4B8DFF'), [System.Drawing.ColorTranslator]::FromHtml('#1648C4'), [single]45)
    $clip = [System.Drawing.Drawing2D.LinearGradientBrush]::new([System.Drawing.RectangleF]::new(123,73,16,41),
        [System.Drawing.ColorTranslator]::FromHtml('#96D6FF'), [System.Drawing.ColorTranslator]::FromHtml('#3DB2FF'), [single]90)
    try {
        $graphics.Clear([System.Drawing.Color]::Transparent)
        $graphics.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
        $graphics.ScaleTransform([single]($renderSize / 256), [single]($renderSize / 256))
        Fill-Rounded $graphics $tile 10 10 236 236 58
        $corners.StartCap = $corners.EndCap = [System.Drawing.Drawing2D.LineCap]::Round
        $corners.LineJoin = [System.Drawing.Drawing2D.LineJoin]::Round
        $graphics.DrawLines($corners, [System.Drawing.PointF[]]@([System.Drawing.PointF]::new(58,97), [System.Drawing.PointF]::new(58,60), [System.Drawing.PointF]::new(95,60)))
        $graphics.DrawLines($corners, [System.Drawing.PointF[]]@([System.Drawing.PointF]::new(198,164), [System.Drawing.PointF]::new(198,201), [System.Drawing.PointF]::new(161,201)))
        $state = $graphics.Save()
        $graphics.TranslateTransform(141,147.5); $graphics.RotateTransform(11); $graphics.TranslateTransform(-141,-147.5)
        Fill-Rounded $graphics $back 99 102 84 91 11
        $graphics.Restore($state)
        $state = $graphics.Save()
        $graphics.TranslateTransform(131,145.5); $graphics.RotateTransform(-7); $graphics.TranslateTransform(-131,-145.5)
        Fill-Rounded $graphics $white 77 96 108 99 12
        Fill-Rounded $graphics $photoFill 87 107 88 63 5
        $photo = New-RoundedPath 87 107 88 63 5
        try {
            $graphics.SetClip($photo, [System.Drawing.Drawing2D.CombineMode]::Intersect)
            $graphics.FillEllipse($sun, 145, 114, 14, 14)
            $graphics.FillPolygon($hill, [System.Drawing.PointF[]]@([System.Drawing.PointF]::new(87,150), [System.Drawing.PointF]::new(111,127),
                [System.Drawing.PointF]::new(136,150), [System.Drawing.PointF]::new(150,136), [System.Drawing.PointF]::new(175,161),
                [System.Drawing.PointF]::new(175,170), [System.Drawing.PointF]::new(87,170)))
        } finally { $photo.Dispose(); $graphics.Restore($state) }
        Fill-Rounded $graphics $clip 123 73 16 41 8
        $highlight.StartCap = $highlight.EndCap = [System.Drawing.Drawing2D.LineCap]::Round
        $graphics.DrawLine($highlight, 128,80,128,99)
        $result = [System.Drawing.Bitmap]::new($size, $size, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
        $resizer = [System.Drawing.Graphics]::FromImage($result)
        try {
            $resizer.CompositingMode = [System.Drawing.Drawing2D.CompositingMode]::SourceCopy
            $resizer.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
            $resizer.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
            $resizer.DrawImage($source, [System.Drawing.Rectangle]::new(0,0,$size,$size))
        } finally { $resizer.Dispose() }
        return $result
    } finally {
        $graphics.Dispose(); $source.Dispose(); $white.Dispose(); $photoFill.Dispose(); $hill.Dispose(); $sun.Dispose();
        $back.Dispose(); $corners.Dispose(); $highlight.Dispose(); $tile.Dispose(); $clip.Dispose()
    }
}

$master = New-LogoBitmap 1024
try { $master.Save((Join-Path $assetDirectory 'logo.png'), [System.Drawing.Imaging.ImageFormat]::Png) } finally { $master.Dispose() }

# Keep the first image at 32 px for the existing native tray loader.
$sizes = @(32,16,20,24,40,48,64,128,256)
$images = [System.Collections.Generic.List[object]]::new()
foreach ($size in $sizes) {
    $bitmap = New-LogoBitmap $size
    $buffer = [System.IO.MemoryStream]::new()
    try {
        # PNG frames retain full color and alpha; Icon.FromHandle().Save() quantizes to 16 colors.
        $bitmap.Save($buffer, [System.Drawing.Imaging.ImageFormat]::Png)
        $images.Add([pscustomobject]@{Size=$size; Payload=$buffer.ToArray()})
    } finally { $buffer.Dispose(); $bitmap.Dispose() }
}
$stream = [System.IO.File]::Create((Join-Path $assetDirectory 'app.ico'))
$writer = [System.IO.BinaryWriter]::new($stream)
try {
    $writer.Write([uint16]0); $writer.Write([uint16]1); $writer.Write([uint16]$images.Count)
    $offset = 6 + 16 * $images.Count
    foreach ($image in $images) {
        $dimension = if ($image.Size -eq 256) { 0 } else { $image.Size }
        $writer.Write([byte]$dimension); $writer.Write([byte]$dimension); $writer.Write([byte]0); $writer.Write([byte]0)
        $writer.Write([uint16]1); $writer.Write([uint16]32); $writer.Write([uint32]$image.Payload.Length); $writer.Write([uint32]$offset)
        $offset += $image.Payload.Length
    }
    foreach ($image in $images) { $writer.Write([byte[]]$image.Payload) }
} finally { $writer.Dispose(); $stream.Dispose() }
Write-Output 'Generated assets/logo.png and app.ico (16-256 px); vector source: assets/logo.svg'
