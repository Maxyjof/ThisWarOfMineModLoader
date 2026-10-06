import pathlib

from lupa.lua51 import LuaRuntime


ROOT = pathlib.Path(__file__).resolve().parents[1]

#创建只包含官方事件适配器的Lua宿主替身
runtime = LuaRuntime()
messages = []
runtime.globals().print = messages.append
runtime.execute(r'''
assert = function(value) if not value then error("test assertion failed") end end
gConsole = {Show = function() end}
bind = function() end
igParams = {ScavengeConfig = {NormalScavenge = {Duration = 6}}}
local values = {Hungry = 0, Tired = 30, Sick = 0, Wounded = 0}
dweller = {GetDwellerName = function() return "test-dweller" end,
    GetParameterValue = function(self, key) return values[key] or 0 end,
    SetParameterValue = function(self, key, value) values[key] = value end,
    SolveParameterDependency = function() end}
KosovoScene = {OnBeforeInit = function() end, OnAfterInit = function() end,
    OnBeforeDayBegin = function() end, OnDayBegin = function() end, OnEndDay = function() end,
    OnEnterScavenge = function() end, OnSaveScavengeState = function() end,
    OnBeforeSwitchScene = function() end, OnRadioBroadcast = function() end,
    NewShelterItemBuilt = function() end}
gScene = {GetDwellerCount = function() return 1 end, GetCurrentDay = function() return 1 end,
    GetDweller = function() return dweller end}
component = {GetMyHost = function() return dweller end}
KosovoDwellerControllerComponent = {OnAfterInit = function() end}
ScavengeAction = {OnBegin = function(self) self.Duration = igParams.ScavengeConfig.NormalScavenge.Duration; return true end,
    OnComplete = function() end}
UseCrafter = {OnBeginCrafting = function() end}
KosovoCraftingBaseComponent = {OnCraftingComplete = function() end}
''')

#使用运行时生成的官方事件适配器包检查加载计划和入口注册
runtime.execute((ROOT / "artifacts" / "tests" / "playtest-bundle.lua").read_text(encoding="utf-8"))

#逐个调用已核验的游戏回调并检查包装前后事件与原函数返回值
runtime.execute(r'''
local check = function(value) if not value then error("test assertion failed") end end
check(MaxyModLoader.loaded["twom.play.bridge"])
KosovoScene.OnBeforeInit(gScene, true, true)
KosovoScene.OnAfterInit(gScene, true)
KosovoScene.OnBeforeDayBegin(gScene, false)
KosovoScene.OnDayBegin(gScene, false)
KosovoScene.OnEndDay(gScene)
KosovoScene.OnEnterScavenge(gScene)
KosovoScene.OnSaveScavengeState(gScene)
KosovoScene.OnRadioBroadcast(gScene)
KosovoScene.NewShelterItemBuilt(gScene, {name = "test-item"})
KosovoScene.OnBeforeSwitchScene(gScene)
KosovoDwellerControllerComponent.OnAfterInit(component)
action = {ScavengeType = "NormalScavenge", Timer = 3}
check(ScavengeAction.OnBegin(action, dweller))
check(action.Duration == 6)
ScavengeAction.OnComplete(action, 0)
UseCrafter.OnBeginCrafting({}, true)
KosovoCraftingBaseComponent.OnCraftingComplete({})
''')

#确认真实回调已被适配器记录
assert any("bridge.ready" in message for message in messages)
assert any("day.begin" in message for message in messages)
assert any("scavenge.begin" in message for message in messages)
assert any("scavenge.complete" in message for message in messages)
assert any("craft.begin" in message for message in messages)
assert any("craft.complete" in message for message in messages)
assert any("scene.ready" in message for message in messages)
assert any("day.begin" in message for message in messages)
assert any("character.ready" in message for message in messages)
print("通过：官方游戏事件适配器、场景生命周期、角色初始化、搜刮与制作记录")
