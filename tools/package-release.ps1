param(
    [Parameter(Mandatory = $true)][string]$Version,
    [string]$OutputDirectory
)
$ErrorActionPreference = 'Stop'

#只接受版本号或带v前缀的版本标签
if ($Version -notmatch '^v?(\d+)\.(\d+)\.(\d+)$') { throw '版本必须采用v主版本.次版本.修订号格式' }
$versionNumber = "$($Matches[1]).$($Matches[2]).$($Matches[3])"
$repository = Split-Path -Parent $PSScriptRoot
if ([string]::IsNullOrWhiteSpace($OutputDirectory)) { $OutputDirectory = Join-Path $repository 'artifacts\release' }
$output = [IO.Path]::GetFullPath($OutputDirectory)
$releaseRoot = [IO.Path]::GetFullPath((Join-Path $repository 'artifacts'))
if (!$output.StartsWith($releaseRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
    throw '发布输出目录必须位于仓库artifacts目录中'
}
New-Item -ItemType Directory -Path $output -Force | Out-Null

#将单个文件流式写入发行ZIP并避免复制大型自包含程序
function Add-ReleaseFile([IO.Compression.ZipArchive]$Archive, [string]$Source, [string]$EntryName) {
    if (!(Test-Path -LiteralPath $Source -PathType Leaf)) { throw "发布文件不存在：$Source" }
    [IO.Compression.ZipFileExtensions]::CreateEntryFromFile(
        $Archive, $Source, $EntryName, [IO.Compression.CompressionLevel]::Optimal) | Out-Null
}

#发布两个独立自包含单文件程序以便玩家不必预装.NET
Add-Type -AssemblyName System.IO.Compression.FileSystem
$cliOutput = Join-Path $repository 'MaxyModLoader.Cli\bin\Release\net10.0\win-x64\publish'
$bootstrapOutput = Join-Path $repository 'MaxyModLoader.Bootstrap\bin\Release\net10.0\win-x64\publish'
dotnet publish (Join-Path $repository 'MaxyModLoader.Cli') -c Release -r win-x64 --self-contained true `
    "-p:Version=$versionNumber" -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:DebugType=None -p:DebugSymbols=false `
    -o $cliOutput
if ($LASTEXITCODE -ne 0) { throw '模组加载器自包含发布失败' }
dotnet publish (Join-Path $repository 'MaxyModLoader.Bootstrap') -c Release -r win-x64 --self-contained true `
    "-p:Version=$versionNumber" -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:DebugType=None -p:DebugSymbols=false `
    -o $bootstrapOutput
if ($LASTEXITCODE -ne 0) { throw '游戏入口自包含发布失败' }

#发布前确认两个程序的文件版本与发行标签完全一致
$expectedFileVersion = "$versionNumber.0"
foreach ($binary in @(
    (Join-Path $cliOutput 'MaxyModLoader.exe'),
    (Join-Path $bootstrapOutput 'MaxyModLoader.Bootstrap.exe')
)) {
    if (!(Test-Path -LiteralPath $binary -PathType Leaf)) { throw "发布程序缺失：$binary" }
    $actualFileVersion = [Diagnostics.FileVersionInfo]::GetVersionInfo($binary).FileVersion
    if ($actualFileVersion -ne $expectedFileVersion) {
        throw "程序版本与发行标签不一致：$binary（$actualFileVersion）"
    }
}

#发行加载器ZIP只收纳启动程序、安装器、说明和许可
$loaderZip = Join-Path $output "MaxyModLoader-$versionNumber-Windows-x64.zip"
$sampleZip = Join-Path $output "MaxyModLoader-$versionNumber-SampleMods.zip"
if (Test-Path -LiteralPath $loaderZip) { throw "该版本发行包已存在：$loaderZip" }
if (Test-Path -LiteralPath $sampleZip) { throw "该版本示例模组包已存在：$sampleZip" }
$loaderArchive = [IO.Compression.ZipFile]::Open($loaderZip, [IO.Compression.ZipArchiveMode]::Create)
try {
    Add-ReleaseFile $loaderArchive (Join-Path $repository 'tools\install-loader.ps1') 'install-loader.ps1'
    Add-ReleaseFile $loaderArchive (Join-Path $repository 'Docs\Players\Installation.md') '安装说明.md'
    Add-ReleaseFile $loaderArchive (Join-Path $repository 'LICENSE') 'LICENSE'
    Add-ReleaseFile $loaderArchive (Join-Path $cliOutput 'MaxyModLoader.exe') 'app/MaxyModLoader.exe'
    Add-ReleaseFile $loaderArchive (Join-Path $bootstrapOutput 'MaxyModLoader.Bootstrap.exe') 'app/MaxyModLoader.Bootstrap.exe'
    Add-ReleaseFile $loaderArchive (Join-Path $cliOutput 'ThirdPartyNotices.txt') 'app/ThirdPartyNotices.txt'
    $versionEntry = $loaderArchive.CreateEntry('版本.txt', [IO.Compression.CompressionLevel]::Optimal)
    $versionStream = $versionEntry.Open()
    try {
        $writer = [IO.StreamWriter]::new($versionStream, [Text.UTF8Encoding]::new($false))
        try { $writer.WriteLine($versionNumber) } finally { $writer.Dispose() }
    } finally { $versionStream.Dispose() }
} finally {
    $loaderArchive.Dispose()
}

#示例包仅包含有明确依赖和配置说明的日常玩法模组
$sampleMods = @(
    @{ Name = '游戏事件与测试记录桥'; Source = 'playtests\mods\bridge' },
    @{ Name = '搜刮提速'; Source = 'playtests\mods\fast-scavenge' },
    @{ Name = '节省移动体力'; Source = 'playtests\mods\stamina' },
    @{ Name = '生存营地辅助包'; Source = 'playtests\mods\survival-camp' }
)
$samplesArchive = [IO.Compression.ZipFile]::Open($sampleZip, [IO.Compression.ZipArchiveMode]::Create)
try {
    foreach ($mod in $sampleMods) {
        #每个嵌套ZIP根目录直接包含清单和模组入口
        $source = Join-Path $repository $mod.Source
        if (!(Test-Path -LiteralPath (Join-Path $source 'mod.json'))) { throw "示例模组清单缺失：$($mod.Name)" }
        $zipName = [regex]::Replace($mod.Name, '[\\/:*?"<>|]', '_') + '.zip'
        $nestedEntry = $samplesArchive.CreateEntry($zipName, [IO.Compression.CompressionLevel]::Optimal)
        $nestedStream = $nestedEntry.Open()
        $modArchive = [IO.Compression.ZipArchive]::new($nestedStream, [IO.Compression.ZipArchiveMode]::Create, $true)
        try {
            foreach ($file in Get-ChildItem -LiteralPath $source -File -Recurse) {
                #拒绝把链接目标或仓库外文件打进玩家模组
                if (($file.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { throw "模组含有文件链接：$($file.FullName)" }
                $relative = [IO.Path]::GetRelativePath($source, $file.FullName).Replace('\', '/')
                Add-ReleaseFile $modArchive $file.FullName $relative
            }
        } finally {
            $modArchive.Dispose()
            $nestedStream.Dispose()
        }
    }
} finally {
    $samplesArchive.Dispose()
}

#为本次版本的两个可下载文件生成SHA256校验清单
$hashes = Get-FileHash -Algorithm SHA256 -LiteralPath @($loaderZip, $sampleZip)
$hashes | ForEach-Object { '{0}  {1}' -f $_.Hash.ToLowerInvariant(), [IO.Path]::GetFileName($_.Path) } |
    Set-Content -LiteralPath (Join-Path $output 'SHA256SUMS.txt') -Encoding utf8
Write-Output "已生成Windows加载器包：$loaderZip"
Write-Output "已生成示例模组包：$sampleZip"
