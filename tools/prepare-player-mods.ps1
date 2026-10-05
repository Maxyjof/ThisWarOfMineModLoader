param([Parameter(Mandatory = $true)][string]$Destination)
$ErrorActionPreference = 'Stop'

#组合五个已实装功能与四个新增原生内容模组
$repository = Split-Path -Parent $PSScriptRoot
$target = [IO.Path]::GetFullPath($Destination)
if (Test-Path -LiteralPath $target) { throw '目标目录已经存在请使用新的目录以保留原有模组和个人配置' }
$entries = @(
    @{ Name = 'bridge'; Source = 'playtests\mods\bridge' },
    @{ Name = 'chronicle'; Source = 'playtests\mods\chronicle' },
    @{ Name = 'fast-scavenge'; Source = 'playtests\mods\fast-scavenge' },
    @{ Name = 'stamina'; Source = 'playtests\mods\stamina' },
    @{ Name = 'survival-camp'; Source = 'playtests\mods\survival-camp' },
    @{ Name = 'more-guns'; Source = 'mods\more-guns' },
    @{ Name = 'ammunition-supply'; Source = 'mods\ammunition-supply' },
    @{ Name = 'field-equipment'; Source = 'mods\field-equipment' },
    @{ Name = 'weapon-trading'; Source = 'mods\weapon-trading' }
)

#先确认所有源清单存在再创建目标避免半套模组包
foreach ($entry in $entries) {
    $source = Join-Path $repository $entry.Source
    if (!(Test-Path -LiteralPath (Join-Path $source 'mod.json'))) { throw "缺少模组：$($entry.Name)" }
}
New-Item -ItemType Directory -Path $target | Out-Null
foreach ($entry in $entries) {
    $source = Join-Path $repository $entry.Source
    Copy-Item -LiteralPath $source -Destination $target -Recurse
}
Write-Output "已准备九个实用与内容模组：$target"
