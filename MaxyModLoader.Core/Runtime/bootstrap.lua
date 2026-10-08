--在已核验的游戏Lua5.1虚拟机内运行
local compile = loadstring
local loaded = {}
local listeners = {}
local services = {}
local rules = {}
local actions = {}
local api = { name = "MaxyModLoader", version = "@MML_VERSION@", api_version = "1.2.0", loaded = loaded, mods = {}, mod_by_id = {} }
api.actions = {}
MaxyModLoader = api

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
    require_condition(type(metadata) == "table" and type(metadata.id) == "string" and
        type(metadata.description_document) == "table", "invalid mod metadata")
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
    --Lua5.1的xpcall只接受无参数闭包
    local ok, result = xpcall(callback, traceback)
    if not ok then log(id, result) end
    return ok, result
end

local storage_header = "MMLSTORE1"
local storage_max_bytes = 1048576
local storage_max_entries = 256
local storage_max_value_bytes = 4096

--<summary>
--校验模组持久数据的键名格式与长度
--</summary>
local function validate_storage_key(key)
    --限制键名字符避免产生歧义并确保数据只属于当前模组
    require_condition(type(key) == "string" and #key <= 64 and key:match("^[a-z][a-z0-9_.-]*$"), "invalid storage key")
    return key
end

--<summary>
--把字符串字节编码为仅含小写十六进制的文本
--</summary>
local function storage_to_hex(value)
    --逐字节编码使换行和任意UTF8内容都能安全存入记录
    local result = {}
    for index = 1, #value do result[index] = string.format("%02x", value:byte(index)) end
    return table.concat(result)
end

--<summary>
--校验并解码持久数据中的十六进制文本
--</summary>
local function storage_from_hex(value)
    --拒绝奇数长度或非十六进制内容避免静默丢失数据
    require_condition(#value % 2 == 0 and value:match("^[0-9a-f]*$"), "invalid storage encoding")
    local result = {}
    for index = 1, #value, 2 do result[#result + 1] = string.char(tonumber(value:sub(index, index + 1), 16)) end
    return table.concat(result)
end

--<summary>
--校验单个模组数据文件并读取其最新快照
--</summary>
local function read_storage_slot(id, slot)
    --文件名由已校验的模组ID和固定槽位构成不接受模组提供的路径
    local path = "MaxyModLoader/storage/" .. id .. "." .. tostring(slot) .. ".dat"
    if not io or type(io.open) ~= "function" then return {exists = true, error = "Lua文件读取接口不可用"} end
    local file, open_error = io.open(path, "rb")
    if not file then return {exists = false, error = open_error} end

    --隔离文件读取与格式解析错误使另一份有效快照仍可用于恢复
    local ok, result = pcall(function()
        local contents, read_error = file:read("*a")
        local close_ok, close_error = file:close()
        file = nil
        require_condition(contents ~= nil, read_error or "storage read failed")
        require_condition(close_ok ~= nil, close_error or "storage close failed")
        require_condition(#contents <= storage_max_bytes, "storage file exceeds maximum size")

        --只接受当前格式标记不尝试解析旧格式或未知版本
        local generation_text, body = contents:match("^MMLSTORE1\t(%d+)\n(.*)$")
        require_condition(generation_text ~= nil, "storage format is not MMLSTORE1")
        local generation = tonumber(generation_text)
        require_condition(generation ~= nil and generation == generation and generation >= 0 and
            generation % 1 == 0 and generation < math.huge, "invalid storage generation")

        --只解析完整行并忽略崩溃时可能留下的末尾半行
        local last_newline = body:match(".*()\n") or 0
        local complete_body = body:sub(1, last_newline)
        local values = {}
        local count = 0
        for line in complete_body:gmatch("(.-)\n") do
            local operation, key_hex, value_type, value_hex = line:match("^([SD])\t([0-9a-f]+)\t?([snb]?)\t?([0-9a-f]*)$")
            require_condition(operation ~= nil, "invalid storage record")
            local key = validate_storage_key(storage_from_hex(key_hex))
            if operation == "D" then
                require_condition(value_type == "" and value_hex == "", "invalid storage delete record")
                if values[key] ~= nil then values[key] = nil; count = count - 1 end
            else
                local encoded = storage_from_hex(value_hex)
                local value
                if value_type == "s" then
                    require_condition(#encoded <= storage_max_value_bytes, "stored string exceeds maximum size")
                    value = encoded
                elseif value_type == "n" then
                    value = tonumber(encoded)
                    require_condition(value ~= nil and value == value and math.abs(value) < math.huge, "invalid stored number")
                else
                    require_condition(value_type == "b" and (encoded == "0" or encoded == "1"), "invalid stored boolean")
                    value = encoded == "1"
                end
                if values[key] == nil then count = count + 1 end
                require_condition(count <= storage_max_entries, "storage has too many entries")
                values[key] = value
            end
        end
        return {exists = true, valid = true, path = path, generation = generation, slot = slot,
            values = values, truncated = #body > last_newline}
    end)

    --关闭读取期间发生解析错误时遗留的文件句柄
    if file then pcall(function() file:close() end) end
    if not ok then return {exists = true, path = path, error = tostring(result)} end
    return result
end

--<summary>
--创建当前模组专属的双槽持久键值存储
--</summary>
local function create_storage(id)
    --读取两份快照并选择代数较新的有效文件
    local slots = {read_storage_slot(id, 0), read_storage_slot(id, 1)}
    local selected
    for _, slot in ipairs(slots) do
        if slot.valid then
            if not selected or slot.generation > selected.generation then selected = slot end
        end
    end

    --一份快照损坏时保留另一份有效状态并把恢复情况写入日志
    if selected then
        for _, slot in ipairs(slots) do
            if slot.exists and not slot.valid then log(id, "持久数据槽损坏，已从另一份有效快照恢复：" .. tostring(slot.error)) end
        end
    else
        --已有文件却没有有效快照时明确失败不把损坏数据伪装成空状态
        for _, slot in ipairs(slots) do
            require_condition(not slot.exists, "模组持久数据损坏：" .. tostring(slot.error))
        end
    end
    --两个快照代数必须不同否则无法确定最近一次已提交的数据
    if slots[1].valid and slots[2].valid then
        require_condition(slots[1].generation ~= slots[2].generation, "模组持久数据代数重复")
    end
    if selected and selected.truncated then log(id, "持久数据末尾记录不完整，已使用最后一条完整记录") end

    local values = selected and selected.values or {}
    local active_slot = selected and selected.slot or nil
    local generation = selected and selected.generation or 0
    local storage = {}

    --<summary>
    --把完整新快照写入非活动槽并在成功后切换状态
    --</summary>
    local function persist(next_values)
        --逐键排序使快照稳定并便于诊断同一模组的数据文件
        local keys = {}
        for key in pairs(next_values) do keys[#keys + 1] = key end
        table.sort(keys)
        local next_generation = generation + 1
        require_condition(next_generation < math.huge and next_generation % 1 == 0, "storage generation exhausted")
        local lines = {storage_header .. "\t" .. string.format("%.0f", next_generation) .. "\n"}
        for _, key in ipairs(keys) do
            local value = next_values[key]
            local value_type, encoded
            if type(value) == "string" then
                value_type, encoded = "s", value
            elseif type(value) == "number" then
                value_type, encoded = "n", string.format("%.17g", value)
            else
                value_type, encoded = "b", value and "1" or "0"
            end
            lines[#lines + 1] = "S\t" .. storage_to_hex(key) .. "\t" .. value_type .. "\t" .. storage_to_hex(encoded) .. "\n"
        end
        local contents = table.concat(lines)
        require_condition(#contents <= storage_max_bytes, "storage snapshot exceeds maximum size")

        --完整写入临时文件并刷新后才替换较旧的数据槽
        local target_slot = active_slot == 0 and 1 or 0
        local target = "MaxyModLoader/storage/" .. id .. "." .. tostring(target_slot) .. ".dat"
        local temporary = target .. ".tmp"
        require_condition(io and type(io.open) == "function" and os and type(os.rename) == "function" and
            type(os.remove) == "function", "Lua文件原子保存接口不可用")
        local ok, failure = pcall(function()
            local file, open_error = io.open(temporary, "wb")
            require_condition(file ~= nil, open_error or "storage temporary file could not be opened")
            local write_ok, write_error = file:write(contents)
            if not write_ok then file:close(); error(write_error or "storage write failed", 0) end
            local flush_ok, flush_error = file:flush()
            if not flush_ok then file:close(); error(flush_error or "storage flush failed", 0) end
            local close_ok, close_error = file:close()
            require_condition(close_ok ~= nil, close_error or "storage close failed")

            --旧活动槽保持不动直到临时快照全部写完并完成替换
            if slots[target_slot + 1].exists then
                local remove_ok, remove_error = os.remove(target)
                require_condition(remove_ok ~= nil, remove_error or "old storage slot could not be removed")
                slots[target_slot + 1].exists = false
            end
            local rename_ok, rename_error = os.rename(temporary, target)
            require_condition(rename_ok ~= nil, rename_error or "new storage slot could not be installed")
        end)
        if not ok then
            pcall(function() os.remove(temporary) end)
            error(failure, 2)
        end

        --新槽替换成功后才提交内存状态并标记下一次写入目标
        slots[target_slot + 1] = {exists = true, valid = true, slot = target_slot, generation = next_generation}
        active_slot, generation = target_slot, next_generation
    end

    --<summary>
    --读取指定键并在缺少数据时返回经过校验的默认值
    --</summary>
    function storage.get(key, default)
        --默认值与已保存值使用相同的标量类型约束
        validate_storage_key(key)
        if default ~= nil then
            require_condition(type(default) == "string" or type(default) == "number" and default == default and
                math.abs(default) < math.huge or type(default) == "boolean", "storage default must be a finite scalar")
            require_condition(type(default) ~= "string" or #default <= storage_max_value_bytes, "storage default string exceeds maximum size")
        end
        local value = values[key]
        if value == nil then return default end
        return value
    end

    --<summary>
    --保存经过大小限制的字符串数值或布尔值
    --</summary>
    function storage.set(key, value)
        --只允许有限标量并跳过没有变化的重复写入
        validate_storage_key(key)
        local value_type = type(value)
        require_condition(value_type == "string" or value_type == "number" or value_type == "boolean", "storage value must be a string, finite number or boolean")
        require_condition(value_type ~= "number" or value == value and math.abs(value) < math.huge, "storage number must be finite")
        require_condition(value_type ~= "string" or #value <= storage_max_value_bytes, "storage string exceeds maximum size")
        if values[key] == value then return false end
        if values[key] == nil then
            local count = 0
            for _ in pairs(values) do count = count + 1 end
            require_condition(count < storage_max_entries, "storage entry limit reached")
        end
        local next_values = {}
        for existing_key, existing_value in pairs(values) do next_values[existing_key] = existing_value end
        next_values[key] = value
        persist(next_values)
        values = next_values
        return true
    end

    --<summary>
    --删除已保存键并在磁盘快照成功后更新内存
    --</summary>
    function storage.delete(key)
        --不存在的键保持幂等且不产生额外文件写入
        validate_storage_key(key)
        if values[key] == nil then return false end
        local next_values = {}
        for existing_key, existing_value in pairs(values) do
            if existing_key ~= key then next_values[existing_key] = existing_value end
        end
        persist(next_values)
        values = next_values
        return true
    end

    --<summary>
    --返回当前模组持久数据的浅复制表
    --</summary>
    function storage.all()
        --复制键值避免调用者绕过校验直接修改内部数据
        local result = {}
        for key, value in pairs(values) do result[key] = value end
        return result
    end

    return storage
end

--<summary>
--创建模组上下文及失败时的订阅和函数包装清理操作
--</summary>
local function context_for(id, dependencies, options)
    --记录模组拥有的注册项以便入口失败后撤销
    local context = { id = id, api_version = api.api_version, loader_version = api.version }
    context.config = options.config
    local module_cache = {}
    local module_loading = {}
    local module_sources = options.modules
    local allowed_services = { [id] = true }
    for _, dependency in ipairs(dependencies) do allowed_services[dependency] = true end
    services[id] = {}
    local owned_listeners = {}
    local owned_wrappers = {}
    local owned_rules = {}
    local owned_rule_changes = {}
    local owned_actions = {}
    local allowed_capabilities = {}
    for _, capability in ipairs(options.capabilities) do allowed_capabilities[capability] = true end
    context.log = function(message) log(id, message) end

    --<summary>
    --惰性创建当前模组专属的数据存储
    --</summary>
    local mod_storage
    local function get_storage()
        --没有使用数据接口的模组不会产生磁盘访问
        if not mod_storage then mod_storage = create_storage(id) end
        return mod_storage
    end
    context.storage = {}
    --<summary>
    --从模组持久数据读取键值或默认值
    --</summary>
    context.storage.get = function(key, default) return get_storage().get(key, default) end
    --<summary>
    --保存模组持久数据并返回是否发生变化
    --</summary>
    context.storage.set = function(key, value) return get_storage().set(key, value) end
    --<summary>
    --删除模组持久数据并返回是否发生变化
    --</summary>
    context.storage.delete = function(key) return get_storage().delete(key) end
    --<summary>
    --返回模组持久数据的浅复制
    --</summary>
    context.storage.all = function() return get_storage().all() end

    --<summary>
    --加载并缓存当前模组显式声明的内部模块
    --</summary>
    context.require = function(name)
        --检测模块循环并区分尚未加载和已缓存的返回值
        if module_cache[name] ~= nil then return module_cache[name] end
        require_condition(not module_loading[name], "cyclic module: " .. tostring(name))
        require_condition(type(module_sources[name]) == "string", "unknown module: " .. tostring(name))
        local chunk, err = compile(module_sources[name], "@MaxyModLoader/mods/" .. id .. "/" .. name)
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
    context.game = {phase = {}, scene = {}, campaign = {}, characters = {}, items = {}, inventory = {}, story = {}}
    --<summary>
    --取得当前活动场景并在游戏尚未创建场景时明确失败
    --</summary>
    local function get_game_scene()
        --不创建测试对象也不回退到旧全局名称
        require_condition(gScene ~= nil and type(gScene.GetDwellerCount) == "function", "game scene is unavailable")
        return gScene
    end
    --<summary>
    --校验原生物品标识并取得已注册物品配置
    --</summary>
    local function get_game_item(name)
        --先限制键格式再访问原生注册表避免任意索引
        require_condition(type(name) == "string" and #name <= 96 and name:match("^[A-Za-z][A-Za-z0-9_]*$"), "invalid item name")
        require_condition(gKosovoItemConfig ~= nil and type(gKosovoItemConfig.GetEntryWithName) == "function", "item registry is unavailable")
        local entry = gKosovoItemConfig:GetEntryWithName(name)
        require_condition(entry ~= nil, "item is not registered")
        return entry
    end
    --<summary>
    --返回加载、夜间搜刮、庇护所或其他玩法阶段
    --</summary>
    context.game.phase.current = function()
        --读取已核验的游戏委托与游戏加载状态绑定
        if gGame and gGame.IsLoadingScreenActive and gGame:IsLoadingScreenActive() then return "loading" end
        if gGameDelegate and gGameDelegate.IsScavenge and gGameDelegate:IsScavenge() then return "scavenge" end
        if gGameDelegate and gGameDelegate.IsCoreGameplayPhase and gGameDelegate:IsCoreGameplayPhase() then return "shelter" end
        return "other"
    end
    --<summary>
    --读取当前场景的天数小时和角色数量
    --</summary>
    context.game.scene.state = function()
        --在场景不可用时由统一校验给出准确错误
        local scene = get_game_scene()
        return {day = scene:GetCurrentDay(), hour = scene.GetCurrentHour and scene:GetCurrentHour() or nil,
            character_count = scene:GetDwellerCount()}
    end
    --读取已核验的原生存档载入标记避免新战役事件在读档时重放
    context.game.campaign.is_loading_saved_game = function()
        --原生接口不可用时明确报错不猜测当前战役状态
        require_condition(gKosovoGlobalState ~= nil and type(gKosovoGlobalState.IsJustLoadedGame) == "function", "campaign load state is unavailable")
        return gKosovoGlobalState:IsJustLoadedGame()
    end
    --<summary>
    --读取当前玩法场景中的幸存者数量
    --</summary>
    context.game.characters.count = function()
        --角色数量由当前场景原生接口返回
        return get_game_scene():GetDwellerCount()
    end
    --<summary>
    --构造受校验的角色状态和物资操作门面
    --</summary>
    local function character_api(dweller)
        --仅导出已核验的角色查询和物资方法
        require_condition(dweller ~= nil and type(dweller.GetDwellerName) == "function" and
            type(dweller.GetParameterValue) == "function" and type(dweller.SetParameterValue) == "function", "character binding is incomplete")
        local character = {name = dweller:GetDwellerName()}
        --读取角色存在的数字状态参数
        character.get_parameter = function(name)
            require_condition(type(name) == "string" and #name <= 96 and name:match("^[A-Za-z][A-Za-z0-9_]*$"), "invalid parameter name")
            local value = dweller:GetParameterValue(name)
            require_condition(type(value) == "number" and value == value and math.abs(value) < math.huge, "parameter is unavailable")
            return value
        end
        --写入角色参数并执行游戏依赖求解
        character.set_parameter = function(name, value)
            require_condition(type(name) == "string" and #name <= 96 and name:match("^[A-Za-z][A-Za-z0-9_]*$"), "invalid parameter name")
            require_condition(type(value) == "number" and value == value and math.abs(value) < math.huge, "parameter value must be finite")
            local previous = character.get_parameter(name)
            dweller:SetParameterValue(name, value)
            if type(dweller.SolveParameterDependency) == "function" then dweller:SolveParameterDependency() end
            return {previous = previous, current = character.get_parameter(name)}
        end
        --向指定角色添加已注册物品并返回全局库存变化
        character.add_item = function(name, amount)
            require_condition(type(amount) == "number" and amount == math.floor(amount) and amount >= 1 and amount <= 999, "item amount must be an integer from 1 to 999")
            get_game_item(name)
            require_condition(type(dweller.AddItems) == "function", "character item insertion is unavailable")
            local before = gKosovoGlobalState and gKosovoGlobalState.GetGlobalItemCount and gKosovoGlobalState:GetGlobalItemCount(name) or nil
            dweller:AddItems(name, amount)
            local after = gKosovoGlobalState and gKosovoGlobalState.GetGlobalItemCount and gKosovoGlobalState:GetGlobalItemCount(name) or nil
            return {name = name, amount = amount, global_before = before, global_after = after}
        end
        --消耗角色可访问的全局物品并返回原生操作结果
        character.consume_item = function(name)
            get_game_item(name)
            require_condition(type(dweller.ConsumeGlobalItem) == "function", "character item consumption is unavailable")
            return dweller:ConsumeGlobalItem(name, true)
        end
        --查询角色是否可使用指定工具或已携带该工具
        character.can_use_tool = function(name)
            get_game_item(name)
            require_condition(type(dweller.CanEquipTool) == "function" and type(dweller.HasEquippedItemOrTool) == "function", "tool query is unavailable")
            return dweller:CanEquipTool(name) or dweller:HasEquippedItemOrTool(name)
        end
        return character
    end
    --<summary>
    --按零起始序号取得当前场景中的幸存者门面
    --</summary>
    context.game.characters.get = function(index)
        --拒绝越界序号并通过原生场景对象取得角色
        local scene = get_game_scene()
        require_condition(type(index) == "number" and index == math.floor(index) and index >= 0 and index < scene:GetDwellerCount(), "character index is out of range")
        return character_api(scene:GetDweller(index))
    end
    --<summary>
    --查询已注册物品的公开数值配置
    --</summary>
    context.game.items.get = function(name)
        --仅返回已核验的物品字段不泄露原生配置对象
        local entry = get_game_item(name)
        local result = {name = name, properties = {}}
        for _, property in ipairs({"Value", "StackSize", "HP", "BulletsPerShot", "BulletTimeInterval", "CooldownTime",
            "CombatSinA", "CombatSinB", "CombatSinC", "CombatSinMax", "DamageBoostMultiplier"}) do
            local value = entry[property]
            if type(value) == "number" and value == value and math.abs(value) < math.huge then result.properties[property] = value end
        end
        return result
    end
    --<summary>
    --读取游戏全局物资数量
    --</summary>
    context.game.inventory.global_count = function(name)
        --通过注册表校验后才查询原生全局库存
        get_game_item(name)
        require_condition(gKosovoGlobalState ~= nil and type(gKosovoGlobalState.GetGlobalItemCount) == "function", "global inventory is unavailable")
        return gKosovoGlobalState:GetGlobalItemCount(name)
    end
    --<summary>
    --读取庇护所公共物资数量
    --</summary>
    context.game.inventory.shelter_count = function(name)
        --公共仓库仅开放已核验的读取方法
        get_game_item(name)
        require_condition(gKosovoGlobalState ~= nil and type(gKosovoGlobalState.GetShelterItemCount) == "function", "shelter inventory is unavailable")
        return gKosovoGlobalState:GetShelterItemCount(name)
    end
    --<summary>
    --通过原生场景事件广播剧情事件
    --</summary>
    context.game.story.broadcast = function(group, event, character_name)
        --校验标识字符串并调用当前场景已确认的剧情入口
        require_condition(type(group) == "string" and #group <= 128 and type(event) == "string" and #event <= 128, "invalid story event")
        local scene = get_game_scene()
        require_condition(type(scene.BroadcastStoryEvent) == "function", "story event binding is unavailable")
        return scene:BroadcastStoryEvent(group, event, character_name)
    end
    context.schedule = {}
    --<summary>
    --在收到指定数量的游戏日开始事件后执行一次回调
    --</summary>
    context.schedule.after_days = function(days, callback)
        --仅接受正整数避免无意义或永不触发的倒计时
        require_condition(type(days) == "number" and days == math.floor(days) and days >= 1 and days <= 36500, "days must be an integer from 1 to 36500")
        require_condition(type(callback) == "function", "schedule callback must be a function")
        local remaining = days
        local cancel
        cancel = context.events.on("game.day.begin", function(scene, ...)
            --在回调前解除订阅确保异常也不会造成重复执行
            remaining = remaining - 1
            if remaining == 0 then
                cancel()
                callback(scene, ...)
            end
        end)
        return cancel
    end
    --<summary>
    --每收到指定间隔的游戏日开始事件重复执行回调
    --</summary>
    context.schedule.every_days = function(interval, callback)
        --周期复用正整数限制并要求回调显式存在
        require_condition(type(interval) == "number" and interval == math.floor(interval) and interval >= 1 and interval <= 36500, "interval must be an integer from 1 to 36500")
        require_condition(type(callback) == "function", "schedule callback must be a function")
        local elapsed = 0
        return context.events.on("game.day.begin", function(scene, ...)
            --以已确认的白天开始事件累计游戏日而非现实时间
            elapsed = elapsed + 1
            if elapsed >= interval then
                elapsed = 0
                callback(scene, ...)
            end
        end)
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
    --复制订阅快照避免回调中注册新订阅造成无限遍历
    local snapshot = {}
    for _, item in ipairs(listeners[name] or {}) do table.insert(snapshot, item) end
    for _, item in ipairs(snapshot) do
        if item.active then
            guarded(item.id, function() item.callback(unpack(args, 1, args.n)) end)
        end
    end
end

--<summary>
--加载单个模组并跳过运行时加载失败的依赖
--</summary>
function api.load_mod(id, source, dependencies, options)
    --打包入口必须先注册完整元数据不自动补全缺失的加载阶段
    require_condition(api.mod_by_id[id] ~= nil, "mod metadata must be registered before loading")
    require_condition(type(dependencies) == 'table' and type(options) == 'table' and
        type(options.config) == 'table' and type(options.modules) == 'table' and
        type(options.capabilities) == 'table', 'invalid mod loading contract')
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
    local context, rollback = context_for(id, dependencies, options)
    metadata.status = "loading"
    local ok, failure = guarded(id, function()
        local chunk, err = compile(source, "@MaxyModLoader/mods/" .. id)
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
