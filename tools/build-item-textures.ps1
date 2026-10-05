$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

$repository = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$sourceRoot = Join-Path $PSScriptRoot 'item-art/source'
$jobs = @(
    [pscustomobject]@{
        Source = Join-Path $sourceRoot 'weapons-atlas.png'
        Columns = 8
        Indices = @(0..39)
        Output = 'mods/more-guns/resources/UI/MaxyModLoader/Items/MoreGuns.dds'
    },
    [pscustomobject]@{
        Source = Join-Path $sourceRoot 'survival-items-atlas.png'
        Columns = 4
        Indices = @(0..3)
        Output = 'mods/ammunition-supply/resources/UI/MaxyModLoader/Items/Ammunition.dds'
    },
    [pscustomobject]@{
        Source = Join-Path $sourceRoot 'survival-items-atlas.png'
        Columns = 4
        Indices = @(4..9)
        Output = 'mods/field-equipment/resources/UI/MaxyModLoader/Items/FieldEquipment.dds'
    }
)

foreach ($job in $jobs) {
    #确认源图真实带有透明通道并按均匀格子切分
    $source = [Drawing.Bitmap]::FromFile($job.Source)
    $atlas = [Drawing.Bitmap]::new(1024, 1024, [Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $graphics = [Drawing.Graphics]::FromImage($atlas)
    try {
        if ($source.Width -ne $source.Height -or $source.GetPixel(0, 0).A -ne 0) {
            throw "源图必须为带透明背景的正方形PNG：$($job.Source)"
        }
        $graphics.Clear([Drawing.Color]::Transparent)
        $graphics.CompositingMode = [Drawing.Drawing2D.CompositingMode]::SourceOver
        $graphics.InterpolationMode = [Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
        $graphics.PixelOffsetMode = [Drawing.Drawing2D.PixelOffsetMode]::HighQuality
        $sourceCell = $source.Width / $job.Columns

        #把图标放入128像素单元对应的唯一编号位置
        for ($targetIndex = 0; $targetIndex -lt $job.Indices.Count; $targetIndex++) {
            $sourceIndex = $job.Indices[$targetIndex]
            $sourceX = ($sourceIndex % $job.Columns) * $sourceCell
            $sourceY = [Math]::Floor($sourceIndex / $job.Columns) * $sourceCell
            $sourceRectangle = [Drawing.RectangleF]::new($sourceX, $sourceY, $sourceCell, $sourceCell)
            $targetX = ($targetIndex % 8) * 128
            $targetY = [Math]::Floor($targetIndex / 8) * 128
            $targetRectangle = [Drawing.RectangleF]::new($targetX, $targetY, 128, 128)
            $graphics.DrawImage($source, $targetRectangle, $sourceRectangle, [Drawing.GraphicsUnit]::Pixel)
        }

        #导出连续的BGRA像素以匹配游戏已核验纹理格式
        $bounds = [Drawing.Rectangle]::new(0, 0, 1024, 1024)
        $locked = $atlas.LockBits($bounds, [Drawing.Imaging.ImageLockMode]::ReadOnly,
            [Drawing.Imaging.PixelFormat]::Format32bppArgb)
        try {
            if ($locked.Stride -ne 4096) { throw '纹理行步长不符合1024像素BGRA格式' }
            $pixels = [byte[]]::new(4096 * 1024)
            [Runtime.InteropServices.Marshal]::Copy($locked.Scan0, $pixels, 0, $pixels.Length)
        }
        finally {
            $atlas.UnlockBits($locked)
        }

        #写入无压缩DDS头部由加载器转换为LiquidEngine纹理
        $header = [byte[]]::new(128)
        [Text.Encoding]::ASCII.GetBytes('DDS ').CopyTo($header, 0)
        $fields = @(
            @(4, 124), @(8, 0x0002100F), @(12, 1024), @(16, 1024), @(20, 4096),
            @(24, 0), @(28, 0), @(76, 32), @(80, 0x41), @(84, 0), @(88, 32),
            @(92, 0x00FF0000), @(96, 0x0000FF00), @(100, 0x000000FF), @(104, 0xFF000000),
            @(108, 0x1000), @(112, 0), @(116, 0), @(120, 0), @(124, 0)
        )
        foreach ($field in $fields) {
            $value = [uint32](([long]$field[1]) -band 0xFFFFFFFFL)
            [Array]::Copy([BitConverter]::GetBytes($value), 0, $header, [int]$field[0], 4)
        }
        $output = Join-Path $repository $job.Output
        New-Item -ItemType Directory -Force (Split-Path $output) | Out-Null
        $dds = [byte[]]::new($header.Length + $pixels.Length)
        [Array]::Copy($header, 0, $dds, 0, $header.Length)
        [Array]::Copy($pixels, 0, $dds, $header.Length, $pixels.Length)
        [IO.File]::WriteAllBytes($output, $dds)
        Write-Output "已生成$(Resolve-Path $output)"
    }
    finally {
        $graphics.Dispose()
        $atlas.Dispose()
        $source.Dispose()
    }
}
