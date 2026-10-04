--在游戏既有Lua虚拟机内运行并兼容Lua5.1与Lua5.2
local compile = loadstring or load
local loaded = {}
local listeners = {}
local api = { version = "0.1.0", loaded = loaded }
TWOMLoader = api

--<summary>
--执行加载器自身校验避免游戏重定义全局assert后失去校验能力
--</summary>
local function require_condition(condition, message)
    --游戏System脚本会覆盖assert因此必须直接使用错误抛出机制
    if not condition then error(message, 2) end
    return condition
end

--<summary>
--向控制台和可用的日志文件写入带模组ID的消息
--</summary>
local function log(id, message)
    --日志设施异常不得中断其他模组
    local line = "[TWOMLoader][" .. id .. "] " .. tostring(message)
    if print then pcall(print, line) end
    if io and io.open then
        pcall(function()
            local file = io.open("TWOMLoader/runtime.log", "a")
            if file then file:write(line .. "\n"); file:close() end
        end)
    end
end

--<summary>
--尽可能附加Lua调用栈以定位模组错误
--</summary>
local function traceback(message)
    --游戏未开放debug库时保留原错误信息
    if debug and debug.traceback then return debug.traceback(tostring(message), 2) end
    return tostring(message)
end

--<summary>
--隔离模组入口或事件回调的异常并记录诊断
--</summary>
local function guarded(id, callback)
    --使用无参数闭包兼容Lua5.1的xpcall约定
    local ok, result = xpcall(callback, traceback)
    if not ok then log(id, result) end
    return ok, result
end

--<summary>
--创建模组上下文及失败时的订阅和函数包装清理操作
--</summary>
local function context_for(id)
    --记录模组拥有的注册项以便入口失败后撤销
    local context = { id = id, api_version = api.version }
    local owned_listeners = {}
    local owned_wrappers = {}
    context.log = function(message) log(id, message) end
    context.events = {}
    context.events.on = function(name, callback)
        --返回取消函数并用独立标记控制订阅是否生效
        require_condition(type(name) == "string" and type(callback) == "function", "invalid event subscription")
        local subscription = { id = id, callback = callback, active = true }
        listeners[name] = listeners[name] or {}
        table.insert(listeners[name], subscription)
        table.insert(owned_listeners, subscription)
        return function() subscription.active = false end
    end
    context.wrap = function(target, key, callback)
        require_condition(type(target) == "table" and type(target[key]) == "function", "wrap requires a Lua table function")
        require_condition(type(callback) == "function", "wrapper must be a function")
        --包装链逐层组合失败模组的包装退化为原函数透传
        local previous = target[key]
        local item = { active = true }
        local wrapped = function(...)
            if item.active then return callback(previous, ...) end
            return previous(...)
        end
        target[key] = wrapped
        table.insert(owned_wrappers, item)
        return function() item.active = false end
    end
    local rollback = function()
        --保留其他模组包装和订阅只禁用当前入口新增的注册项
        for _, item in ipairs(owned_listeners) do item.active = false end
        for _, item in ipairs(owned_wrappers) do item.active = false end
    end
    return context, rollback
end

--<summary>
--按订阅顺序广播事件并隔离每个回调异常
--</summary>
function api.emit(name, ...)
    --显式保存参数数量使末尾nil也可以正确传递
    local args = { n = select("#", ...), ... }
    local unpack_args = unpack or table.unpack
    --复制订阅快照避免回调中注册新订阅造成无限遍历
    local snapshot = {}
    for _, item in ipairs(listeners[name] or {}) do table.insert(snapshot, item) end
    for _, item in ipairs(snapshot) do
        if item.active then
            guarded(item.id, function() item.callback(unpack_args(args, 1, args.n)) end)
        end
    end
end

--<summary>
--加载单个模组并跳过运行时加载失败的依赖
--</summary>
function api.load_mod(id, source, dependencies)
    --静态依赖检查通过后仍需确认依赖入口实际成功执行
    for _, dependency in ipairs(dependencies) do
        if not loaded[dependency] then log(id, "skipped: dependency failed: " .. dependency); return false end
    end
    --编译入口并要求返回带on_load方法的模组表
    local context, rollback = context_for(id)
    local ok = guarded(id, function()
        local chunk, err = compile(source, "@twom/mods/" .. id)
        require_condition(chunk, err)
        local mod = chunk()
        require_condition(type(mod) == "table" and type(mod.on_load) == "function", "entry must return { on_load = function(context) ... end }")
        mod.on_load(context)
    end)
    --失败时清理框架拥有的注册项不声称可以回滚任意全局副作用
    if ok then loaded[id] = true; log(id, "loaded") else rollback() end
    return ok
end
