local api = MaxyModLoader
--离线测试和没有已核验原生绑定的平台不安装游戏主线程桥
if not LuaGameDelegate or not gGame or not gGameDelegate then return end
local context = {config = {poll_interval = 0.1}, log = function(value) api.log('mcp', value) end}
local module = {}
local last_poll = -1
local root = "MaxyModLoader/mcp/"
local array_type = {}

--<summary>
--标记数组以便空集合仍然输出正确的JSON数组
--</summary>
function module.array(value)
    return setmetatable(value or {}, array_type)
end

--<summary>
--将UTF8字符串转换为JSON字符串且转义控制字符
--</summary>
local function quote(value)
    --逐字节保留中文只转义JSON保留字符和控制字节
    return '"' .. tostring(value):gsub('[%z\1-\31\\"]', function(byte)
        return string.format("\\u%04x", string.byte(byte))
    end) .. '"'
end

--<summary>
--将受控返回值转换为JSON避免泄露原生对象地址
--</summary>
function module.json(value)
    --返回值只接受数据类型原生对象必须先提取为普通表
    local kind = type(value)
    if kind == "nil" then return "null" end
    if kind == "boolean" then return value and "true" or "false" end
    if kind == "number" then
        if value ~= value or value == math.huge or value == -math.huge then return "null" end
        return tostring(value)
    end
    if kind == "string" then return quote(value) end
    if kind ~= "table" then error("返回值包含非数据类型") end
    local parts = {}
    --显式标记保留空数组非空连续数字键也按数组输出
    if getmetatable(value) == array_type or #value > 0 then
        for index = 1, #value do table.insert(parts, module.json(value[index])) end
        return "[" .. table.concat(parts, ",") .. "]"
    end
    local keys = {}
    for key in pairs(value) do table.insert(keys, key) end
    table.sort(keys)
    for _, key in ipairs(keys) do table.insert(parts, quote(key) .. ":" .. module.json(value[key])) end
    return "{" .. table.concat(parts, ",") .. "}"
end

--<summary>
--取得当前可操作的原生界面根节点
--</summary>
function module.screen()
    --主菜单和游戏场景共享引擎的前置界面或游戏覆盖界面
    local screen = gGame:GetPreFSEUIScreen()
    if screen and screen:IsVisible() and screen:GetFirstChild() then return screen end
    local overlay = gGameDelegate:GetGameOverlayScreen()
    if overlay and overlay:IsVisible() then return overlay end
    return nil
end

--<summary>
--按有界深度遍历实际界面层级并返回稳定的路径
--</summary>
function module.ui_tree(include_hidden)
    local result = module.array()
    local screen = module.screen()
    if not screen then return {available = false, elements = result} end
    --限制深度和节点数量避免异常界面层级阻塞游戏主线程
    --<summary>
    --只遍历实际可见的分支避免隐藏页面耗尽节点上限
    --</summary>
    local function visit(element, path, depth)
        if depth > 24 or #result >= (include_hidden and 4000 or 1000) then return end
        if depth > 0 and not include_hidden and not element:IsVisible() then return end
        local name = element:GetName() or ""
        local current = path .. "/" .. name
        table.insert(result, {name = name, path = current, visible = element:IsVisible(),
            enabled = element:IsEnabled(), kind = tolua and tolua.type(element) or "UIElement"})
        local child = element:GetFirstChild()
        while child do
            visit(child, current, depth + 1)
            child = child:GetNextSibling()
        end
    end
    visit(screen, "", 0)
    return {available = true, elements = result}
end

--<summary>
--读取当前场景和真实角色参数
--</summary>
function module.state()
    local result = {loader = MaxyModLoader.name, version = MaxyModLoader.version,
        paused = gGame:IsPaused(), gameplay_paused = gGame:IsGameplayPaused(), characters = module.array()}
    --非玩法场景没有角色接口时返回空状态不制造替身
    if gScene and gScene.GetDwellerCount then
        result.day = gScene:GetCurrentDay()
        result.dweller_count = gScene:GetDwellerCount()
        for index = 0, result.dweller_count - 1 do
            local dweller = gScene:GetDweller(index)
            table.insert(result.characters, {index = index, name = dweller:GetDwellerName(),
                hungry = dweller:GetParameterValue("Hungry"), tired = dweller:GetParameterValue("Tired"),
                sick = dweller:GetParameterValue("Sick"), wounded = dweller:GetParameterValue("Wounded")})
        end
    end
    return result
end

--<summary>
--列出公开绑定的方法名称以支持原生接口诊断
--</summary>
function module.inspect(name)
    --只接受单个公开类型名不能索引任意对象或执行代码
    if not name or not name:match("^[A-Za-z][A-Za-z0-9_]*$") then error("类型名无效") end
    local value = _G[name]
    if type(value) == "userdata" then value = getmetatable(value) end
    if type(value) ~= "table" then error("未找到公开类型") end
    local methods = module.array()
    for key, item in pairs(value) do
        if type(key) == "string" and key:sub(1, 1) ~= "." and key:sub(1, 2) ~= "__" then
            table.insert(methods, {name = key, kind = type(item)})
        end
    end
    --属性只返回公开名称不会求值或暴露内部取值函数
    local properties = rawget(value, '.get')
    if type(properties) == 'table' then
        for key in pairs(properties) do
            if type(key) == 'string' and key:sub(1, 1) ~= '.' then
                table.insert(methods, {name = key, kind = 'property'})
            end
        end
    end
    table.sort(methods, function(a, b) return a.name < b.name end)
    return {name = name, members = methods}
end

--<summary>
--读取屏幕归一化坐标处的原生命中结果用于截图与控件位置对照
--</summary>
function module.hit_test(argument)
    --接受有限的零到一坐标不会移动真实鼠标或改变桌面焦点
    local x, y = (argument or ''):match('^([%d.]+),([%d.]+)$')
    x, y = tonumber(x), tonumber(y)
    if not x or not y or x < 0 or x > 1 or y < 0 or y > 1 then error('坐标格式必须为0到1之间的x,y') end
    local screen = module.screen()
    if not screen then error('当前没有可操作界面') end
    local element = screen:GetElementAtScreenPosition(Vector:Instance(x, y, 0, 0))
    local chain = module.array()
    local node = element
    --返回父链可以确认文字是否遮挡按钮而不是靠截图猜测事件目标
    for depth = 1, 24 do
        if not node then break end
        table.insert(chain, {name = node:GetName(), visible = node:IsVisible(), enabled = node:IsEnabled(),
            mouse_focusable = node:IsFlag(UIFLAG_FOCUSABLEWITHMOUSE)})
        node = node:GetParent()
    end
    return element, {x = x, y = y, chain = chain}
end

--<summary>
--读取实际注册物品的公开参数以验证原生配置编译结果
--</summary>
function module.item_config(name)
    --名称只作为原生查询键不能包含路径或可执行表达式
    if type(name) ~= 'string' or #name > 96 or not name:match('^[A-Za-z][A-Za-z0-9_]*$') then error('物品名称无效') end
    if not gKosovoItemConfig then error('原生物品配置尚未就绪') end
    local entry = gKosovoItemConfig:GetEntryWithName(name)
    if not entry then return {name = name, registered = false} end
    --只复制已核验的数字属性避免返回原生对象或读取未知偏移
    local result = {name = name, registered = true, properties = {}}
    for _, property in ipairs({'Value', 'StackSize', 'HP', 'BulletsPerShot', 'BulletTimeInterval',
        'CooldownTime', 'CombatSinA', 'CombatSinB', 'CombatSinC', 'CombatSinMax', 'DamageBoostMultiplier'}) do
        local value = entry[property]
        if type(value) == 'number' then result.properties[property] = value end
    end
    return result
end

--<summary>
--分发白名单命令所有游戏操作均在主线程执行
--</summary>
function module.dispatch(command, argument)
    if command == 'display_host_ready' then
        if not api.display then error('原版设置扩展未安装') end
        return api.display.ready(argument)
    end
    if command == 'display_request' then
        if not api.display then error('原版设置扩展未安装') end
        return api.display.request(argument)
    end
    if command == 'settings_state' then
        if not api.display then error('原版设置扩展未安装') end
        return api.display.state()
    end
    if command == 'display_state' then
        if not gConfigHelper then error('游戏显示设置接口不可用') end
        return {fullscreen = gConfigHelper:GetFullScreen(), mode = gConfigHelper:GetScreenMode()}
    end
    if command == 'display_prepare' then
        if not gConfigHelper then error('游戏显示设置接口不可用') end
        local mode, width, height = (argument or ''):match('^(%a+)|(%d+)|(%d+)$')
        width, height = tonumber(width), tonumber(height)
        if not (mode == 'borderless' or mode == 'windowed' or mode == 'fullscreen') or
            not width or not height or width < 640 or height < 480 or width > 8192 or height > 8192 then
            error('显示模式或显示尺寸无效')
        end
        --失败时恢复原显示设置并通过游戏已核验的画面配置入口应用
        local previous = gConfigHelper:GetFullScreen()
        local ok, message = pcall(function()
            gConfigHelper:SetFullScreen(mode == 'fullscreen')
            gGame:RequestScreenResoultionChange(width, height)
            gConfigHelper:ApplySeriousGFXSettings()
            gConfigHelper:SaveConfig()
        end)
        if not ok then
            gConfigHelper:SetFullScreen(previous)
            gConfigHelper:ApplySeriousGFXSettings()
            gConfigHelper:SaveConfig()
            error('显示设置应用失败：' .. tostring(message))
        end
        return {requested = mode, fullscreen = gConfigHelper:GetFullScreen(), previous = previous}
    end
    if command == "game_state" then return module.state() end
    if command == "mods_list" then return {mods = MaxyModLoader.mods} end
    if command == "ui_tree" then return module.ui_tree() end
    if command == 'ui_catalog' then return module.ui_tree(true) end
    if command == 'ui_adjust' then
        if not MaxyModLoader.manager then error('管理界面尚未安装') end
        return MaxyModLoader.manager.adjust(argument)
    end
    if command == 'mod_scroll' then
        if not MaxyModLoader.manager then error('管理界面尚未安装') end
        return MaxyModLoader.manager.scroll(argument)
    end
    if command == "mod_manager" then
        if not MaxyModLoader.manager then return {available = false} end
        --状态查询只核验界面所有权避免同一帧再次应用真实滚轮输入
        MaxyModLoader.manager.attach()
        return MaxyModLoader.manager.state()
    end
    if command == "inspect_type" then return module.inspect(argument) end
    if command == 'item_config' then return module.item_config(argument) end
    if command == 'ui_hit_test' then local element, result = module.hit_test(argument); return result end
    if command == 'ui_click_point' then
        local element, result = module.hit_test(argument)
        if not element or not element:IsEnabled() then error('坐标处没有可操作控件') end
        --原生命中缓存可能保留已关闭页面控件必须核验整条父链
        for _, node in ipairs(result.chain) do
            if not node.visible or not node.enabled then error('坐标命中控件属于隐藏或禁用页面') end
        end
        --自有控件也必须经过原生命中不允许按名字直接调用函数冒充点击验证
        local manager = MaxyModLoader.manager
        if manager and manager.pointed_button(element) then
            manager.pointer(true, false, element)
            result.activated = manager.pointer(false, true, element)
            result.source = 'MCP坐标命中'
        else
            module.screen():SimulateClick(element)
            result.activated, result.source = true, '游戏原生界面'
        end
        return result
    end
    if command == "ui_click" then
        --只点击当前界面中存在且可见的元素不向桌面注入输入
        local screen = module.screen()
        if not screen then error("当前没有可操作界面") end
        if MaxyModLoader.manager and MaxyModLoader.manager.activate(argument) then return {clicked = argument, handler = "MaxyModLoader"} end
        --名称重复时拒绝歧义调用者可使用界面树返回的完整路径
        local matches, visited = {}, 0
        --<summary>
        --在可见界面中查找唯一目标并限制遍历工作量
        --</summary>
        local function find(element, path, depth)
            if depth > 24 or visited >= 1000 or not element:IsVisible() then return end
            visited = visited + 1
            local name = element:GetName() or ""
            local current = path .. "/" .. name
            if argument == name or argument == current then table.insert(matches, element) end
            local child = element:GetFirstChild()
            while child do find(child, current, depth + 1); child = child:GetNextSibling() end
        end
        find(screen, "", 0)
        if visited >= 1000 then error("界面节点超出遍历上限无法确认点击目标唯一性") end
        if #matches ~= 1 or not matches[1]:IsEnabled() then error("元素不可点击或名称存在歧义请使用完整路径") end
        local element = matches[1]
        screen:SimulateClick(element)
        return {clicked = argument}
    end
    if command == "pause" then
        --暂停值明确传递而不是不确定的切换操作
        if argument ~= "true" and argument ~= "false" then error("暂停值必须是布尔值") end
        gGame:SetUserPause(argument == "true")
        return {requested = argument == "true", paused = gGame:IsPaused()}
    end
    if command == "end_day" then
        if not gScene or not gScene.GetDwellerCount or gScene:GetDwellerCount() == 0 or
            not gGameDelegate:IsCoreGameplayPhase() or gGameDelegate:IsScavenge() then error("当前没有可推进的庇护所白天") end
        gGameDelegate:EndDay()
        return {requested = true}
    end
    if command == "quit" then
        --用于可恢复部署的正常退出请求不终止其他桌面程序
        gGame:Quit()
        return {requested = true}
    end
    error("未知游戏命令：" .. tostring(command))
end

--<summary>
--读取原子发布的单个请求并写回关联ID的结果
--</summary>
function module.poll()
    --降低磁盘探测频率且只处理固定目录下的固定文件
    local now = os.clock()
    if now - last_poll < (context.config.poll_interval or 0.1) then return end
    last_poll = now
    local file = io.open(root .. "request.txt", "rb")
    if not file then return end
    local request = file:read(65537)
    file:close()
    if #request > 65536 then os.remove(root .. "request.txt"); return end
    local id, command, encoded = request:match("^MML1\n([a-f0-9]+)\n([a-z_]+)\n([a-f0-9]*)\n$")
    os.remove(root .. "request.txt")
    if not id or #id ~= 32 or #encoded % 2 ~= 0 then return end
    local argument = encoded:gsub("..", function(byte) return string.char(tonumber(byte, 16)) end)
    --异常结果传给调用者不会扩散到游戏原本的帧回调
    local ok, result = pcall(module.dispatch, command, argument)
    local output = module.json({id = id, ok = ok, result = ok and result or nil, error = not ok and tostring(result) or nil})
    local response = io.open(root .. "response.tmp", "wb")
    if not response then context.log("无法写入MCP返回文件"); return end
    response:write(output)
    response:close()
    os.remove(root .. "response.json")
    os.rename(root .. "response.tmp", root .. "response.json")
end
--内置桥在所有正常帧和暂停帧工作不会作为第三方模组占用目录
api.mcp = module
--<summary>
--内置控制桥启用期间保持后台主线程命令处理
--</summary>
LuaGameDelegate.CanSleep = function() return false end
for _, name in ipairs({'OnTick', 'OnPauseTick'}) do
    local previous = LuaGameDelegate[name]
    LuaGameDelegate[name] = function(delegate, ...)
        module.poll()
        return previous(delegate, ...)
    end
end
context.log('内置游戏主线程MCP控制桥已安装')
return module
