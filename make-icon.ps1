<#
  App icon generator - builds a multi-resolution .ico with GDI+.
  Output : iPhoneTransfer.App\appicon.ico  (16/32/48/64/128/256, PNG-compressed ICO)
  Preview: _iconpreview.png (256)
  Run    : powershell -ExecutionPolicy Bypass -File .\make-icon.ps1
  Design : blue rounded square + white smartphone + green transfer badge (up/down arrows)
#>
$ErrorActionPreference = "Stop"
Add-Type -AssemblyName System.Drawing

$icoPath     = Join-Path $PSScriptRoot "iPhoneTransfer.App\appicon.ico"
$previewPath = Join-Path $PSScriptRoot "_iconpreview.png"

function Get-RoundedRect([single]$x, [single]$y, [single]$w, [single]$h, [single]$r) {
    $p = New-Object System.Drawing.Drawing2D.GraphicsPath
    $d = $r * 2
    $p.AddArc($x, $y, $d, $d, 180, 90)
    $p.AddArc($x + $w - $d, $y, $d, $d, 270, 90)
    $p.AddArc($x + $w - $d, $y + $h - $d, $d, $d, 0, 90)
    $p.AddArc($x, $y + $h - $d, $d, $d, 90, 90)
    $p.CloseFigure()
    return $p
}

function Add-Arrow($path, $cx, $cy, $len, $wid, $up) {
    $cx = [single]$cx; $cy = [single]$cy; $len = [single]$len; $wid = [single]$wid
    $headH = [single]($len * 0.46)
    $stemW = [single]($wid * 0.40)
    $top = [single]($cy - $len * 0.5)
    $bot = [single]($cy + $len * 0.5)
    $hw  = [single]($wid * 0.5)
    $sw  = [single]($stemW * 0.5)
    $pts = New-Object System.Collections.Generic.List[System.Drawing.PointF]
    if ($up) {
        [void]$pts.Add([System.Drawing.PointF]::new($cx, $top))
        [void]$pts.Add([System.Drawing.PointF]::new($cx - $hw, $top + $headH))
        [void]$pts.Add([System.Drawing.PointF]::new($cx - $sw, $top + $headH))
        [void]$pts.Add([System.Drawing.PointF]::new($cx - $sw, $bot))
        [void]$pts.Add([System.Drawing.PointF]::new($cx + $sw, $bot))
        [void]$pts.Add([System.Drawing.PointF]::new($cx + $sw, $top + $headH))
        [void]$pts.Add([System.Drawing.PointF]::new($cx + $hw, $top + $headH))
    } else {
        [void]$pts.Add([System.Drawing.PointF]::new($cx, $bot))
        [void]$pts.Add([System.Drawing.PointF]::new($cx - $hw, $bot - $headH))
        [void]$pts.Add([System.Drawing.PointF]::new($cx - $sw, $bot - $headH))
        [void]$pts.Add([System.Drawing.PointF]::new($cx - $sw, $top))
        [void]$pts.Add([System.Drawing.PointF]::new($cx + $sw, $top))
        [void]$pts.Add([System.Drawing.PointF]::new($cx + $sw, $bot - $headH))
        [void]$pts.Add([System.Drawing.PointF]::new($cx + $hw, $bot - $headH))
    }
    $path.AddPolygon($pts.ToArray())
}

function New-IconBitmap([int]$S) {
    $f = $S / 256.0
    $bmp = New-Object System.Drawing.Bitmap($S, $S, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode     = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
    $g.PixelOffsetMode   = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
    $g.Clear([System.Drawing.Color]::Transparent)

    # background: blue gradient rounded square
    $m = [single](6 * $f)
    $bg = Get-RoundedRect $m $m ([single]($S - 2*$m)) ([single]($S - 2*$m)) ([single](54 * $f))
    $rect = New-Object System.Drawing.RectangleF(0, 0, $S, $S)
    $c1 = [System.Drawing.Color]::FromArgb(255, 70, 175, 255)
    $c2 = [System.Drawing.Color]::FromArgb(255, 0, 100, 228)
    $grad = New-Object System.Drawing.Drawing2D.LinearGradientBrush($rect, $c1, $c2, [single]90)
    $g.FillPath($grad, $bg)

    # white smartphone
    $pw = [single](116 * $f); $ph = [single](188 * $f)
    $px = [single](($S - $pw) / 2); $py = [single](($S - $ph) / 2 - 6*$f)
    $shadow = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(45, 0, 0, 0))
    $shPath = Get-RoundedRect ([single]($px+3*$f)) ([single]($py+5*$f)) $pw $ph ([single](24*$f))
    $g.FillPath($shadow, $shPath)
    $white = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::White)
    $phone = Get-RoundedRect $px $py $pw $ph ([single](24*$f))
    $g.FillPath($white, $phone)
    # screen (light blue)
    $si = [single](10 * $f)
    $scr = Get-RoundedRect ([single]($px+$si)) ([single]($py+$si*1.6)) ([single]($pw-2*$si)) ([single]($ph-$si*3.2)) ([single](14*$f))
    $scrB = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(255, 225, 240, 255))
    $g.FillPath($scrB, $scr)

    # transfer badge: green circle + white up/down arrows
    $bcx = [single]($S * 0.705); $bcy = [single]($S * 0.715); $br = [single](46 * $f)
    $g.FillEllipse($white, [single]($bcx - $br - 5*$f), [single]($bcy - $br - 5*$f), [single](($br+5*$f)*2), [single](($br+5*$f)*2))
    $greenRect = New-Object System.Drawing.RectangleF([single]($bcx-$br), [single]($bcy-$br), [single]($br*2), [single]($br*2))
    $gb1 = [System.Drawing.Color]::FromArgb(255, 86, 214, 110)
    $gb2 = [System.Drawing.Color]::FromArgb(255, 38, 184, 92)
    $greenGrad = New-Object System.Drawing.Drawing2D.LinearGradientBrush($greenRect, $gb1, $gb2, [single]90)
    $g.FillEllipse($greenGrad, $greenRect)
    $arrows = New-Object System.Drawing.Drawing2D.GraphicsPath
    Add-Arrow $arrows ($bcx - $br*0.42) $bcy ($br*1.05) ($br*0.62) $true
    Add-Arrow $arrows ($bcx + $br*0.42) $bcy ($br*1.05) ($br*0.62) $false
    $g.FillPath($white, $arrows)

    $g.Dispose()
    return $bmp
}

function Save-Ico($bitmaps, [string]$path) {
    $list = @($bitmaps)
    $pngs = @()
    foreach ($b in $list) {
        $ms = New-Object System.IO.MemoryStream
        $b.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
        $pngs += , ($ms.ToArray())
        $ms.Dispose()
    }
    $fs = [System.IO.File]::Create($path)
    $bw = New-Object System.IO.BinaryWriter($fs)
    $count = $list.Count
    $bw.Write([uint16]0)
    $bw.Write([uint16]1)
    $bw.Write([uint16]$count)
    $offset = 6 + 16 * $count
    for ($i = 0; $i -lt $count; $i++) {
        $b = $list[$i]; $png = $pngs[$i]
        $wByte = if ($b.Width  -ge 256) { 0 } else { $b.Width }
        $hByte = if ($b.Height -ge 256) { 0 } else { $b.Height }
        $bw.Write([byte]$wByte)
        $bw.Write([byte]$hByte)
        $bw.Write([byte]0)
        $bw.Write([byte]0)
        $bw.Write([uint16]1)
        $bw.Write([uint16]32)
        $bw.Write([uint32]$png.Length)
        $bw.Write([uint32]$offset)
        $offset += $png.Length
    }
    foreach ($png in $pngs) { $bw.Write($png) }
    $bw.Flush(); $bw.Close(); $fs.Close()
}

$sizes = @(16, 32, 48, 64, 128, 256)
$bitmaps = @()
foreach ($s in $sizes) { $bitmaps += (New-IconBitmap $s) }
Save-Ico $bitmaps $icoPath
($bitmaps | Where-Object { $_.Width -eq 256 })[0].Save($previewPath, [System.Drawing.Imaging.ImageFormat]::Png)
foreach ($b in $bitmaps) { $b.Dispose() }

Write-Host ("ICO written: " + $icoPath + "  (" + ([math]::Round((Get-Item $icoPath).Length/1KB,1)) + " KB)")
Write-Host ("Preview: " + $previewPath)
