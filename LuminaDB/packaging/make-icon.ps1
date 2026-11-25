<#
  Makes LuminaDB's icon at every size from the SVG drawings in ..\..\icons\svg:
    icon_2c_paper_tri.svg      the full drawing (40 px and up)
    icon_2c_paper_tri_32.svg   simplified, on the pixel grid (32 px)
    icon_2c_paper_tri_24.svg   simplified, on the pixel grid (20 and 24 px)
    icon_2c_paper_tri_16.svg   simplified, on the pixel grid (16 px)
  Renders with Microsoft Edge (headless, transparent background), then writes
    src\LuminaDB.App\Assets\LuminaDB.ico   16-48 px as 32-bit bitmaps, 64-256 px as PNG
    src\LuminaDB.App\Assets\LuminaDB.png   256 px
    packaging\out\icon\                    every size as PNG and a strip of them all
#>
param(
    [string]$Edge = "${env:ProgramFiles(x86)}\Microsoft\Edge\Application\msedge.exe"
)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

$here   = Split-Path -Parent $MyInvocation.MyCommand.Path
$svgDir = [IO.Path]::GetFullPath((Join-Path $here '..\..\icons\svg'))
$assets = [IO.Path]::GetFullPath((Join-Path $here '..\src\LuminaDB.App\Assets'))
$out    = Join-Path $here 'out\icon'
New-Item -ItemType Directory -Force $out | Out-Null
if (-not (Test-Path $Edge)) { throw "Microsoft Edge not found at $Edge" }

$sizes = 16, 20, 24, 32, 40, 48, 64, 96, 128, 256
function Get-Drawing([int]$size) {
    if ($size -le 16) { return 'icon_2c_paper_tri_16.svg' }
    if ($size -le 24) { return 'icon_2c_paper_tri_24.svg' }
    if ($size -le 32) { return 'icon_2c_paper_tri_32.svg' }
    return 'icon_2c_paper_tri.svg'
}

# One page with every size side by side at 1:1 pixels, captured in one screenshot.
$gap = 8
$x = $gap
$cells = @()
$imgs = foreach ($s in $sizes) {
    $bytes = [IO.File]::ReadAllBytes((Join-Path $svgDir (Get-Drawing $s)))
    $uri = 'data:image/svg+xml;base64,' + [Convert]::ToBase64String($bytes)
    $cells += [pscustomobject]@{ Size = $s; X = $x }
    "<img src='$uri' style='position:absolute;left:${x}px;top:0;width:${s}px;height:${s}px'>"
    $x += $s + $gap
}
$width = $x
$height = 256
$html = Join-Path $out 'render.html'
"<!doctype html><html><head><style>html,body{margin:0;background:transparent;overflow:hidden}</style></head><body>$($imgs -join '')</body></html>" |
    Set-Content -Encoding utf8 $html
$shot = Join-Path $out 'render.png'
if (Test-Path $shot) { Remove-Item $shot }
$profile = Join-Path $out 'edge-profile'
$args = @('--headless', '--disable-gpu', '--hide-scrollbars', '--force-device-scale-factor=1',
          '--default-background-color=00000000', "--user-data-dir=$profile",
          "--window-size=$width,$height", "--screenshot=$shot", ([Uri]$html).AbsoluteUri)
$p = Start-Process -FilePath $Edge -ArgumentList $args -PassThru -WindowStyle Hidden
if (-not $p.WaitForExit(60000)) { $p.Kill(); throw 'Edge took too long' }
for ($i = 0; $i -lt 20 -and -not (Test-Path $shot); $i++) { Start-Sleep -Milliseconds 250 }
if (-not (Test-Path $shot)) { throw 'Edge made no screenshot' }

$sheet = [Drawing.Bitmap]::FromFile($shot)
$pngs = @{}
try {
    foreach ($c in $cells) {
        $bmp = New-Object Drawing.Bitmap $c.Size, $c.Size, ([Drawing.Imaging.PixelFormat]::Format32bppArgb)
        $g = [Drawing.Graphics]::FromImage($bmp)
        $g.CompositingMode = [Drawing.Drawing2D.CompositingMode]::SourceCopy
        $g.DrawImage($sheet, (New-Object Drawing.Rectangle 0, 0, $c.Size, $c.Size), (New-Object Drawing.Rectangle $c.X, 0, $c.Size, $c.Size), [Drawing.GraphicsUnit]::Pixel)
        $g.Dispose()
        $file = Join-Path $out ("LuminaDB-{0}.png" -f $c.Size)
        $bmp.Save($file, [Drawing.Imaging.ImageFormat]::Png)
        $pngs[$c.Size] = $bmp
    }
}
finally { $sheet.Dispose() }

# A 32-bit DIB icon entry (BITMAPINFOHEADER, bottom-up BGRA, then the 1-bit AND mask).
function Get-DibBytes([Drawing.Bitmap]$bmp) {
    $s = $bmp.Width
    $ms = New-Object IO.MemoryStream
    $w = New-Object IO.BinaryWriter $ms
    $w.Write([int]40); $w.Write([int]$s); $w.Write([int]($s * 2)); $w.Write([int16]1); $w.Write([int16]32)
    $w.Write([int]0); $w.Write([int]0); $w.Write([int]0); $w.Write([int]0); $w.Write([int]0); $w.Write([int]0)
    for ($y = $s - 1; $y -ge 0; $y--) {
        for ($x = 0; $x -lt $s; $x++) {
            $c = $bmp.GetPixel($x, $y)
            $w.Write([byte]$c.B); $w.Write([byte]$c.G); $w.Write([byte]$c.R); $w.Write([byte]$c.A)
        }
    }
    $stride = [int]([Math]::Ceiling($s / 32.0) * 4)
    for ($y = $s - 1; $y -ge 0; $y--) {
        $row = New-Object byte[] $stride
        for ($x = 0; $x -lt $s; $x++) {
            if ($bmp.GetPixel($x, $y).A -eq 0) { $row[[int][Math]::Floor($x / 8)] = $row[[int][Math]::Floor($x / 8)] -bor (0x80 -shr ($x % 8)) }
        }
        $w.Write($row, 0, $row.Length)
    }
    $w.Flush()
    return , $ms.ToArray()
}

$icoSizes = 16, 20, 24, 32, 40, 48, 64, 96, 128, 256
$entries = New-Object 'System.Collections.Generic.List[byte[]]'
foreach ($s in $icoSizes) {
    if ($s -le 48) { $entries.Add([byte[]](Get-DibBytes $pngs[$s])) }
    else { $entries.Add([IO.File]::ReadAllBytes((Join-Path $out "LuminaDB-$s.png"))) }
}
$ico = New-Object IO.MemoryStream
$w = New-Object IO.BinaryWriter $ico
$w.Write([int16]0); $w.Write([int16]1); $w.Write([int16]$icoSizes.Count)
$offset = 6 + 16 * $icoSizes.Count
for ($i = 0; $i -lt $icoSizes.Count; $i++) {
    $s = $icoSizes[$i]; $data = $entries[$i]
    $w.Write([byte]($s % 256)); $w.Write([byte]($s % 256)); $w.Write([byte]0); $w.Write([byte]0)
    $w.Write([int16]1); $w.Write([int16]32); $w.Write([int]$data.Length); $w.Write([int]$offset)
    $offset += $data.Length
}
foreach ($data in $entries) { $w.Write($data, 0, $data.Length) }
$w.Flush()
$icoFile = Join-Path $assets 'LuminaDB.ico'
[IO.File]::WriteAllBytes($icoFile, $ico.ToArray())

# The icon must open in WPF (the window), System.Drawing (the tray) and Explorer: check the first two here.
Add-Type -AssemblyName PresentationCore
$fs = [IO.File]::OpenRead($icoFile)
try
{
    $dec = [Windows.Media.Imaging.BitmapDecoder]::Create($fs, 'None', 'OnLoad')
    $got = ($dec.Frames | ForEach-Object { $_.PixelWidth } | Sort-Object) -join ','
    if ($got -ne ($icoSizes -join ',')) { throw "WPF reads sizes $got" }
}
finally { $fs.Dispose() }
foreach ($s in 16, 20, 24, 32) { $i = New-Object Drawing.Icon $icoFile, $s, $s; if ($i.Width -ne $s) { throw "System.Drawing picked $($i.Width) for $s" }; $i.Dispose() }
Copy-Item (Join-Path $out 'LuminaDB-256.png') (Join-Path $assets 'LuminaDB.png') -Force

# The strip: every size at 1:1 on light and dark backgrounds, then 16/24/32 enlarged 8x (nearest neighbour).
$zoomSizes = 16, 24, 32
$zoom = 8
$stripW = [Math]::Max($width, ($zoomSizes | ForEach-Object { $_ * $zoom + $gap } | Measure-Object -Sum).Sum + $gap)
$stripH = 2 * (256 + 2 * $gap) + 32 * $zoom + 2 * $gap
$strip = New-Object Drawing.Bitmap $stripW, $stripH
$g = [Drawing.Graphics]::FromImage($strip)
$g.Clear([Drawing.Color]::FromArgb(255, 245, 242, 234))
$g.FillRectangle((New-Object Drawing.SolidBrush ([Drawing.Color]::FromArgb(255, 32, 32, 32))), 0, 256 + 2 * $gap, $stripW, 256 + 2 * $gap)
foreach ($band in 0, 1) {
    foreach ($c in $cells) { $g.DrawImageUnscaled($pngs[$c.Size], $c.X, $band * (256 + 2 * $gap) + $gap) }
}
$g.InterpolationMode = [Drawing.Drawing2D.InterpolationMode]::NearestNeighbor
$g.PixelOffsetMode = [Drawing.Drawing2D.PixelOffsetMode]::Half
$zx = $gap
foreach ($s in $zoomSizes) {
    $g.DrawImage($pngs[$s], $zx, 2 * (256 + 2 * $gap) + $gap, $s * $zoom, $s * $zoom)
    $zx += $s * $zoom + $gap
}
$g.Dispose()
$strip.Save((Join-Path $out 'strip.png'), [Drawing.Imaging.ImageFormat]::Png)
$strip.Dispose()
foreach ($b in $pngs.Values) { $b.Dispose() }
Remove-Item $html, $shot -Force
Remove-Item $profile -Recurse -Force -ErrorAction SilentlyContinue
"icon: $((Get-Item (Join-Path $assets 'LuminaDB.ico')).Length) bytes, $($icoSizes.Count) sizes; strip: $(Join-Path $out 'strip.png')"
