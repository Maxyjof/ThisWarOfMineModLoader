import pathlib
import sys

#优先使用当前Python兼容的Lupa仅在缺少依赖时回退到本机测试包
try:
    from lupa.lua51 import LuaRuntime
except ImportError:
    sys.path.insert(0, str(pathlib.Path("local/tools").resolve()))
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

--正式名称和兼容别名必须指向同一个运行库
assert(MaxyModLoader == TWOMLoader and MaxyModLoader.name == "MaxyModLoader")
assert(MaxyModLoader.api_version == "1.0.0")
MaxyModLoader.register_mod({id="disabled.metadata", name="禁用介绍", enabled=false,
    description="中文介绍与Unicode字符保持完整", features={"功能一", "功能二"}})
assert(MaxyModLoader.mod_by_id["disabled.metadata"].status == "disabled")

--测试事件异常隔离、取消订阅和尾部nil参数
count = 0
assert(TWOMLoader.load_mod("events", [[return {on_load = function(c)
    c.events.on("sample", function() error("expected callback failure") end)
    c.events.on("sample", function(...) assert(select("#", ...) == 3); count = count + 1 end)
    local cancel = c.events.on("sample", function() count = count + 100 end)
    cancel()
end}]], {}))
TWOMLoader.emit("sample", 1, nil, nil)
assert(count == 1)

--测试包装顺序、多返回值和失败入口的包装撤销
target = {value = function(x) return x, nil, "tail" end}
assert(TWOMLoader.load_mod("wrap.a", [[return {on_load = function(c)
    c.wrap(target, "value", function(previous, x) return previous(x + 2) end)
end}]], {}))
assert(TWOMLoader.load_mod("wrap.b", [[return {on_load = function(c)
    c.wrap(target, "value", function(previous, x) return previous(x * 3) end)
end}]], {"wrap.a"}))
assert(not TWOMLoader.load_mod("broken", [[return {on_load = function(c)
    c.wrap(target, "value", function(previous, x) return previous(x + 100) end)
    c.events.on("after.failure", function() error("must not run") end)
    error("expected entry failure")
end}]], {}))
local first, middle, last = target.value(4)
assert(first == 14 and middle == nil and last == "tail")
assert(not TWOMLoader.loaded.broken)
TWOMLoader.emit("after.failure")

--测试依赖入口失败时依赖方不执行与错误入口协议
assert(not TWOMLoader.load_mod("dependent", [[error("must not execute")]], {"broken"}))
assert(not TWOMLoader.loaded.dependent)
--失败和跳过是不同状态管理界面不能只展示静态启用值
assert(MaxyModLoader.mod_by_id.broken.status == "failed")
assert(MaxyModLoader.mod_by_id.dependent.status == "skipped")
assert(MaxyModLoader.mod_by_id.events.status == "loaded")
assert(not TWOMLoader.load_mod("syntax", "this is not lua", {}))
assert(not TWOMLoader.load_mod("protocol", "return 123", {}))
assert(not TWOMLoader.load_mod("invalid.subscription", [[return {on_load = function(c)
    c.events.on("event", nil)
end}]], {}))

--测试事件回调内新增订阅不会进入当前广播快照
dynamic = 0
assert(TWOMLoader.load_mod("dynamic", [[return {on_load = function(c)
    c.events.on("dynamic", function()
        dynamic = dynamic + 1
        c.events.on("dynamic", function() dynamic = dynamic + 10 end)
    end)
end}]], {}))
TWOMLoader.emit("dynamic")
assert(dynamic == 1)
TWOMLoader.emit("dynamic")
assert(dynamic == 12)

--验证内部模块仅执行一次并将配置注入模组上下文
module_runs = 0
assert(TWOMLoader.load_mod("modular", [[return {on_load = function(c)
    local a = c.require("helper")
    local b = c.require("helper")
    if a ~= b or a.value ~= 7 then error("module cache/config mismatch") end
    c.services.provide("calculator", a)
end}]], {}, {config = {value = 7}, modules = {
    helper = "local c = ...; module_runs = module_runs + 1; return {value = c.config.value}"
}}))
assert(module_runs == 1)
assert(TWOMLoader.load_mod("consumer", [[return {on_load = function(c)
    if c.services.get("modular", "calculator").value ~= 7 then error("missing service") end
end}]], {"modular"}))
assert(not TWOMLoader.load_mod("undeclared", [[return {on_load = function(c)
    c.services.get("modular", "calculator")
end}]], {}))

--验证循环内部模块和失败服务提供者的隔离
assert(not TWOMLoader.load_mod("module.cycle", [[return {on_load = function(c) c.require("a") end}]], {}, {modules = {
    a = "local c = ...; return c.require('b')", b = "local c = ...; return c.require('a')"
}}))
assert(not TWOMLoader.load_mod("service.failure", [[return {on_load = function(c)
    c.services.provide("bad", {}); error("expected service failure")
end}]], {}))
assert(not TWOMLoader.load_mod("service.dependent", [[return {on_load = function(c)
    error("must not execute")
end}]], {"service.failure"}))

--验证模组可声明受类型范围约束的规则并由显式依赖调整
rule_notifications = 0
assert(TWOMLoader.load_mod("rules.provider", [[return {on_load = function(c)
    c.rules.define("fatigue_rate", {type = "number", default = 0.5, minimum = 0, maximum = 1,
        description = "每日疲劳增长倍率"})
    c.rules.define("supply_mode", {type = "string", default = "balanced", values = {"scarce", "balanced", "abundant"}})
    c.rules.define("enabled", {type = "boolean", default = true})
    if c.rules.get("fatigue_rate") ~= 0.5 then error("rule default mismatch") end
end}]], {}))
assert(TWOMLoader.load_mod("rules.tuner", [[return {on_load = function(c)
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
assert(not TWOMLoader.load_mod("rules.unauthorized", [[return {on_load = function(c)
    c.rules.set("rules.provider", "fatigue_rate", 0.2)
end}]], {}))
assert(not TWOMLoader.load_mod("rules.failed_writer", [[return {on_load = function(c)
    c.rules.set("rules.provider", "fatigue_rate", 0.2)
    error("expected writer rollback")
end}]], {"rules.provider"}))
assert(TWOMLoader.loaded["rules.provider"] and TWOMLoader.loaded["rules.tuner"])
assert(rule_notifications == 2)
local rule_rows = MaxyModLoader.rule_snapshot()
assert(#rule_rows == 3 and rule_rows[1].id == "rules.provider:enabled")
assert(rule_rows[2].value == 0.75 and rule_rows[3].value == "abundant")
assert(not TWOMLoader.load_mod("rules.rollback", [[return {on_load = function(c)
    c.rules.define("temporary", {type = "boolean", default = true})
    error("expected rule rollback")
end}]], {}))
for _, rule in ipairs(MaxyModLoader.rule_snapshot()) do assert(rule.id ~= "rules.rollback:temporary") end
''')
assert any("expected entry failure" in message for message in messages)
assert any("expected callback failure" in message for message in messages)
assert any("invalid event subscription" in message for message in messages)
print("通过：Lua5.1入口隔离、依赖失败、事件参数、订阅快照和函数包装")

#额外验证由C#工具生成的完整入口源码实际可以执行
bundle = pathlib.Path(sys.argv[1] if len(sys.argv) > 1 else "artifacts/tests/bundle.lua")
if bundle.exists():
    compiled = LuaRuntime()
    compiled.globals().print = messages.append
    compiled.execute("gLua = {ResetReplication = function() end, ExecuteFile = function() end}")
    compiled.execute(bundle.read_text(encoding="utf-8"))
    assert compiled.globals().TWOMLoader.loaded["twom.hello"]
    print("通过：C#生成入口在Lua5.1中加载示例模组")
else:
    raise FileNotFoundError("请先运行C#测试生成完整Lua入口")
