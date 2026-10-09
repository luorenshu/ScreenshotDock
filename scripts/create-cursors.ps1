$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
Add-Type @'
using System;
using System.Runtime.InteropServices;
public static class CursorAssetNativeMethods {
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool DestroyIcon(IntPtr icon);
}
'@
$projectRoot = Split-Path -Parent $PSScriptRoot
$cursorDirectory = Join-Path $projectRoot 'assets\cursors'
New-Item -ItemType Directory -Path $cursorDirectory -Force | Out-Null

function New-CursorAsset([string]$name, [double]$rotation, [string]$shape) {
    $bitmap = [System.Drawing.Bitmap]::new(32, 32, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
    $outline = [System.Drawing.Pen]::new([System.Drawing.Color]::FromArgb(255, 20, 27, 36), 5)
    $center = [System.Drawing.Pen]::new([System.Drawing.Color]::White, 1.8)
    $path = [System.Drawing.Drawing2D.GraphicsPath]::new()
    $iconHandle = [IntPtr]::Zero
    try {
        $graphics.Clear([System.Drawing.Color]::Transparent)
        $graphics.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
        $outline.StartCap = $outline.EndCap = [System.Drawing.Drawing2D.LineCap]::Round
        $center.StartCap = $center.EndCap = [System.Drawing.Drawing2D.LineCap]::Round
        $outline.LineJoin = $center.LineJoin = [System.Drawing.Drawing2D.LineJoin]::Round
        $segments = switch ($shape) {
            'crosshair' { @(@(3,16,12,16), @(20,16,29,16), @(16,3,16,12), @(16,20,16,29)) }
            'text' { @(@(16,4,16,28), @(11,4,21,4), @(11,28,21,28)) }
            'move' { @(@(16,3,16,29), @(3,16,29,16), @(12,7,16,3), @(16,3,20,7), @(12,25,16,29), @(16,29,20,25), @(7,12,3,16), @(3,16,7,20), @(25,12,29,16), @(29,16,25,20)) }
            'resize' { @(@(3,16,29,16), @(7,12,3,16), @(3,16,7,20), @(25,12,29,16), @(29,16,25,20)) }
        }
        foreach ($segment in $segments) {
            $path.StartFigure()
            $path.AddLine([single]$segment[0], [single]$segment[1], [single]$segment[2], [single]$segment[3])
        }
        if ($rotation -ne 0) {
            $matrix = [System.Drawing.Drawing2D.Matrix]::new()
            try { $matrix.RotateAt([single]$rotation, [System.Drawing.PointF]::new(16,16)); $path.Transform($matrix) } finally { $matrix.Dispose() }
        }
        $graphics.DrawPath($outline, $path)
        $graphics.DrawPath($center, $path)
        if ($shape -eq 'crosshair') {
            $graphics.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::None
            $dotOutline = [System.Drawing.SolidBrush]::new($outline.Color)
            try { $graphics.FillRectangle($dotOutline, 15, 15, 3, 3); $graphics.FillRectangle([System.Drawing.Brushes]::White, 16, 16, 1, 1) } finally { $dotOutline.Dispose() }
        }
        $iconHandle = $bitmap.GetHicon()
        $icon = [System.Drawing.Icon]::FromHandle($iconHandle)
        $stream = [System.IO.MemoryStream]::new()
        try { $icon.Save($stream); $bytes = $stream.ToArray() } finally { $icon.Dispose(); $stream.Dispose() }
        if ([BitConverter]::ToUInt16($bytes, 4) -ne 1) { throw 'Expected a single cursor image.' }
        # CUR uses the ICO image data, with type 2 and x/y hotspots in the directory entry.
        $bytes[2] = 2; $bytes[3] = 0
        $bytes[10] = 16; $bytes[11] = 0; $bytes[12] = 16; $bytes[13] = 0
        [System.IO.File]::WriteAllBytes((Join-Path $cursorDirectory ($name + '.cur')), $bytes)
    } finally {
        if ($iconHandle -ne [IntPtr]::Zero) { [void][CursorAssetNativeMethods]::DestroyIcon($iconHandle) }
        $path.Dispose(); $center.Dispose(); $outline.Dispose(); $graphics.Dispose(); $bitmap.Dispose()
    }
}

New-CursorAsset 'crosshair' 0 'crosshair'
New-CursorAsset 'text' 0 'text'
New-CursorAsset 'move' 0 'move'
New-CursorAsset 'resize-horizontal' 0 'resize'
New-CursorAsset 'resize-vertical' 90 'resize'
New-CursorAsset 'resize-diagonal-down' 45 'resize'
New-CursorAsset 'resize-diagonal-up' -45 'resize'
