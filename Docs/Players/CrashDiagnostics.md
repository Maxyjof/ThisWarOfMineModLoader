# 崩溃诊断信息

启动器每次运行游戏后会更新`<游戏目录>/MaxyModLoader/last-run.json`，记录加载器版本、原版程序SHA256、游戏进程PID、启动与退出时间、运行时长、退出码、部署包标识及模组ID。游戏以非零退出码结束时，同一份摘要也会保存在`crash-report.json`。启动流程各阶段及加载器异常堆栈写入`startup.log`，模组运行日志写入`runtime.log`。

退出码`0xC0000005`表示Windows报告了原生访问冲突。摘要可确认进程和部署现场，但无法指出具体故障指令；需要小型转储分析时，可在管理员PowerShell中运行：

```powershell
Set-ExecutionPolicy -Scope Process Bypass
& '路径\到\仓库\tools\configure-crash-dumps.ps1' -GameDirectory 'D:\Steam\steamapps\common\This War of Mine'
```

Windows错误报告会将最多3份原版游戏进程小型转储写入`<游戏目录>/MaxyModLoader/crash-dumps`，新文件会覆盖最旧文件。转储可能包含游戏运行时内存中的数据，请仅在需要诊断时启用，并在提交问题时自行检查内容。此配置使用Windows错误报告的管理员级按程序设置；脚本只配置`MaxyModLoader.Original.exe`，不会修改其他程序的转储规则。

诊断文件都在游戏目录下生成，不属于仓库内容。提交问题时优先提供`crash-report.json`、崩溃时间附近的`startup.log`和`runtime.log`末尾内容；转储仅在需要深入分析原生崩溃时提供。
