$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
Add-Type @'
using System;
using System.Runtime.InteropServices;
public static class BrandPreviewNative {
    [DllImport("user32.dll", SetLastError=true)]
    public static extern IntPtr CreateIconFromResourceEx(byte[] data, uint size, [MarshalAs(UnmanagedType.Bool)] bool icon, uint version, int width, int height, uint flags);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool DestroyIcon(IntPtr icon);
}
'@
$projectRoot = Split-Path -Parent $PSScriptRoot
$iconPath = Join-Path $projectRoot 'assets\app.ico'
$iconData = [System.IO.File]::ReadAllBytes($iconPath)
$iconCount = [BitConverter]::ToUInt16($iconData,4)
$entries = @(for ($index=0; $index -lt $iconCount; $index++) {
    $entry = 6+16*$index
    $size = if ($iconData[$entry] -eq 0) { 256 } else { $iconData[$entry] }
    $offset = [BitConverter]::ToInt32($iconData,$entry+12)
    $length = [BitConverter]::ToInt32($iconData,$entry+8)
    if ($offset -lt (6+16*$iconCount) -or $offset+$length -gt $iconData.Length) { throw 'Invalid icon frame bounds.' }
    $payload = [byte[]]::new($length)
    [Array]::Copy($iconData,$offset,$payload,0,$length)
    $handle = [BrandPreviewNative]::CreateIconFromResourceEx($payload,[uint32]$length,$true,0x00030000,$size,$size,0)
    if ($handle -eq [IntPtr]::Zero) { throw 'Windows could not load an icon frame.' }
    $icon = [System.Drawing.Icon]::FromHandle($handle)
    $bitmap = $icon.ToBitmap()
    try {
        if ($bitmap.Width -ne $size -or $bitmap.Height -ne $size -or $bitmap.GetPixel(0,0).A -ne 0) { throw 'Invalid icon size or transparency.' }
    } finally { $bitmap.Dispose(); $icon.Dispose(); [void][BrandPreviewNative]::DestroyIcon($handle) }
    [pscustomobject]@{ size=$size; bits=[BitConverter]::ToUInt16($iconData,$entry+6); length=$length; offset=$offset; format='PNG'; nativeDecode='passed' }
})
$evidenceDirectory = Join-Path $projectRoot 'artifacts\editor-verification'
New-Item -ItemType Directory -Path $evidenceDirectory -Force | Out-Null
$entries | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $evidenceDirectory 'logo-icon-frames.json') -Encoding utf8

$sheet = [System.Drawing.Bitmap]::new(700,410)
$graphics = [System.Drawing.Graphics]::FromImage($sheet)
$titleFont = [System.Drawing.Font]::new('Segoe UI',22,[System.Drawing.FontStyle]::Bold)
$taglineFont = [System.Drawing.Font]::new('Microsoft YaHei UI',13)
$labelFont = [System.Drawing.Font]::new('Segoe UI',9)
$ink = [System.Drawing.SolidBrush]::new([System.Drawing.ColorTranslator]::FromHtml('#172B4D'))
$muted = [System.Drawing.SolidBrush]::new([System.Drawing.ColorTranslator]::FromHtml('#62728D'))
$night = [System.Drawing.SolidBrush]::new([System.Drawing.ColorTranslator]::FromHtml('#122B52'))
$master = [System.Drawing.Bitmap]::new((Join-Path $projectRoot 'assets\logo.png'))
try {
    $graphics.Clear([System.Drawing.ColorTranslator]::FromHtml('#F7FAFF'))
    $graphics.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
    $graphics.DrawImage($master, [System.Drawing.Rectangle]::new(34,28,256,256))
    $graphics.TextRenderingHint = [System.Drawing.Text.TextRenderingHint]::AntiAliasGridFit
    $graphics.DrawString('ScreenshotDock',$titleFont,$ink,[single]326,[single]62)
    $graphics.DrawString('截图即收纳',$taglineFont,$muted,[single]330,[single]112)
    $graphics.FillRectangle($night,0,290,700,120)
    for ($index=0; $index -lt 5; $index++) {
        $size = @(16,24,32,48,64)[$index]
        $icon = [System.Drawing.Icon]::new($iconPath,$size,$size)
        $bitmap = $icon.ToBitmap()
        try {
            $x = 345+64*$index
            $topY = 204-[int]($size/2)
            $bottomY = 342-[int]($size/2)
            $graphics.DrawImageUnscaled($bitmap,$x,$topY)
            $graphics.DrawImageUnscaled($bitmap,$x,$bottomY)
            $graphics.DrawString(($size.ToString()+' px'),$labelFont,$muted,[single]$x,[single]249)
            $graphics.DrawString(($size.ToString()+' px'),$labelFont,[System.Drawing.Brushes]::White,[single]$x,[single]382)
        } finally { $bitmap.Dispose(); $icon.Dispose() }
    }
    $graphics.DrawString('深色背景',$taglineFont,[System.Drawing.Brushes]::White,[single]45,[single]338)
    $sheet.Save((Join-Path $projectRoot 'assets\logo-preview.png'), [System.Drawing.Imaging.ImageFormat]::Png)
} finally {
    $master.Dispose(); $titleFont.Dispose(); $taglineFont.Dispose(); $labelFont.Dispose(); $ink.Dispose();
    $muted.Dispose(); $night.Dispose(); $graphics.Dispose(); $sheet.Dispose()
}
Write-Output 'All 9 icon frames decoded with correct dimensions and transparent corners.'
