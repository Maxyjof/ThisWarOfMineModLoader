import pathlib
import sys
import os
import tempfile

#测试依赖由tests/requirements.txt统一提供
from lupa.lua51 import LuaRuntime

#使用Lua5.1执行实际运行库覆盖入口、依赖失败、事件与包装链
runtime = LuaRuntime()
messages = []
runtime.globals().print = messages.append
runtime.execute("assert = function() return nil end")
runtime.execute(pathlib.Path("MaxyModLoader.Core/Runtime/bootstrap.lua").read_text(encoding="utf-8"))
runtime.execute(r'''
--测试断言独立于被游戏覆盖的全局assert
local assert = function(value, message)
    if not value then error(message or "test assertion failed") end
    return value
end

--运行库只公开当前品牌入口不提供旧名称别名
assert(MaxyModLoader.name == "MaxyModLoader" and TWOMLoader == nil)
--缺少元数据注册阶段的调用必须直接失败
local registered = pcall(MaxyModLoader.load_mod, "missing.metadata", "return {}", {}, {})
assert(not registered and MaxyModLoader.mod_by_id["missing.metadata"] == nil)
--<summary>
--先注册测试元数据再按当前契约加载入口
--</summary>
local function load_test_mod(id, source, dependencies, options)
    --每个测试入口都明确经过注册阶段
    MaxyModLoader.register_mod({id = id, name = id, enabled = true, description_document = {}})
    --测试按当前契约提供完整选项表省略测试参数时仅在替身中补齐
    options = options or {}
    options.config, options.modules, options.capabilities = options.config or {}, options.modules or {}, options.capabilities or {}
    return MaxyModLoader.load_mod(id, source, dependencies or {}, options)
end
assert(MaxyModLoader.api_version == "1.1.0")
MaxyModLoader.register_mod({id="disabled.metadata", name="禁用介绍", enabled=false,
    description_document={{kind="paragraph", runs={{text="中文介绍与Unicode字符保持完整"}}}}, features={"功能一", "功能二"}})
assert(MaxyModLoader.mod_by_id["disabled.metadata"].status == "disabled")
--旧元数据和省略加载选项的调用均须拒绝不自动补全
assert(not pcall(MaxyModLoader.register_mod, {id="missing.document", description="plain text"}))
assert(MaxyModLoader.mod_by_id["missing.document"] == nil)
assert(not pcall(MaxyModLoader.load_mod, "disabled.metadata", "return {}", {}))

--测试事件异常隔离、取消订阅和尾部nil参数
count = 0
assert(load_test_mod("events", [[return {on_load = function(c)
    c.events.on("sample", function() error("expected callback failure") end)
    c.events.on("sample", function(...) assert(select("#", ...) == 3); count = count + 1 end)
    local cancel = c.events.on("sample", function() count = count + 100 end)
    cancel()
end}]], {}))
MaxyModLoader.emit("sample", 1, nil, nil)
assert(count == 1)

--验证游戏日任务计数取消周期执行与输入边界
scheduled_once, scheduled_repeat = 0, 0
schedule_scene = {}
assert(load_test_mod("schedule", [[return {on_load = function(c)
    c.schedule.after_days(2, function(scene, was_scavenging)
        scheduled_once = scheduled_once + 1
        schedule_scene.scene, schedule_scene.was_scavenging = scene, was_scavenging
    end)
    c.schedule.every_days(2, function() scheduled_repeat = scheduled_repeat + 1 end)
end}]], {}))
local game_scene = {}
MaxyModLoader.emit("game.day.begin", game_scene, true)
assert(scheduled_once == 0 and scheduled_repeat == 0)
MaxyModLoader.emit("game.day.begin", game_scene, true)
assert(scheduled_once == 1 and scheduled_repeat == 1)
MaxyModLoader.emit("game.day.begin", game_scene, false)
assert(scheduled_once == 1 and scheduled_repeat == 1)
MaxyModLoader.emit("game.day.begin", game_scene, false)
assert(scheduled_once == 1 and scheduled_repeat == 2)
assert(schedule_scene.scene == game_scene and schedule_scene.was_scavenging == true)
assert(load_test_mod("schedule.invalid", [[return {on_load = function(c)
    assert(not pcall(function() c.schedule.after_days(0, function() end) end))
    assert(not pcall(function() c.schedule.every_days(1.5, function() end) end))
    assert(not pcall(function() c.schedule.after_days(1, nil) end))
end}]], {}))

--测试包装顺序、多返回值和失败入口的包装撤销
target = {value = function(x) return x, nil, "tail" end}
assert(load_test_mod("wrap.a", [[return {on_load = function(c)
    c.wrap(target, "value", function(previous, x) return previous(x + 2) end)
end}]], {}))
assert(load_test_mod("wrap.b", [[return {on_load = function(c)
    c.wrap(target, "value", function(previous, x) return previous(x * 3) end)
end}]], {"wrap.a"}))
assert(not load_test_mod("broken", [[return {on_load = function(c)
    c.wrap(target, "value", function(previous, x) return previous(x + 100) end)
    c.events.on("after.failure", function() error("must not run") end)
    error("expected entry failure")
end}]], {}))
local first, middle, last = target.value(4)
assert(first == 14 and middle == nil and last == "tail")
assert(not MaxyModLoader.loaded.broken)
MaxyModLoader.emit("after.failure")

--测试依赖入口失败时依赖方不执行与错误入口协议
assert(not load_test_mod("dependent", [[error("must not execute")]], {"broken"}))
assert(not MaxyModLoader.loaded.dependent)
--失败和跳过是不同状态管理界面不能只展示静态启用值
assert(MaxyModLoader.mod_by_id.broken.status == "failed")
assert(MaxyModLoader.mod_by_id.dependent.status == "skipped")
assert(MaxyModLoader.mod_by_id.events.status == "loaded")
assert(not load_test_mod("syntax", "this is not lua", {}))
assert(not load_test_mod("protocol", "return 123", {}))
assert(not load_test_mod("invalid.subscription", [[return {on_load = function(c)
    c.events.on("event", nil)
end}]], {}))

--测试事件回调内新增订阅不会进入当前广播快照
dynamic = 0
assert(load_test_mod("dynamic", [[return {on_load = function(c)
    c.events.on("dynamic", function()
        dynamic = dynamic + 1
        c.events.on("dynamic", function() dynamic = dynamic + 10 end)
    end)
end}]], {}))
MaxyModLoader.emit("dynamic")
assert(dynamic == 1)
MaxyModLoader.emit("dynamic")
assert(dynamic == 12)

--验证内部模块仅执行一次并将配置注入模组上下文
module_runs = 0
assert(load_test_mod("modular", [[return {on_load = function(c)
    local a = c.require("helper")
    local b = c.require("helper")
    if a ~= b or a.value ~= 7 then error("module cache/config mismatch") end
    c.services.provide("calculator", a)
end}]], {}, {config = {value = 7}, modules = {
    helper = "local c = ...; module_runs = module_runs + 1; return {value = c.config.value}"
}}))
assert(module_runs == 1)
assert(load_test_mod("consumer", [[return {on_load = function(c)
    if c.services.get("modular", "calculator").value ~= 7 then error("missing service") end
end}]], {"modular"}))
assert(not load_test_mod("undeclared", [[return {on_load = function(c)
    c.services.get("modular", "calculator")
end}]], {}))

--验证循环内部模块和失败服务提供者的隔离
assert(not load_test_mod("module.cycle", [[return {on_load = function(c) c.require("a") end}]], {}, {modules = {
    a = "local c = ...; return c.require('b')", b = "local c = ...; return c.require('a')"
}}))
assert(not load_test_mod("service.failure", [[return {on_load = function(c)
    c.services.provide("bad", {}); error("expected service failure")
end}]], {}))
assert(not load_test_mod("service.dependent", [[return {on_load = function(c)
    error("must not execute")
end}]], {"service.failure"}))

--验证模组可声明受类型范围约束的规则并由显式依赖调整
rule_notifications = 0
assert(load_test_mod("rules.provider", [[return {on_load = function(c)
    c.rules.define("fatigue_rate", {type = "number", default = 0.5, minimum = 0, maximum = 1,
        description = "每日疲劳增长倍率"})
    c.rules.define("supply_mode", {type = "string", default = "balanced", values = {"scarce", "balanced", "abundant"}})
    c.rules.define("enabled", {type = "boolean", default = true})
    if c.rules.get("fatigue_rate") ~= 0.5 then error("rule default mismatch") end
end}]], {}))
assert(load_test_mod("rules.tuner", [[return {on_load = function(c)
    c.rules.on_change("rules.provider", "fatigue_rate", function(value, previous)
        if value == 0.75 and (previous == 0.5 or previous == 0.2) then rule_notifications = rule_notifications + 1 end
    end)
    c.rules.set("rules.provider", "fatigue_rate", 0.75)
    c.rules.set("rules.provider", "supply_mode", "abundant")
    local accepted = pcall(function() c.rules.set("rules.provider", "fatigue_rate", 3) end)
    if accepted or c.rules.get("rules.provider", "fatigue_rate") ~= 0.75 then error("rule range was not enforced") end
    local accepted_choice = pcall(function() c.rules.set("rules.provider", "supply_mode", "impossible") end)
    if accepted_choice then error("rule choices were not enforced") end
    if c.rules.get("rules.provider", "enabled") ~= true then error("boolean rule mismatch") end
end}]], {"rules.provider"}))
assert(rule_notifications == 1)
assert(not load_test_mod("rules.unauthorized", [[return {on_load = function(c)
    c.rules.set("rules.provider", "fatigue_rate", 0.2)
end}]], {}))
assert(not load_test_mod("rules.failed_writer", [[return {on_load = function(c)
    c.rules.set("rules.provider", "fatigue_rate", 0.2)
    error("expected writer rollback")
end}]], {"rules.provider"}))
assert(MaxyModLoader.loaded["rules.provider"] and MaxyModLoader.loaded["rules.tuner"])
assert(rule_notifications == 2)
local rule_rows = MaxyModLoader.rule_snapshot()
assert(#rule_rows == 3 and rule_rows[1].id == "rules.provider:enabled")
assert(rule_rows[2].value == 0.75 and rule_rows[3].value == "abundant")
assert(not load_test_mod("rules.rollback", [[return {on_load = function(c)
    c.rules.define("temporary", {type = "boolean", default = true})
    error("expected rule rollback")
end}]], {}))
for _, rule in ipairs(MaxyModLoader.rule_snapshot()) do assert(rule.id ~= "rules.rollback:temporary") end

--验证模组MCP动作必须获权且只接收符合声明模式的标量参数
assert(not load_test_mod("action.denied", [[return {on_load = function(c)
    c.actions.register("echo", {description = "未授权", properties = {}}, function() return true end)
end}]], {}))
assert(load_test_mod("action.echo", [[return {on_load = function(c)
    c.actions.register("echo", {description = "回声工具", properties = {
        text = {type = "string"}, count = {type = "integer"}, active = {type = "boolean"}
    }, required = {"text"}, read_only = true}, function(arguments)
        return {text = arguments.text, count = arguments.count or 1, active = arguments.active or false}
    end)
end}]], {}, {capabilities = {"mcp.tools"}}))
local action_rows = MaxyModLoader.actions.list()
assert(#action_rows == 1 and action_rows[1].id == "action.echo:echo" and action_rows[1].readOnly)
local action_result = MaxyModLoader.actions.call("action.echo:echo", {text = "中文\n消息", count = 2, active = true})
assert(action_result.text == "中文\n消息" and action_result.count == 2 and action_result.active)
assert(not pcall(MaxyModLoader.actions.call, "action.echo:echo", {text = "ok", invalid = true}))
assert(not pcall(MaxyModLoader.actions.call, "action.echo:echo", {text = "ok", count = 1.5}))
assert(not load_test_mod("action.rollback", [[return {on_load = function(c)
    c.actions.register("hidden", {description = "失败动作", properties = {}}, function() return true end)
    error("expected action rollback")
end}]], {}, {capabilities = {"mcp.tools"}}))
assert(#MaxyModLoader.actions.list() == 1)
''')
assert any("expected entry failure" in message for message in messages)
assert any("expected callback failure" in message for message in messages)
assert any("invalid event subscription" in message for message in messages)
print("通过：Lua5.1入口隔离、规则API、模组MCP动作、事件与服务隔离")

#用最小主线程替身验证内置桥对模组动作的实际解析和分发
runtime.execute("LuaGameDelegate = {OnTick = function() end, OnPauseTick = function() end}; gGame = {}; gGameDelegate = {}")
runtime.execute(pathlib.Path("MaxyModLoader.Core/Runtime/mcp.lua").read_text(encoding="utf-8"))
runtime.execute(r'''
--模组动作列表仅展示有权限且已成功加载的动作
local actions = MaxyModLoader.mcp.dispatch("mod_actions_list").actions
assert(#actions == 1 and actions[1].id == "action.echo:echo")
--结构参数在Lua侧解码后仍经过动作注册表的模式校验
local result = MaxyModLoader.mcp.dispatch("mod_action_call", "action.echo:echo\n616374697665|b|74727565\n636f756e74|n|32\n74657874|s|e4bda0e5a5bd0ae5b9b8e5ad98e88085\n")
local expected = string.char(228,189,160,229,165,189,10,229,185,184,229,173,152,232,128,133)
assert(result.result.text == expected and result.result.count == 2 and result.result.active)
''')
print("通过：Lua5.1内置游戏桥解析并分发模组MCP动作")

#额外验证由C#工具生成的完整入口源码实际可以执行
bundle = pathlib.Path(sys.argv[1] if len(sys.argv) > 1 else "artifacts/tests/bundle.lua")
if bundle.exists():
    compiled = LuaRuntime()
    compiled.globals().print = messages.append
    compiled.execute("gLua = {ResetReplication = function() end, ExecuteFile = function() end}")
    compiled.execute(bundle.read_text(encoding="utf-8"))
    assert compiled.globals().MaxyModLoader.loaded["twom.hello"]
    print("通过：C#生成入口在Lua5.1中加载示例模组")
else:
    raise FileNotFoundError("请先运行C#测试生成完整Lua入口")

#用独立游戏目录验证持久数据API的隔离保存、跨虚拟机读取和损坏恢复
repository = pathlib.Path.cwd()
storage_example = (repository / "examples/persistent-storage/main.lua").read_text(encoding="utf-8")
with tempfile.TemporaryDirectory(prefix="mml-storage-") as storage_root:
    os.chdir(storage_root)
    pathlib.Path("MaxyModLoader/storage").mkdir(parents=True)
    storage_messages = []
    for expected_count in (1, 2):
        storage_runtime = LuaRuntime()
        storage_runtime.globals().print = storage_messages.append
        storage_runtime.execute((repository / "MaxyModLoader.Core/Runtime/bootstrap.lua").read_text(encoding="utf-8"))
        storage_runtime.globals().storage_example_source = storage_example
        storage_runtime.execute(r'''
--注册并执行真实示例以确认每次启动会读取并递增已有记录
MaxyModLoader.register_mod({id="twom.storage.example", name="持久存储示例", enabled=true, description_document={}})
local loaded = MaxyModLoader.load_mod("twom.storage.example", storage_example_source, {},
    {config={}, modules={}, capabilities={}})
if not loaded then error(MaxyModLoader.mod_by_id["twom.storage.example"].error) end
''')
        assert any(f"示例启动次数：{expected_count}" in message for message in storage_messages)

    #校验标量类型、数据隔离、输入边界、复制语义和幂等操作
    storage_runtime.execute(r'''
MaxyModLoader.register_mod({id="twom.storage.contract", name="持久存储契约测试", enabled=true, description_document={}})
local source = [[return {
--<summary>
--验证模组持久存储的类型校验和复制语义
--</summary>
on_load=function(context)
    if context.storage.get("launch_count") ~= nil then error("storage leaked across mod IDs") end
    context.storage.set("note", "中文换行\n内容")
    context.storage.set("ratio", 1.25)
    context.storage.set("enabled", false)
    if context.storage.get("ratio") ~= 1.25 or context.storage.get("enabled", true) ~= false then error("scalar round trip failed") end
    if context.storage.set("ratio", 1.25) then error("unchanged value should not write") end
    local values = context.storage.all()
    values.ratio = 99
    if context.storage.get("ratio") ~= 1.25 then error("all exposed the internal table") end
    if not context.storage.delete("enabled") or context.storage.delete("enabled") then error("delete idempotency failed") end
    if pcall(context.storage.set, "Uppercase", true) then error("invalid key was accepted") end
    if pcall(context.storage.set, "nested", {}) then error("table value was accepted") end
    if pcall(context.storage.set, "not_finite", 0 / 0) then error("NaN was accepted") end
    if pcall(context.storage.set, "too_long", string.rep("x", 4097)) then error("oversized text was accepted") end
end}]]
local loaded = MaxyModLoader.load_mod("twom.storage.contract", source, {}, {config={}, modules={}, capabilities={}})
if not loaded then error(MaxyModLoader.mod_by_id["twom.storage.contract"].error) end
''')
    contract_runtime = LuaRuntime()
    contract_runtime.globals().print = storage_messages.append
    contract_runtime.execute((repository / "MaxyModLoader.Core/Runtime/bootstrap.lua").read_text(encoding="utf-8"))
    contract_runtime.execute(r'''
--重新创建虚拟机确认字符串和数字已持久化且删除状态也已保存
MaxyModLoader.register_mod({id="twom.storage.contract", name="持久存储读取测试", enabled=true, description_document={}})
local source = [[return {
--<summary>
--确认新的Lua虚拟机可以读取已保存数据
--</summary>
on_load=function(context)
    if context.storage.get("note") ~= "中文换行\n内容" or context.storage.get("ratio") ~= 1.25 then error("stored values were not restored") end
    if context.storage.get("enabled") ~= nil then error("deleted value was restored") end
end}]]
local loaded = MaxyModLoader.load_mod("twom.storage.contract", source, {}, {config={}, modules={}, capabilities={}})
if not loaded then error(MaxyModLoader.mod_by_id["twom.storage.contract"].error) end
''')

    #第一份快照写入失败时第二份仍应保存最后一次完整状态
    slots = [pathlib.Path(f"MaxyModLoader/storage/twom.storage.example.{slot}.dat") for slot in (0, 1)]
    assert all(path.exists() for path in slots)
    generations = [int(path.read_text(encoding="ascii").split("\t", 1)[1].splitlines()[0]) for path in slots]
    newest = generations.index(max(generations))
    slots[newest].write_text("damaged current snapshot", encoding="ascii")
    recovered_runtime = LuaRuntime()
    recovered_runtime.globals().print = storage_messages.append
    recovered_runtime.execute((repository / "MaxyModLoader.Core/Runtime/bootstrap.lua").read_text(encoding="utf-8"))
    recovered_runtime.execute(r'''
--有效槽损坏时使用另一份完整快照并继续接受当前契约写入
MaxyModLoader.register_mod({id="twom.storage.example", name="持久存储恢复测试", enabled=true, description_document={}})
local source = [[return {
--<summary>
--确认有效数据槽损坏时可以恢复上一份快照
--</summary>
on_load=function(context)
    if context.storage.get("launch_count") ~= 1 then error("recovery did not select the previous snapshot") end
    context.storage.set("recovered", true)
end}]]
local loaded = MaxyModLoader.load_mod("twom.storage.example", source, {}, {config={}, modules={}, capabilities={}})
if not loaded then error(MaxyModLoader.mod_by_id["twom.storage.example"].error) end
''')
    assert any("持久数据槽损坏" in message for message in storage_messages)
    os.chdir(repository)

print("通过：Lua5.1模组持久存储、跨启动读取与双槽损坏恢复")
