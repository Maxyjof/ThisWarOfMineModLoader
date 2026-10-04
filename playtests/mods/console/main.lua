return {
    --<summary>
    --绑定真实游戏快捷键以显示测试状态和记录角色快照
    --</summary>
    on_load = function(context)
        local telemetry = context.services.get("twom.play.bridge", "telemetry")
        local journal = context.services.get("twom.play.chronicle", "journal")
        TWOMPlaytestCommands = {}
        --<summary>
        --显示控制台状态并读取当前原生场景中的角色数据
        --</summary>
        function TWOMPlaytestCommands.status()
            --只有玩家实际触发快捷键时才显示游戏控制台
            gConsole:Show()
            telemetry.snapshot(gScene, "keyboard.J")
            telemetry.record("keyboard.status", {journal_entries = #journal.entries})
        end
        --<summary>
        --记录快照而不打开控制台以方便玩法场景测试
        --</summary>
        function TWOMPlaytestCommands.snapshot()
            telemetry.snapshot(gScene, "keyboard.K")
            telemetry.record("keyboard.snapshot", {})
        end
        --使用游戏原有BindKey接口绑定未用于移动的两个字母键
        bind("J", "TWOMPlaytestCommands.status()")
        bind("K", "TWOMPlaytestCommands.snapshot()")
        telemetry.record("keyboard.bound", {keys = "J,K"})
    end
}
