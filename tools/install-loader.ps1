param(
    [Parameter(Mandatory = $true)][string]$GameDirectory,
    [Parameter(Mandatory = $true)][string]$PublishDirectory,
    [Parameter(Mandatory = $true)][string]$BootstrapDirectory
)
$ErrorActionPreference = 'Stop'

#解析游戏和发布目录并检查目标游戏程序
$game = [IO.Path]::GetFullPath($GameDirectory)
$publish = [IO.Path]::GetFullPath($PublishDirectory)
$bootstrapPublish = [IO.Path]::GetFullPath($BootstrapDirectory)
if (!(Test-Path -LiteralPath (Join-Path $game 'x64\This War of Mine.exe'))) { throw '指定目录不是受支持的游戏安装目录' }
if (!(Test-Path -LiteralPath (Join-Path $publish 'MaxyModLoader.exe'))) { throw '发布目录缺少MaxyModLoader.exe请先执行单文件自包含发布' }
if (!(Test-Path -LiteralPath (Join-Path $bootstrapPublish 'MaxyModLoader.Bootstrap.exe'))) { throw '引导发布目录缺少MaxyModLoader.Bootstrap.exe请先执行GUI单文件发布' }

#将启动器放入专属子目录并保留已有备份日志和运行记录
$app = Join-Path $game 'MaxyModLoader\app'
New-Item -ItemType Directory -Path $app -Force | Out-Null
#旧版多文件自包含部署只保留新入口所需的应用文件
Get-ChildItem -LiteralPath $app -File | Where-Object {
    $_.Extension -in @('.dll', '.pdb', '.json') -or $_.Name -eq 'createdump.exe'
} | Remove-Item -Force
#只复制单文件程序和第三方许可避免把调试文件或旧运行时带进游戏
Copy-Item -LiteralPath (Join-Path $publish 'MaxyModLoader.exe') -Destination (Join-Path $app 'MaxyModLoader.exe') -Force
Copy-Item -LiteralPath (Join-Path $bootstrapPublish 'MaxyModLoader.Bootstrap.exe') -Destination (Join-Path $app 'MaxyModLoader.Bootstrap.exe') -Force
if (Test-Path -LiteralPath (Join-Path $publish 'ThirdPartyNotices.txt')) {
    Copy-Item -LiteralPath (Join-Path $publish 'ThirdPartyNotices.txt') -Destination (Join-Path $app 'ThirdPartyNotices.txt') -Force
}
[IO.File]::WriteAllText((Join-Path $app '.self-contained'), "single-file`n", [Text.UTF8Encoding]::new($false))

#复用游戏已有的Mods目录其中的ZIP由启动器读取
$mods = Join-Path $game 'Mods'
New-Item -ItemType Directory -Path $mods -Force | Out-Null

#使用加载器本身替换经过指纹核验的原生游戏入口
$loader = Join-Path $app 'MaxyModLoader.exe'
& $loader prepare-host $game
if ($LASTEXITCODE -ne 0) { throw "窗口辅助入口安装失败退出码：$LASTEXITCODE" }
& $loader install-wrapper $game
if ($LASTEXITCODE -ne 0) { throw "游戏入口安装失败退出码：$LASTEXITCODE" }

#只移除旧版安装器生成且内容完全一致的脚本
$launcher = Join-Path $game '使用MaxyModLoader启动.cmd'
$content = "@echo off`r`n`"%~dp0MaxyModLoader\app\MaxyModLoader.exe`" play `"%~dp0`"`r`nif errorlevel 1 pause`r`n"
if ((Test-Path -LiteralPath $launcher) -and [IO.File]::ReadAllText($launcher) -ceq $content) { Remove-Item -LiteralPath $launcher -Force }
Write-Output "已安装启动器：$app"
Write-Output "请将模组ZIP放入：$mods"
Write-Output '之后直接从Steam或原游戏入口启动'
