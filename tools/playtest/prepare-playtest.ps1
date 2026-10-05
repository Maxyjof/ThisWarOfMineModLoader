param(
    [Parameter(Mandatory = $true)][string]$GameDirectory,
    [ValidateSet('baseline', 'combined', 'content')][string]$Mode = 'combined'
)

#从脚本位置确定仓库路径所有实验输出都保存在忽略的本机目录
$ErrorActionPreference = 'Stop'
$taskRepository = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$taskGameRoot = [IO.Path]::GetFullPath($GameDirectory)
$taskGameSource = Join-Path $taskGameRoot 'common'
$taskTextureSource = Join-Path $taskGameRoot 'textures-s3'
if (-not (Test-Path -LiteralPath ($taskGameSource + '.dat')) -or -not (Test-Path -LiteralPath ($taskGameSource + '.idx'))) { throw '游戏目录缺少common.dat或common.idx' }
if (-not (Test-Path -LiteralPath ($taskTextureSource + '.dat')) -or -not (Test-Path -LiteralPath ($taskTextureSource + '.idx'))) { throw '游戏目录缺少textures-s3.dat或textures-s3.idx' }

$taskRunName = $Mode + '-' + (Get-Date -Format 'yyyyMMdd-HHmmss')
$taskRunRoot = Join-Path $taskRepository ('artifacts\playtests\' + $taskRunName)
$taskModRoot = Join-Path $taskRunRoot 'mods'
New-Item -ItemType Directory -Path $taskModRoot | Out-Null

#按测试目的选择互相隔离的模组集合
$taskNames = switch ($Mode) {
    'baseline' { @('bridge', 'chronicle', 'console') }
    'combined' { @('bridge', 'chronicle', 'console', 'fast-scavenge', 'stamina', 'wellbeing', 'survival-camp', 'failure', 'failure-dependent') }
    'content' { @('bridge', 'chronicle', 'fast-scavenge', 'stamina', 'survival-camp', 'more-guns', 'ammunition-supply', 'field-equipment', 'weapon-trading') }
}

#由清单目录映射确保内容模组与测试模组都复制进同一隔离目录
$taskSources = @{
    'bridge' = 'playtests\mods\bridge'
    'chronicle' = 'playtests\mods\chronicle'
    'console' = 'playtests\mods\console'
    'fast-scavenge' = 'playtests\mods\fast-scavenge'
    'stamina' = 'playtests\mods\stamina'
    'wellbeing' = 'playtests\mods\wellbeing'
    'survival-camp' = 'playtests\mods\survival-camp'
    'failure' = 'playtests\mods\failure'
    'failure-dependent' = 'playtests\mods\failure-dependent'
    'more-guns' = 'mods\more-guns'
    'ammunition-supply' = 'mods\ammunition-supply'
    'field-equipment' = 'mods\field-equipment'
    'weapon-trading' = 'mods\weapon-trading'
}
foreach ($taskName in $taskNames) {
    $taskModSource = Join-Path $taskRepository $taskSources[$taskName]
    Copy-Item -LiteralPath $taskModSource -Destination $taskModRoot -Recurse
}

#MCP控制桥随加载器内置无需复制额外模组

#运行标识编入配置便于区分两轮真实游戏记录
$taskBridgeManifest = Join-Path $taskModRoot 'bridge\mod.json'
$taskBridge = Get-Content -LiteralPath $taskBridgeManifest -Raw | ConvertFrom-Json
$taskBridge.settings.run_id = $taskRunName
$taskBridge | ConvertTo-Json -Depth 16 | Set-Content -LiteralPath $taskBridgeManifest -Encoding utf8

#只生成部署包不自动改变安装或存档状态
$taskPackage = Join-Path $taskRunRoot 'package'
dotnet run --project (Join-Path $taskRepository 'MaxyModLoader.Cli') -c Release -- build $taskGameSource 5faa28a2 $taskModRoot $taskPackage
if ($LASTEXITCODE -ne 0) { throw '构建测试包失败' }
Write-Output $taskPackage
