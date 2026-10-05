param([Parameter(Mandatory = $true)][string]$Destination)
$ErrorActionPreference = 'Stop'

#仅输出实用模组开发示例和失败探针仍保留在测试目录
$repository = Split-Path -Parent $PSScriptRoot
$target = [IO.Path]::GetFullPath($Destination)
if (Test-Path -LiteralPath $target) { throw '目标目录已经存在请使用新的目录以保留原有模组和个人配置' }
$names = @('bridge', 'chronicle', 'fast-scavenge', 'stamina', 'survival-camp')

#先确认所有源文件存在再创建目录避免源包不完整时生成半套部署输入
foreach ($name in $names) {
    $source = Join-Path $repository "playtests\mods\$name"
    if (!(Test-Path -LiteralPath (Join-Path $source 'mod.json'))) { throw "缺少实用模组：$name" }
}
New-Item -ItemType Directory -Path $target | Out-Null
foreach ($name in $names) {
    Copy-Item -LiteralPath (Join-Path $repository "playtests\mods\$name") -Destination $target -Recurse
}
Write-Output "已准备五个实用模组：$target"
