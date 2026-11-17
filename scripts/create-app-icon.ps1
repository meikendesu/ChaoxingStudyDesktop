$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName PresentationCore,WindowsBase
$folder = Join-Path (Split-Path $PSScriptRoot -Parent) 'src/Desktop.App/Branding'
New-Item -ItemType Directory -Path $folder -Force | Out-Null
$paths = @(
    @{Color='#2563EB'; Path='M56,12 L200,12 Q244,12 244,56 L244,200 Q244,244 200,244 L56,244 Q12,244 12,200 L12,56 Q12,12 56,12Z'},
    @{Color='#3B82F6'; Path='M56,12 L200,12 Q244,12 244,56 L244,102 L12,170 L12,56 Q12,12 56,12Z'},
    @{Color='#DBEAFE'; Path='M47,68 Q87,58 124,79 L124,186 Q84,166 47,174Z'},
    @{Color='#FFFFFF'; Path='M132,79 Q169,58 209,68 L209,174 Q170,166 132,186Z'},
    @{Color='#93C5FD'; Path='M61,91 Q88,88 108,98 L108,105 Q87,96 61,99Z M61,115 Q88,112 108,122 L108,129 Q87,120 61,123Z'},
    @{Color='#BFDBFE'; Path='M148,98 Q169,88 195,91 L195,99 Q170,96 148,105Z M148,122 Q169,112 195,115 L195,123 Q170,120 148,129Z'},
    @{Color='#1D4ED8'; Path='M159,180 A36,36 0 1 1 231,180 A36,36 0 1 1 159,180Z'},
    @{Color='#34D399'; Path='M165,178 A30,30 0 1 1 225,178 A30,30 0 1 1 165,178Z'},
    @{Color='#FFFFFF'; Path='M177,175 L188,186 L210,163 L216,169 L188,197 L171,181Z'}
)
$svgPaths = ($paths | ForEach-Object { '<path fill="'+$_.Color+'" d="'+$_.Path+'"/>' }) -join "`n"
[IO.File]::WriteAllText((Join-Path $folder 'app-icon.svg'), '<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 256 256">'+$svgPaths+'</svg>')
$frames = @()
foreach ($size in @(16,24,32,48,64,128,256)) {
    $visual = New-Object Windows.Media.DrawingVisual
    $context = $visual.RenderOpen()
    $context.PushTransform((New-Object Windows.Media.ScaleTransform(($size/256.0),($size/256.0))))
    foreach ($shape in $paths) {
        $brush = [Windows.Media.BrushConverter]::new().ConvertFromString($shape.Color)
        $context.DrawGeometry($brush,$null,[Windows.Media.Geometry]::Parse($shape.Path))
    }
    $context.Pop(); $context.Close()
    $bitmap = [Windows.Media.Imaging.RenderTargetBitmap]::new($size,$size,96,96,[Windows.Media.PixelFormats]::Pbgra32)
    $bitmap.Render($visual)
    $encoder = New-Object Windows.Media.Imaging.PngBitmapEncoder
    $encoder.Frames.Add([Windows.Media.Imaging.BitmapFrame]::Create($bitmap))
    $stream = New-Object IO.MemoryStream
    $encoder.Save($stream)
    $frames += ,@{Size=$size; Bytes=$stream.ToArray()}
    if ($size -eq 256) { [IO.File]::WriteAllBytes((Join-Path $folder 'app-icon.png'),$stream.ToArray()) }
    $stream.Dispose()
}
$icon = [IO.File]::Create((Join-Path $folder 'app.ico'))
$writer = [IO.BinaryWriter]::new($icon)
try {
    $writer.Write([uint16]0); $writer.Write([uint16]1); $writer.Write([uint16]$frames.Count)
    $offset = 6 + 16 * $frames.Count
    foreach ($frame in $frames) {
        $dimension = if ($frame.Size -eq 256) { 0 } else { $frame.Size }
        $writer.Write([byte]$dimension); $writer.Write([byte]$dimension); $writer.Write([byte]0); $writer.Write([byte]0)
        $writer.Write([uint16]1); $writer.Write([uint16]32); $writer.Write([uint32]$frame.Bytes.Length); $writer.Write([uint32]$offset)
        $offset += $frame.Bytes.Length
    }
    foreach ($frame in $frames) { $writer.Write([byte[]]$frame.Bytes) }
} finally { $writer.Dispose() }
Write-Output 'Created SVG, PNG and seven-size ICO.'
