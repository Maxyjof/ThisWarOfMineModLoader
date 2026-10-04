local api = MaxyModLoader
--离线Lua测试和未知平台没有原生UI时不安装界面
if not LuaGameDelegate or not UIButton or not UITextBox or not Vector then return end
local manager = {open = false, selection = 1, list_page = 1, detail_page = 1, buttons = {}}
api.manager = manager
local statuses = {loaded = '已加载', failed = '加载失败', skipped = '依赖失败', disabled = '已禁用', pending = '等待加载', loading = '加载中'}

--<summary>
--将UTF8文本解码为Unicode码点用于换行和原生中文显示
--</summary>
local function characters(value)
    local result, index = {}, 1
    --错误编码替换为占位符合法四字节字符保留完整码点
    while index <= #value do
        local byte, count, code = value:byte(index), 1, value:byte(index)
        if byte >= 240 then count, code = 4, byte - 240
        elseif byte >= 224 then count, code = 3, byte - 224
        elseif byte >= 192 then count, code = 2, byte - 192 end
        local valid = byte < 128 or byte >= 194 and byte <= 244
        for offset = 1, count - 1 do
            local next_byte = value:byte(index + offset)
            if not next_byte or next_byte < 128 or next_byte > 191 then valid = false; break end
            code = code * 64 + next_byte - 128
        end
        if not valid or code > 1114111 or code >= 55296 and code <= 57343 or
            count == 2 and code < 128 or count == 3 and code < 2048 or count == 4 and code < 65536 then
            count, code = 1, 65533
        end
        table.insert(result, {code = code, text = value:sub(index, index + count - 1)})
        index = index + count
    end
    return result
end

--<summary>
--使用引擎UTF16字符串避免中文经过本地ANSI编码
--</summary>
local function unicode(value)
    local result = LuaUnicodeString:Instance()
    --引擎单字符为UTF16补充平面字符拆成代理项
    for _, item in ipairs(characters(value)) do
        if item.code <= 65535 then result:AppendChar(item.code)
        else
            local scalar = item.code - 65536
            result:AppendChar(55296 + math.floor(scalar / 1024))
            result:AppendChar(56320 + scalar % 1024)
        end
    end
    return result
end
manager.unicode = unicode

--<summary>
--创建统一的原生向量
--</summary>
local function vector(x, y)
    return Vector:Instance(x, y, 0, 0)
end

--<summary>
--创建拥有中文字体的原生多行文字控件
--</summary>
local function text(parent, name, value, x, y, width, height, size)
    --裸构造函数缺少配方初始化的文字渲染状态使用原主菜单配方创建独立实例
    local element = parent:FindElementByName(name)
    local attached = element ~= nil
    element = element or manager.template:CreateElementFromSubRecipe('BUTTON_NAME')
    if not element or not element:IsUITextBase() then error('游戏主菜单文字配方不可用') end
    element:SetName(name)
    element:SetWindowAlignment(UIWINDOWALIGNMENT_NONE)
    element:SetAnchor(vector(0, 0))
    element:SetFont('NotoSansCJKsc-Medium.otf', size or 20, true)
    element:SetTextConversion(0)
    element:SetLinesToRender(0, 100)
    --原按钮配方行距适合单行字体多行中文需要重新指定避免字行重叠
    element:SetLineSpacingScale(1.2)
    element:SetColor(1, 1, 1, 1)
    element:SetColorMode(UICOLOR_STATIC)
    element:SetAlignment(TEXTALIGNMENT_LEFT)
    element:SetSize(vector(width, height))
    element:SetPosition(vector(x, y))
    element:SetText(unicode(value))
    if not attached then parent:AddChild(element) end
    return element
end

--<summary>
--创建同时支持真实鼠标和MCP操作的独立按钮
--</summary>
local function button(parent, name, value, x, y, width, handler)
    local element = parent:FindElementByName(name)
    local attached = element ~= nil
    element = element or UIButton:new()
    element:SetName(name)
    --运行时新建按钮必须显式加入鼠标可聚焦集合否则只会绘制而不参与原生命中
    element:RaiseFlag(UIFLAG_FOCUSABLEWITHMOUSE)
    element:SetColor(0.16, 0.16, 0.16, 1)
    element:SetColorMode(UICOLOR_STATIC)
    element:SetSize(vector(width, 44))
    element:SetPosition(vector(x, y))
    if not attached then parent:AddChild(element) end
    text(element, name .. '_TEXT', value, 8, 5, width - 16, 36, 20)
    manager.buttons[name] = {element = element, handler = handler}
    return element
end

--<summary>
--按文字宽度估计拆行避免长介绍覆盖控件
--</summary>
local function wrap(value)
    local lines, current, width = {}, {}, 0
    for _, item in ipairs(characters(value)) do
        local weight = item.code < 128 and 1 or 2
        if item.code == 10 or width + weight > 58 then
            table.insert(lines, table.concat(current)); current, width = {}, 0
        end
        if item.code ~= 10 then table.insert(current, item.text); width = width + weight end
    end
    table.insert(lines, table.concat(current))
    return lines
end

--<summary>
--整理所选模组的完整介绍而不是只显示模组ID
--</summary>
function manager.details(mod)
    local lines = {'名称：' .. mod.name, '标识：' .. mod.id, '版本：' .. mod.version,
        '作者：' .. (mod.author ~= '' and mod.author or '未提供'),
        '状态：' .. (statuses[mod.status] or mod.status), '', '内容介绍：',
        mod.description ~= '' and mod.description or '作者尚未提供内容介绍', '', '功能说明：'}
    for _, feature in ipairs(mod.features or {}) do table.insert(lines, '* ' .. feature) end
    table.insert(lines, '兼容说明：' .. (mod.compatibility or '未提供'))
    --依赖排序显示使不同运行时的介绍顺序保持一致
    local dependencies = {}
    for id, version in pairs(mod.dependencies or {}) do table.insert(dependencies, id .. '，最低版本' .. version) end
    table.sort(dependencies)
    table.insert(lines, '依赖：' .. (#dependencies > 0 and table.concat(dependencies, '，') or '无'))
    table.insert(lines, '冲突：' .. (#(mod.conflicts or {}) > 0 and table.concat(mod.conflicts, '，') or '无'))
    table.insert(lines, '主页：' .. (mod.website ~= '' and mod.website or '未提供'))
    table.insert(lines, '许可：' .. (mod.license ~= '' and mod.license or '未指定'))
    if mod.error and mod.error ~= '' then table.insert(lines, '错误详情：' .. mod.error) end
    return wrap(table.concat(lines, '\n'))
end

--<summary>
--刷新模组列表和介绍分页并保持当前选择
--</summary>
function manager.refresh()
    local first = (manager.list_page - 1) * 7
    for index = 1, 7 do
        local mod = api.mods[first + index]
        local row = manager.rows[index]
        row:SetVisible(mod ~= nil)
        if mod then
            row:FindElementByName('MML_ROW_' .. index .. '_TEXT'):SetText(unicode(
                (first + index == manager.selection and '> ' or '') .. mod.name .. '\n' .. (statuses[mod.status] or mod.status)))
        end
    end
    local mod = api.mods[manager.selection]
    local lines = mod and manager.details(mod) or {'当前没有发现模组'}
    local pages = math.max(1, math.ceil(#lines / 14))
    manager.detail_page = math.min(manager.detail_page, pages)
    local page = {}
    for index = (manager.detail_page - 1) * 14 + 1, math.min(manager.detail_page * 14, #lines) do table.insert(page, lines[index]) end
    manager.visible_text = table.concat(page, '\n')
    manager.detail:SetText(unicode(manager.visible_text))
    manager.footer:SetText(unicode('共' .. #api.mods .. '个模组  列表' .. manager.list_page .. '/' .. math.max(1, math.ceil(#api.mods / 7)) ..
        '  介绍' .. manager.detail_page .. '/' .. pages .. '  增删或启用变更需要重新构建并重启游戏'))
end

--<summary>
--切换独立管理面板并恢复原主菜单
--</summary>
function manager.show(visible)
    manager.open = visible
    manager.menu:SetVisible(not visible)
    manager.frame:SetVisible(visible)
    if visible then manager.refresh() end
end

--<summary>
--在原生经典主菜单就绪后创建独立入口和管理面板
--</summary>
function manager.attach()
    local screen = gGame:GetPreFSEUIScreen()
    local menu = screen and screen:FindElementByName('ClassicModeMainMenu')
    --空前置特效界面不遮蔽实际拥有主菜单的覆盖界面
    if not menu then
        screen = gGameDelegate:GetGameOverlayScreen()
        menu = screen and screen:FindElementByName('ClassicModeMainMenu')
    end
    --原生界面由场景拥有切换后旧Lua对象可能指向已释放内存绝不再次读取旧控件
    if screen ~= manager.screen or menu ~= manager.menu then
        manager.buttons, manager.rows = {}, {}
        manager.menu, manager.frame, manager.template = nil, nil, nil
        manager.detail, manager.footer, manager.visible_text = nil, nil, ''
        manager.screen, manager.open, manager.pressed = screen, false, nil
    end
    if not menu then return end
    if manager.frame then return end
    --游戏界面以720逻辑高度缩放横坐标根据当前宽高比计算
    manager.menu, manager.screen, manager.buttons = menu, screen, {}
    manager.template = menu:FindElementByName('BUTTON_STARTNEW')
    if not manager.template then return end
    local entry = button(menu, 'BUTTON_MAXY_MODS', '模组管理  >', 720 * gGame:GetScreenAspect() * 0.79 - 230, 438, 230, function() manager.show(true) end)
    entry:SetWindowAlignment(UIWINDOWALIGNMENT_NONE)
    entry:FindElementByName('BUTTON_MAXY_MODS_TEXT'):SetAlignment(TEXTALIGNMENT_RIGHT)
    manager.frame = screen:FindElementByName('MML_MANAGER')
    local attached = manager.frame ~= nil
    manager.frame = manager.frame or UIElement:new()
    manager.frame:SetName('MML_MANAGER')
    manager.frame:SetSize(vector(1100, 650))
    manager.frame:SetWindowAlignment(UIWINDOWALIGNMENT_NONE)
    manager.frame:SetPosition(vector((720 * gGame:GetScreenAspect() - 1100) / 2, 35))
    manager.frame:SetColor(1, 1, 1, 1)
    manager.frame:SetColorMode(UICOLOR_STATIC)
    if not attached then screen:AddChild(manager.frame) end
    --独立背景和列表不接管游戏原有创意工坊页面
    local background = manager.frame:FindElementByName('MML_BACKGROUND')
    local background_attached = background ~= nil
    background = background or UIRoundedRect:new()
    background:SetName('MML_BACKGROUND')
    background:SetSize(vector(1100, 650))
    background:SetColor(0.035, 0.035, 0.035, 0.98)
    background:SetColorMode(UICOLOR_STATIC)
    if not background_attached then manager.frame:AddChild(background) end
    text(manager.frame, 'MML_TITLE', 'MaxyModLoader 模组管理', 30, 20, 1030, 50, 30)
    manager.rows = {}
    for index = 1, 7 do
        local slot = index
        manager.rows[index] = button(manager.frame, 'MML_ROW_' .. index, '', 30, 90 + (index - 1) * 60, 345, function()
            manager.selection = (manager.list_page - 1) * 7 + slot
            manager.detail_page = 1
            manager.refresh()
        end)
        manager.rows[index]:SetSize(vector(345, 58))
        manager.rows[index]:GetFirstChild():SetSize(vector(329, 58))
    end
    manager.detail = text(manager.frame, 'MML_DETAIL', '', 405, 90, 660, 415, 19)
    manager.footer = text(manager.frame, 'MML_FOOTER', '', 30, 600, 1040, 42, 16)
    button(manager.frame, 'MML_LIST_PREVIOUS', '列表上一页', 30, 530, 170, function()
        manager.list_page = math.max(1, manager.list_page - 1); manager.refresh()
    end)
    button(manager.frame, 'MML_LIST_NEXT', '列表下一页', 210, 530, 170, function()
        manager.list_page = math.min(math.max(1, math.ceil(#api.mods / 7)), manager.list_page + 1); manager.refresh()
    end)
    button(manager.frame, 'MML_DETAIL_PREVIOUS', '介绍上一页', 405, 530, 185, function()
        manager.detail_page = math.max(1, manager.detail_page - 1); manager.refresh()
    end)
    button(manager.frame, 'MML_DETAIL_NEXT', '介绍下一页', 600, 530, 185, function()
        manager.detail_page = manager.detail_page + 1; manager.refresh()
    end)
    button(manager.frame, 'MML_CLOSE', '返回主菜单', 850, 530, 215, function() manager.show(false) end)
    manager.frame:SetVisible(false)
    manager.open = false
    api.log('manager', '独立模组管理入口已创建')
end

--<summary>
--从原生命中的文字或子控件向上查找当前界面拥有的按钮
--</summary>
function manager.pointed_button(element)
    --原生文字也参与命中不能只比较最深层控件的名字
    for depth = 1, 24 do
        if not element then return nil end
        local item = manager.buttons[element:GetName()]
        if item and item.element == element and element:IsVisible() and element:IsEnabled() then
            --检查父层可见性防止隐藏管理面板中的行被误点
            if element:IsDescendantOf(manager.frame) and not manager.open then return nil end
            if element:IsDescendantOf(manager.menu) and manager.open then return nil end
            return item
        end
        element = element:GetParent()
    end
end

--<summary>
--按照按下和释放成对处理按钮避免拖出区域仍然激活
--</summary>
function manager.pointer(down, up, element)
    local pointed = manager.pointed_button(element)
    --悬停和按下状态直接显示在文字上提供可辨认的交互反馈
    for name, item in pairs(manager.buttons) do
        local label = item.element:FindElementByName(name .. '_TEXT')
        if label then
            if item == pointed then label:SetColor(1, 0.55, 0.16, 1)
            else label:SetColor(1, 1, 1, 1) end
        end
    end
    if down then manager.pressed = pointed end
    if up then
        local pressed = manager.pressed
        manager.pressed = nil
        if pressed and pressed == pointed then
            manager.click_count = (manager.click_count or 0) + 1
            pressed.handler()
            return true
        end
    end
    return false
end

--<summary>
--在游戏帧中读取原生鼠标边沿与界面命中而不发送桌面输入
--</summary>
function manager.tick()
    --首次失败后停止重试防止异常控件反复影响游戏
    if manager.failed then return end
    manager.attach()
    if not manager.frame then return end
    local cursor = gGame:GetCursorPosition()
    local element = manager.screen:GetElementAtScreenPosition(cursor)
    local down = gGame:IsMouseButtonPressedForTheFirstTime(65536)
    local up = gGame:IsMouseButtonReleasedForTheFirstTime(65536)
    --保存实际命中和最近输入边沿让MCP诊断显示真实鼠标是否经过管理逻辑
    manager.input = {active = gGame:IsActive(), on_window = gGame:IsCursorOnGameWindow(),
        x = cursor.x, y = cursor.y, pointed = element and element:GetName() or '', down = down, up = up}
    if down or up then
        manager.last_input = manager.input
        api.log('manager.input', tostring(down) .. '/' .. tostring(up) .. '/' .. manager.input.pointed)
    end
    --鼠标左键编码由当前版本的公开绑定映射核验不能使用触摸接口冒充鼠标接口
    if not gGame:IsActive() or not gGame:IsCursorOnGameWindow() then
        manager.pressed = nil
        return
    end
    manager.pointer(down, up, element)
end

--<summary>
--让MCP使用同一按下释放逻辑且保留操作来源
--</summary>
function manager.activate(name)
    --MCP轮询可能先于管理帧执行先重新校验当前界面所有权
    manager.attach()
    local item = manager.buttons[name]
    if not item then return false end
    if manager.pointed_button(item.element) ~= item then error('管理按钮当前不可操作') end
    manager.pointer(true, false, item.element)
    return manager.pointer(false, true, item.element)
end

--<summary>
--返回独立面板的实际可见状态与当前显示的介绍
--</summary>
function manager.state()
    return {available = manager.frame ~= nil, open = manager.frame and manager.frame:IsVisible() or false,
        selected = api.mods[manager.selection] and api.mods[manager.selection].id or '',
        list_page = manager.list_page, detail_page = manager.detail_page, text = manager.visible_text or '',
        font_height = manager.detail and manager.detail:GetFontHeight() or 0,
        clicks = manager.click_count or 0, input = manager.input or {}, last_input = manager.last_input or {},
        error = manager.failed or false}
end

--主菜单走暂停帧正常玩法走普通帧两个入口均保留原始回调
for _, name in ipairs({'OnTick', 'OnPauseTick'}) do
    local previous = LuaGameDelegate[name]
    LuaGameDelegate[name] = function(delegate, ...)
        local ok, error_message = pcall(manager.tick)
        if not ok and not manager.failed then api.log('manager', tostring(error_message)); manager.failed = true end
        return previous(delegate, ...)
    end
end
