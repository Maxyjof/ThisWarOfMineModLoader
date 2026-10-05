--在游戏既有Lua虚拟机内运行并兼容Lua5.1与Lua5.2
local compile = loadstring or load
local loaded = {}
local listeners = {}
local services = {}
local rules = {}
local actions = {}
local api = { name = "MaxyModLoader", version = "0.4.0", api_version = "1.0.0", loaded = loaded, mods = {}, mod_by_id = {} }
api.actions = {}
MaxyModLoader = api
--保留旧API名称使已经发布的模组继续兼容
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
    local line = "[MaxyModLoader][" .. id .. "] " .. tostring(message)
    if print then pcall(print, line) end
    if io and io.open then
        pcall(function()
            local file = io.open("MaxyModLoader/runtime.log", "a")
            if file then file:write(line .. "\n"); file:close() end
        end)
    end
end
api.log = log

--<summary>
--注册管理界面使用的完整模组介绍和初始状态
--</summary>
function api.register_mod(metadata)
    --禁用模组保留介绍但不会编译或执行其入口
    require_condition(type(metadata) == "table" and type(metadata.id) == "string", "invalid mod metadata")
    require_condition(api.mod_by_id[metadata.id] == nil, "duplicate mod metadata")
    metadata.status = metadata.enabled == false and "disabled" or "pending"
    metadata.error = ""
    api.mod_by_id[metadata.id] = metadata
    table.insert(api.mods, metadata)
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
local function context_for(id, dependencies, options)
    --记录模组拥有的注册项以便入口失败后撤销
    local context = { id = id, api_version = api.api_version, loader_version = api.version }
    context.config = options.config or {}
    local module_cache = {}
    local module_loading = {}
    local module_sources = options.modules or {}
    local allowed_services = { [id] = true }
    for _, dependency in ipairs(dependencies) do allowed_services[dependency] = true end
    services[id] = {}
    local owned_listeners = {}
    local owned_wrappers = {}
    local owned_rules = {}
    local owned_rule_changes = {}
    local owned_actions = {}
    local allowed_capabilities = {}
    for _, capability in ipairs(options.capabilities or {}) do allowed_capabilities[capability] = true end
    context.log = function(message) log(id, message) end

    --<summary>
    --加载并缓存当前模组显式声明的内部模块
    --</summary>
    context.require = function(name)
        --检测模块循环并区分尚未加载和已缓存的返回值
        if module_cache[name] ~= nil then return module_cache[name] end
        require_condition(not module_loading[name], "cyclic module: " .. tostring(name))
        require_condition(type(module_sources[name]) == "string", "unknown module: " .. tostring(name))
        local chunk, err = compile(module_sources[name], "@twom/mods/" .. id .. "/" .. name)
        require_condition(chunk, err)
        module_loading[name] = true
        local ok, value = pcall(chunk, context)
        module_loading[name] = nil
        require_condition(ok, value)
        if value == nil then value = true end
        module_cache[name] = value
        return value
    end

    context.services = {}
    --<summary>
    --为已声明依赖的其他模组提供命名服务
    --</summary>
    context.services.provide = function(name, service)
        --同一模组不能重复提供同名服务失败入口的服务会清理
        require_condition(type(name) == "string" and name ~= "" and service ~= nil, "invalid service")
        require_condition(services[id][name] == nil, "duplicate service: " .. name)
        services[id][name] = service
    end

    --<summary>
    --只获取自身或清单已声明依赖模组的服务
    --</summary>
    context.services.get = function(provider, name)
        --依赖入口必须成功执行才能向其他模组暴露服务
        require_condition(allowed_services[provider], "undeclared service dependency: " .. tostring(provider))
        require_condition(provider == id or loaded[provider], "service provider not loaded")
        local service = services[provider] and services[provider][name]
        require_condition(service ~= nil, "missing service: " .. tostring(name))
        return service
    end
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
    context.rules = {}
    --<summary>
    --校验规则值与声明的类型范围和枚举约束一致
    --</summary>
    local function validate_rule_value(definition, value)
        --拒绝Lua中可传播的NaN与无穷数避免规则结果不稳定
        if definition.type == "number" then
            require_condition(type(value) == "number" and value == value and value ~= math.huge and value ~= -math.huge, "rule requires a finite number")
            require_condition(definition.minimum == nil or value >= definition.minimum, "rule value is below minimum")
            require_condition(definition.maximum == nil or value <= definition.maximum, "rule value is above maximum")
        elseif definition.type == "boolean" then
            require_condition(type(value) == "boolean", "rule requires a boolean")
        elseif definition.type == "string" then
            require_condition(type(value) == "string", "rule requires a string")
            require_condition(#value <= (definition.max_length or 4096), "rule string exceeds maximum length")
            if definition.values then
                local found = false
                for _, choice in ipairs(definition.values) do if choice == value then found = true; break end end
                require_condition(found, "rule value is not an allowed choice")
            end
        else
            error("unsupported rule type", 2)
        end
        return value
    end
    --<summary>
    --将规则名称限定在提供方命名空间并验证本地声明
    --</summary>
    local function resolve_rule(provider, name)
        require_condition(allowed_services[provider], "undeclared rule dependency: " .. tostring(provider))
        require_condition(provider == id or loaded[provider], "rule provider not loaded")
        require_condition(type(name) == "string" and name:match("^[a-z][a-z0-9_.-]*$"), "invalid rule name")
        local key = provider .. ":" .. name
        local rule = rules[key]
        require_condition(rule ~= nil, "unknown rule: " .. key)
        return key, rule
    end
    context.rules.define = function(name, definition)
        --只接收有限且有默认值的简单规则不执行模组传入的表达式
        require_condition(type(name) == "string" and #name <= 64 and name:match("^[a-z][a-z0-9_.-]*$") and type(definition) == "table", "invalid rule definition")
        require_condition(definition.type == "number" or definition.type == "boolean" or definition.type == "string", "unsupported rule type")
        require_condition(definition.description == nil or type(definition.description) == "string" and #definition.description <= 256, "invalid rule description")
        if definition.type == "number" then
            require_condition(definition.minimum == nil or type(definition.minimum) == "number" and definition.minimum == definition.minimum and math.abs(definition.minimum) < math.huge, "invalid rule minimum")
            require_condition(definition.maximum == nil or type(definition.maximum) == "number" and definition.maximum == definition.maximum and math.abs(definition.maximum) < math.huge, "invalid rule maximum")
            require_condition(definition.minimum == nil or definition.maximum == nil or definition.minimum <= definition.maximum, "rule minimum exceeds maximum")
        elseif definition.minimum ~= nil or definition.maximum ~= nil then
            error("only number rules may declare bounds", 2)
        end
        if definition.type == "string" and definition.values ~= nil then
            require_condition(type(definition.values) == "table" and #definition.values > 0 and #definition.values <= 128, "string rule choices must contain one to 128 values")
            local seen = {}
            for _, choice in ipairs(definition.values) do
                require_condition(type(choice) == "string" and #choice <= 4096, "string rule choices must be short strings")
                require_condition(not seen[choice], "duplicate string rule choice")
                seen[choice] = true
            end
        elseif definition.values ~= nil then
            error("only string rules may declare choices", 2)
        end
        if definition.max_length ~= nil then
            require_condition(definition.type == "string" and type(definition.max_length) == "number" and definition.max_length >= 1 and definition.max_length <= 4096 and definition.max_length % 1 == 0, "invalid string rule maximum length")
        end
        local key = id .. ":" .. name
        require_condition(rules[key] == nil, "duplicate rule: " .. key)
        local rule = {owner = id, name = name, type = definition.type, default = definition.default,
            minimum = definition.minimum, maximum = definition.maximum, values = definition.values,
            description = tostring(definition.description or ""), watchers = {}}
        rule.value = validate_rule_value(rule, definition.default)
        rules[key] = rule
        table.insert(owned_rules, key)
        return key
    end
    context.rules.get = function(provider, name)
        --读取规则要求自有模组或清单中直接声明的依赖
        if name == nil then name, provider = provider, id end
        local _, rule = resolve_rule(provider or id, name)
        return rule.value
    end
    context.rules.set = function(provider, name, value)
        --消费者只能修改已声明依赖提供的规则
        if value == nil then value, name, provider = name, provider, id end
        local key, rule = resolve_rule(provider or id, name)
        validate_rule_value(rule, value)
        if rule.value == value then return false end
        local previous = rule.value
        if owned_rule_changes[key] == nil then owned_rule_changes[key] = previous end
        rule.value = value
        for _, subscription in ipairs(rule.watchers) do
            if subscription.active then guarded(subscription.id, function() subscription.callback(value, previous, key) end) end
        end
        return true
    end
    context.rules.on_change = function(provider, name, callback)
        --变更监听同样受依赖边界限制并纳入入口失败清理
        if callback == nil then callback, name, provider = name, provider, id end
        local key, rule = resolve_rule(provider or id, name)
        require_condition(type(callback) == "function", "rule listener must be a function")
        local subscription = {id = id, callback = callback, active = true}
        table.insert(rule.watchers, subscription)
        table.insert(owned_listeners, subscription)
        return function() subscription.active = false end
    end
    context.actions = {}
    --<summary>
    --注册需要显式清单能力授权的结构化MCP工具
    --</summary>
    context.actions.register = function(name, definition, callback)
        --工具只接受有限的扁平标量参数避免任意Lua或复杂对象穿过跨进程协议
        require_condition(allowed_capabilities["mcp.tools"], "manifest capability mcp.tools is required")
        require_condition(type(name) == "string" and #name <= 48 and name:match("^[a-z][a-z0-9_]*$"), "invalid action name")
        require_condition(type(definition) == "table" and type(definition.description) == "string" and
            #definition.description > 0 and #definition.description <= 512, "invalid action description")
        require_condition(type(definition.properties) == "table" and type(definition.required or {}) == "table", "invalid action schema")
        require_condition(type(callback) == "function", "action callback must be a function")
        local property_count = 0
        for key, property in pairs(definition.properties) do
            property_count = property_count + 1
            require_condition(property_count <= 32 and type(key) == "string" and #key <= 64 and
                key:match("^[A-Za-z][A-Za-z0-9_]*$") and type(property) == "table", "invalid action property")
            require_condition(property.type == "string" or property.type == "number" or
                property.type == "integer" or property.type == "boolean", "unsupported action property type")
            require_condition(property.description == nil or type(property.description) == "string" and #property.description <= 256,
                "invalid action property description")
        end
        local required = {}
        for _, key in ipairs(definition.required or {}) do
            require_condition(type(key) == "string" and definition.properties[key] ~= nil and not required[key], "invalid required action property")
            required[key] = true
        end
        require_condition(definition.read_only == nil or type(definition.read_only) == "boolean", "invalid action read-only hint")
        require_condition(definition.destructive == nil or type(definition.destructive) == "boolean", "invalid action destructive hint")
        local action_id = id .. ":" .. name
        require_condition(actions[action_id] == nil, "duplicate action: " .. action_id)
        local action = {id = action_id, owner = id, name = name, description = definition.description,
            input_schema = {type = "object", properties = definition.properties, required = definition.required or {}, additionalProperties = false},
            callback = callback, read_only = definition.read_only == true, destructive = definition.destructive == true, active = true}
        actions[action_id] = action
        table.insert(owned_actions, action)
        return function() action.active = false end
    end
    local rollback = function()
        --保留其他模组包装和订阅只禁用当前入口新增的注册项
        for _, item in ipairs(owned_listeners) do item.active = false end
        for _, item in ipairs(owned_wrappers) do item.active = false end
        for _, action in ipairs(owned_actions) do action.active = false end
        --失败入口撤销其对其他模组规则的改值并通知仍有效的监听者
        for key, previous in pairs(owned_rule_changes) do
            local rule = rules[key]
            if rule then
                local current = rule.value
                rule.value = previous
                for _, subscription in ipairs(rule.watchers) do
                    if subscription.active then guarded(subscription.id, function() subscription.callback(previous, current, key) end) end
                end
            end
        end
        for _, key in ipairs(owned_rules) do rules[key] = nil end
        services[id] = nil
    end
    return context, rollback
end

--<summary>
--返回规则定义和值的稳定快照供调试工具读取
--</summary>
function api.rule_snapshot()
    --按完整规则键排序避免依赖Lua表遍历顺序
    local result = {}
    for key, rule in pairs(rules) do
        table.insert(result, {id = key, owner = rule.owner, name = rule.name, type = rule.type,
            value = rule.value, default = rule.default, minimum = rule.minimum, maximum = rule.maximum,
            values = rule.values, description = rule.description})
    end
    table.sort(result, function(left, right) return left.id < right.id end)
    return result
end

--<summary>
--返回已成功加载模组注册的结构化MCP工具定义
--</summary>
function api.actions.list()
    --未成功加载的模组或已撤销动作不会暴露给MCP客户端
    local result = {}
    for _, action in pairs(actions) do
        if action.active and loaded[action.owner] then
            table.insert(result, {id = action.id, owner = action.owner, name = action.name,
                description = action.description, inputSchema = action.input_schema,
                readOnly = action.read_only, destructive = action.destructive})
        end
    end
    table.sort(result, function(left, right) return left.id < right.id end)
    return result
end

--<summary>
--校验结构化动作参数后在游戏主线程调用模组回调
--</summary>
function api.actions.call(action_id, arguments)
    --动作名只解析注册表中的精确ID不会编译或执行客户端文本
    local action = actions[action_id]
    require_condition(action and action.active and loaded[action.owner], "mod action is unavailable")
    require_condition(type(arguments) == "table", "mod action arguments must be an object")
    for key, value in pairs(arguments) do
        local property = action.input_schema.properties[key]
        require_condition(property ~= nil, "unknown mod action argument: " .. tostring(key))
        local kind = type(value)
        if property.type == "integer" then
            require_condition(kind == "number" and value == value and math.abs(value) < math.huge and value % 1 == 0, "mod action argument must be an integer")
        elseif property.type == "number" then
            require_condition(kind == "number" and value == value and math.abs(value) < math.huge, "mod action argument must be a finite number")
        else
            require_condition(kind == property.type, "mod action argument has the wrong type")
        end
        if kind == "string" then require_condition(#value <= 4096, "mod action string argument is too long") end
    end
    for _, key in ipairs(action.input_schema.required) do require_condition(arguments[key] ~= nil, "missing mod action argument: " .. key) end
    return action.callback(arguments)
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
function api.load_mod(id, source, dependencies, options)
    --直接调用旧加载API的模组也有可查询状态
    if not api.mod_by_id[id] then api.register_mod({id = id, name = id, enabled = true}) end
    local metadata = api.mod_by_id[id]
    --静态依赖检查通过后仍需确认依赖入口实际成功执行
    for _, dependency in ipairs(dependencies) do
        if not loaded[dependency] then
            metadata.status = "skipped"
            metadata.error = "依赖未加载：" .. dependency
            log(id, "skipped: dependency failed: " .. dependency)
            return false
        end
    end
    --编译入口并要求返回带on_load方法的模组表
    local context, rollback = context_for(id, dependencies, options or {})
    metadata.status = "loading"
    local ok, failure = guarded(id, function()
        local chunk, err = compile(source, "@twom/mods/" .. id)
        require_condition(chunk, err)
        local mod = chunk()
        require_condition(type(mod) == "table" and type(mod.on_load) == "function", "entry must return { on_load = function(context) ... end }")
        mod.on_load(context)
    end)
    --失败时清理框架拥有的注册项不声称可以回滚任意全局副作用
    if ok then
        loaded[id] = true
        metadata.status = "loaded"
        log(id, "loaded")
    else
        metadata.status = "failed"
        metadata.error = tostring(failure)
        rollback()
    end
    return ok
end
