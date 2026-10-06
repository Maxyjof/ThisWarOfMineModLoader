import json
import pathlib

from lupa.lua51 import LuaRuntime


ROOT = pathlib.Path(__file__).resolve().parents[1]
MODS = ROOT / "mods"

#疲劳搜刮模组保留原版入口并按角色状态应用共享规则
lua = LuaRuntime(unpack_returned_tuples=True)
lua.execute('''
mode = "balanced"
tired = 10
action = {Duration = 8}
ScavengeAction = {OnBegin = function(self, user) self.Duration = 8; return true end}
context = {
    rules = {
        define = function(name, definition) assert(name == "fatigue_mode" and definition.default == "balanced") end,
        get = function(name) assert(name == "fatigue_mode"); return mode end
    },
    wrap = function(target, key, wrapper)
        local previous = target[key]
        target[key] = function(...) return wrapper(previous, ...) end
    end,
    log = function() end
}
''')
chunk = lua.execute("return function()\n" + (MODS / "scavenger-tactics" / "main.lua").read_text(encoding="utf-8") + "\nend")
chunk().on_load(lua.globals().context)
lua.execute('''
local actor = {GetParameterValue = function(self, key) assert(key == "Tired"); return tired end}
assert(ScavengeAction.OnBegin(action, actor) == true and action.Duration == 7.2)
tired = 85
action.Duration = 8
ScavengeAction.OnBegin(action, actor)
assert(action.Duration == 9.6)
mode = "careful"
action.Duration = 8
ScavengeAction.OnBegin(action, actor)
assert(action.Duration == 9.6)
mode = "urgent"
action.Duration = 8
ScavengeAction.OnBegin(action, actor)
assert(action.Duration == 6)
ScavengeAction.OnBegin = function(self) self.Duration = 0; return false end
action.Duration = 8
assert(ScavengeAction.OnBegin(action, actor) == false and action.Duration == 0)
''')

#轮休模组只在庇护所的第三游戏日执行并持久化处理日期
lua = LuaRuntime(unpack_returned_tuples=True)
lua.execute('''
handlers, stored, changed = {}, {}, {}
phase, day, scavenging = "shelter", 2, false
values = {20, 60, 40}
context = {
    events = {on = function(name, callback) handlers[name] = callback end},
    storage = {
        get = function(key, default) local value = stored[key]; if value == nil then return default end; return value end,
        set = function(key, value) stored[key] = value end
    },
    game = {
        phase = {current = function() return phase end},
        scene = {state = function() return {day = day, character_count = #values} end},
        characters = {get = function(index)
            return {
                name = "幸存者" .. tostring(index),
                get_parameter = function(key) assert(key == "Tired"); return values[index + 1] end,
                set_parameter = function(key, value) assert(key == "Tired"); changed[#changed + 1] = index; values[index + 1] = value end
            }
        end}
    },
    log = function() end
}
''')
chunk = lua.execute("return function()\n" + (MODS / "camp-rotation" / "main.lua").read_text(encoding="utf-8") + "\nend")
chunk().on_load(lua.globals().context)
lua.execute('''
handlers["game.day.begin"]({}, false)
assert(#changed == 0)
day = 3
handlers["game.day.begin"]({}, false)
assert(#changed == 1 and changed[1] == 1 and values[2] == 48 and stored.last_rotation_day == 3)
handlers["game.day.begin"]({}, false)
assert(#changed == 1)
day = 6
handlers["game.day.begin"]({}, true)
assert(#changed == 1)
phase = "other"
handlers["game.day.begin"]({}, false)
assert(#changed == 1)
''')

#MCP模组限制阶段和参数并只暴露明确注册的动作
lua = LuaRuntime(unpack_returned_tuples=True)
lua.execute('''
phase, fatigue, selected_mode = "shelter", 60, "balanced"
tools = {}
context = {
    game = {
        phase = {current = function() return phase end},
        scene = {state = function() return {day = 8, character_count = 1} end},
        characters = {get = function(index)
            assert(index == 0)
            return {
                name = "Pavle",
                get_parameter = function(key) return ({Hungry = 10, Tired = fatigue, Sick = 0, Wounded = 0})[key] end,
                set_parameter = function(key, value) assert(key == "Tired"); fatigue = value end
            }
        end}
    },
    actions = {register = function(name, definition, callback) tools[name] = {definition = definition, callback = callback} end},
    rules = {set = function(provider, name, value)
        assert(provider == "twom.play.scavenger-tactics" and name == "fatigue_mode")
        selected_mode = value
    end}
}
''')
chunk = lua.execute("return function()\n" + (MODS / "camp-advisor" / "main.lua").read_text(encoding="utf-8") + "\nend")
chunk().on_load(lua.globals().context)
lua.execute('''
local report = tools.shelter_report.callback({})
assert(report.day == 8 and report.survivors[1].name == "Pavle" and report.survivors[1].tired == 60)
local rested = tools.rest_survivor.callback({index = 0, recovery = 15})
assert(rested.previous == 60 and rested.current == 45 and fatigue == 45)
assert(not pcall(tools.rest_survivor.callback, {index = 1, recovery = 15}))
assert(not pcall(tools.rest_survivor.callback, {index = 0, recovery = 99}))
assert(tools.set_scavenge_mode.callback({mode = "urgent"}).mode == "urgent" and selected_mode == "urgent")
assert(not pcall(tools.set_scavenge_mode.callback, {mode = "cheat"}))
phase = "other"
assert(not pcall(tools.shelter_report.callback, {}))
''')

#三个模组清单必须保持当前契约并声明正确依赖
for mod_id, dependencies in (
    ("twom.play.scavenger-tactics", {"twom.play.bridge"}),
    ("twom.play.camp-rotation", {"twom.play.bridge"}),
    ("twom.play.camp-advisor", {"twom.play.bridge", "twom.play.scavenger-tactics"}),
):
    folder = next(path for path in MODS.iterdir() if path.is_dir() and json.loads((path / "mod.json").read_text(encoding="utf-8"))["id"] == mod_id)
    manifest = json.loads((folder / "mod.json").read_text(encoding="utf-8"))
    assert set(manifest["dependencies"]) == dependencies
    assert (folder / manifest["descriptionFile"]).is_file()

print("通过：动态疲劳搜刮、持久营地轮休、游戏状态MCP指挥工具")
