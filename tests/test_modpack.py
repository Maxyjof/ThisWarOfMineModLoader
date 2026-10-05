import pathlib

#独立解释器中的宿主替身只验证模组组合逻辑实际游戏证据单独记录
from lupa.lua51 import LuaRuntime

runtime = LuaRuntime()
messages = []
runtime.globals().print = messages.append
runtime.execute(r'''
--创建最小宿主边界测试替身不把这些对象作为实际游戏实装证据
assert = function() end
gConsole = {Show = function() end}
bind = function() end
igParams = {ScavengeConfig = {NormalScavenge = {Duration = 6}, LightScavenge = {Duration = 2}}}
igKosovoParamDefinition = {Hungry = {Tick = {[0] = {Hungry = 20}}}}
igStaminaRunConsumption, igStaminaWalkConsumption = 30, 10
local values = {Hungry = 0, Tired = 30, Sick = 0, Wounded = 0}
dweller = {GetDwellerName = function() return "test-dweller" end,
    GetParameterValue = function(self, key) return values[key] or 0 end,
    SetParameterValue = function(self, key, value) values[key] = value end,
    SolveParameterDependency = function() end,
    AddItems = function(self, name, amount) food = food + amount end}
food = 0
gKosovoGlobalState = {GetGlobalItemCount = function() return food end}
KosovoScene = {OnAfterInit = function() end, OnDayBegin = function() end, OnEndDay = function() end}
gScene = {GetDwellerCount = function() return 1 end, GetCurrentDay = function() return 1 end,
    GetDweller = function() return dweller end}
component = {GetMyHost = function() return dweller end}
KosovoDwellerControllerComponent = {OnAfterInit = function() end}
KosovoParamComponent = {TickParameters = function(self)
    dweller:SetParameterValue("Hungry", dweller:GetParameterValue("Hungry") + igKosovoParamDefinition.Hungry.Tick[0].Hungry)
end}
ScavengeAction = {OnBegin = function(self) self.Duration = igParams.ScavengeConfig[self.ScavengeType].Duration; return true end,
    OnComplete = function() end}
UseCrafter = {OnBeginCrafting = function() end}
KosovoCraftingBaseComponent = {OnCraftingComplete = function() end}
''')
runtime.execute(pathlib.Path("artifacts/tests/playtest-bundle.lua").read_text(encoding="utf-8"))

#运行宿主边界回调检验九模组组合后的真实状态读写路径与缓存
runtime.execute(r'''
local check = function(value) if not value then error("test assertion failed") end end
check(MaxyModLoader.loaded["twom.play.survival-camp"])
check(MaxyModLoader.loaded["twom.play.chronicle"])
check(not MaxyModLoader.loaded["twom.play.failure"])
check(not MaxyModLoader.loaded["twom.play.failure-dependent"])
check(not TWOMPlaytestUnexpectedDependent)
check(igParams.ScavengeConfig.NormalScavenge.Duration == 3)
check(igStaminaRunConsumption == 15 and igStaminaWalkConsumption == 5)
KosovoDwellerControllerComponent.OnAfterInit(component)
check(dweller:GetParameterValue("Hungry") == -10)
KosovoScene.OnDayBegin(gScene, false)
check(food == 2)
KosovoScene.OnDayBegin(gScene, false)
check(food == 2)
action = {ScavengeType = "NormalScavenge", Timer = 3}
check(ScavengeAction.OnBegin(action, dweller))
check(action.Duration == 3)
ScavengeAction.OnComplete(action, 0)
UseCrafter.OnBeginCrafting({}, true)
KosovoCraftingBaseComponent.OnCraftingComplete({})
KosovoParamComponent.TickParameters(component)
check(dweller:GetParameterValue("Hungry") == 0)
KosovoScene.OnEndDay(gScene)
TWOMPlaytestCommands.status()
''')
assert any("chronicle.summary" in message for message in messages)
assert any("scavenge.complete" in message for message in messages)
assert any("craft.complete" in message for message in messages)
assert any("skipped: dependency failed" in message for message in messages)
print("通过：九模组组合、模块缓存、配置、服务、状态读写、一次性补给、失败隔离和共享函数包装")
