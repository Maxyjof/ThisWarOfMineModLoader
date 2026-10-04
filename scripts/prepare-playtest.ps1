param(
    [Parameter(Mandatory = $true)][string]$GameDirectory,
    [ValidateSet('baseline', 'combined')][string]$Mode = 'combined'
)

#从脚本位置确定仓库路径所有实验输出都保存在忽略的本机目录
$ErrorActionPreference = 'Stop'
$taskRepository = Split-Path -Parent $PSScriptRoot
$taskRunName = $Mode + '-' + (Get-Date -Format 'yyyyMMdd-HHmmss')
$taskRunRoot = Join-Path $taskRepository ('artifacts\playtests\' + $taskRunName)
$taskModRoot = Join-Path $taskRunRoot 'mods'
New-Item -ItemType Directory -Path $taskModRoot | Out-Null

#基线只装观察和快捷键模组组合测试装入全部九个模组
$taskNames = if ($Mode -eq 'baseline') { @('bridge', 'chronicle', 'console') } else {
    @('bridge', 'chronicle', 'console', 'fast-scavenge', 'stamina', 'wellbeing', 'survival-camp', 'failure', 'failure-dependent')
}
foreach ($taskName in $taskNames) {
    Copy-Item -LiteralPath (Join-Path $taskRepository ('playtests\mods\' + $taskName)) -Destination $taskModRoot -Recurse
}

#运行标识编入配置便于区分两轮真实游戏记录
$taskBridgeManifest = Join-Path $taskModRoot 'bridge\mod.json'
$taskBridge = Get-Content -LiteralPath $taskBridgeManifest -Raw | ConvertFrom-Json
$taskBridge.settings.run_id = $taskRunName
$taskBridge | ConvertTo-Json -Depth 16 | Set-Content -LiteralPath $taskBridgeManifest -Encoding utf8

#只生成部署包不自动改变安装或存档状态
$taskSource = Join-Path $GameDirectory 'common'
$taskPackage = Join-Path $taskRunRoot 'package'
dotnet run --project (Join-Path $taskRepository 'ThisWarOfMineModLoader.Cli') -c Release -- build $taskSource 5faa28a2 $taskModRoot $taskPackage
if ($LASTEXITCODE -ne 0) { throw '构建测试包失败' }
Write-Output $taskPackage
