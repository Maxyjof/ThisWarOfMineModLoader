param(
    [Parameter(Mandatory = $true)][string]$GameDirectory,
    [Parameter(Mandatory = $true)][string]$PublishDirectory
)
$ErrorActionPreference = 'Stop'

#解析游戏和发布目录并检查目标游戏程序
$game = [IO.Path]::GetFullPath($GameDirectory)
$publish = [IO.Path]::GetFullPath($PublishDirectory)
if (!(Test-Path -LiteralPath (Join-Path $game 'x64\This War of Mine.exe'))) { throw '指定目录不是受支持的游戏安装目录' }
if (!(Test-Path -LiteralPath (Join-Path $publish 'MaxyModLoader.exe'))) { throw '发布目录缺少MaxyModLoader.exe请先执行自包含发布' }

#将启动器放入专属子目录并保留已有备份日志和运行记录
$app = Join-Path $game 'MaxyModLoader\app'
New-Item -ItemType Directory -Path $app -Force | Out-Null
Get-ChildItem -LiteralPath $publish -File -Recurse | Where-Object { $_.Extension -ne '.pdb' } | ForEach-Object {
    $relative = $_.FullName.Substring($publish.TrimEnd('\').Length + 1)
    $destination = Join-Path $app $relative
    New-Item -ItemType Directory -Path (Split-Path -Parent $destination) -Force | Out-Null
    Copy-Item -LiteralPath $_.FullName -Destination $destination -Force
}

#复用游戏已有的Mods目录其中的ZIP由启动器读取
$mods = Join-Path $game 'Mods'
New-Item -ItemType Directory -Path $mods -Force | Out-Null

#创建双击启动入口并且不覆盖玩家已有同名脚本
$launcher = Join-Path $game '使用MaxyModLoader启动.cmd'
$content = "@echo off`r`n`"%~dp0MaxyModLoader\app\MaxyModLoader.exe`" play `"%~dp0`"`r`nif errorlevel 1 pause`r`n"
if (Test-Path -LiteralPath $launcher) {
    if ([IO.File]::ReadAllText($launcher) -ne $content) { throw '启动脚本已存在且内容不同请先备份或改名' }
} else {
    [IO.File]::WriteAllText($launcher, $content, [Text.Encoding]::ASCII)
}
Write-Output "已安装启动器：$app"
Write-Output "请将模组ZIP放入：$mods"
Write-Output "之后双击：$launcher"
