param(
    [Parameter(Mandatory = $true)][string]$GameDirectory
)
$ErrorActionPreference = 'Stop'

#原生崩溃转储配置写入Windows全局WER注册表因此要求管理员权限
$identity = [Security.Principal.WindowsIdentity]::GetCurrent()
$principal = [Security.Principal.WindowsPrincipal]::new($identity)
if (!$principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw '请在管理员PowerShell中运行此命令'
}

#只配置加载器保存的原版游戏进程并把转储限制在游戏目录
$game = [IO.Path]::GetFullPath($GameDirectory)
$executable = Join-Path $game 'x64\MaxyModLoader.Original.exe'
if (!(Test-Path -LiteralPath $executable)) { throw '找不到MaxyModLoader.Original.exe请先安装当前加载器' }
$dumpFolder = Join-Path $game 'MaxyModLoader\crash-dumps'
New-Item -ItemType Directory -Path $dumpFolder -Force | Out-Null
$key = 'HKLM:\SOFTWARE\Microsoft\Windows\Windows Error Reporting\LocalDumps\MaxyModLoader.Original.exe'
New-Item -Path $key -Force | Out-Null
New-ItemProperty -Path $key -Name DumpFolder -PropertyType ExpandString -Value $dumpFolder -Force | Out-Null
New-ItemProperty -Path $key -Name DumpCount -PropertyType DWord -Value 3 -Force | Out-Null
New-ItemProperty -Path $key -Name DumpType -PropertyType DWord -Value 1 -Force | Out-Null
Write-Output "已为游戏原生进程启用最多3份小型崩溃转储：$dumpFolder"
