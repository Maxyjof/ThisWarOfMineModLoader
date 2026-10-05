local api = MaxyModLoader
--没有原生设置界面的平台不安装显示扩展
if not LuaGameDelegate or not gConfigHelper or not api.manager then return end
local display = {host = false, mode = gConfigHelper:GetFullScreen() and 'fullscreen' or 'windowed', sequence = 0, buttons = {}}
api.display = display
local modes = {'windowed', 'fullscreen', 'borderless'}
local labels = {windowed = '窗口', fullscreen = '全屏', borderless = '无边框全屏'}

--<summary>
--从原生设置命中中取得独立设置行
--</summary>
local function setting_slot(screen)
    --原版二值选择器到达端点时会隐藏一个箭头两侧都需尝试命中
    for _, point in ipairs({{0.49, 0.51}, {0.746, 0.495}}) do
        local slot = screen:GetElementAtScreenPosition(Vector:Instance(point[1], point[2], 0, 0))
        for depth = 1, 12 do
            if not slot or slot:GetName() == 'SETTING_SLOT' then break end
            slot = slot:GetParent()
        end
        if slot and slot:GetName() == 'SETTING_SLOT' then return slot end
    end
    return nil
end

--<summary>
--发布单次显示设置请求并等待后台服务确认
--</summary>
function display.request(mode)
    if not display.host then error('窗口辅助服务尚未就绪') end
    if display.pending then error('上一次显示模式切换尚未完成') end
    if not labels[mode] then error('显示模式无效') end
    display.sequence = display.sequence + 1
    local id = string.format('%016x%016x', os.time(), display.sequence)
    --临时文件完整写完才发布原子请求不会向系统解释模式文本
    local file = io.open('MaxyModLoader/display-request.tmp', 'wb')
    if not file then error('无法写入显示设置请求') end
    file:write('MMLD1\n' .. id .. '\n' .. mode .. '\n')
    file:close()
    local ok, message = os.rename('MaxyModLoader/display-request.tmp', 'MaxyModLoader/display-request.txt')
    if not ok then error(message) end
    display.pending, display.requested_at, display.error = id, os.time(), nil
    return {id = id, requested = mode}
end

--<summary>
--在原版选择器配方中创建拥有相同外观的自有按钮
--</summary>
local function control(parent, recipe, name, handler)
    local element = parent:FindElementByName(name)
    if not element then
        element = parent:CreateElementFromSubRecipe(recipe)
        if not element then error('设置按钮配方不可用：' .. recipe) end
        element:SetName(name)
        element:RaiseFlag(UIFLAG_FOCUSABLEWITHMOUSE)
        parent:AddChild(element)
    end
    --回调保留原版设置的应用顺序并明确标注所属页面
    display.buttons[name] = {element = element, handler = handler,
        available = function() return display.open == true and display.host and not display.pending end}
    return element
end

--<summary>
--从原生命中父链取得设置页面独立拥有的按钮
--</summary>
function display.pointed_button(element)
    if not display.open or not display.host or display.pending then return nil end
    local pointed
    --设置行可能由原版助手独立持有核验完整父链而不依赖主菜单面板
    for depth = 1, 24 do
        if not element then break end
        if not element:IsVisible() or not element:IsEnabled() then return nil end
        local item = display.buttons[element:GetName()]
        if item and item.element == element then pointed = item end
        element = element:GetParent()
    end
    return pointed
end

--<summary>
--成对处理设置按钮的按下释放并复用原版补间反馈
--</summary>
function display.pointer(down, up, element)
    local pointed = display.pointed_button(element)
    --控制按钮使用自身配方的悬停动画不会修改原版设置按钮
    for _, item in pairs(display.buttons) do item.element:SetHighlight(item == pointed, false) end
    if down then display.pressed = pointed end
    if up then
        local pressed = display.pressed
        display.pressed = nil
        if pressed and pressed == pointed then pressed.handler(); return true end
    end
    return false
end

--<summary>
--让MCP通过设置页面同一交互路径验证自有控件
--</summary>
function display.activate(name)
    display.attach()
    local item = display.buttons[name]
    if not item then return false end
    if display.pointed_button(item.element) ~= item then error('设置按钮当前不可操作') end
    display.pointer(true, false, item.element)
    return display.pointer(false, true, item.element)
end

--<summary>
--更改待应用模式而不提前写入玩家设置
--</summary>
function display.select(direction)
    local index = 1
    for value, mode in ipairs(modes) do if mode == display.selected then index = value end end
    display.selected = modes[math.max(1, math.min(#modes, index + direction))]
    display.refresh()
end

--<summary>
--刷新原版设置行的文字和两侧箭头
--</summary>
function display.refresh()
    if not display.slot then return end
    --保留原版字体字号颜色背景和补间动作只替换实际文字内容
    display.slot:FindElementByName('TITLE'):SetText(api.manager.unicode('窗口模式'))
    display.value:SetText(api.manager.unicode(labels[display.selected]))
    --原版助手会在绘制阶段重写二值文本独立文字控件防止帧间覆盖
    display.original_value:Hide()
    display.left:SetVisible(display.selected ~= 'windowed')
    display.right:SetVisible(display.selected ~= 'borderless')
    display.original_left:Hide()
    display.original_right:Hide()
end

--<summary>
--在原版设置页面内接入三种窗口模式
--</summary>
function display.attach()
    local screen = api.mcp and api.mcp.screen(false, 'Settings')
    local panel = screen and screen:FindElementByName('Settings')
    local visible = panel and panel:IsVisible() or false
    --场景切换可能销毁旧控件先比较根节点不读取旧对象的任何属性
    if screen ~= display.screen then
        display.buttons, display.pressed = {}, nil
        display.slot, display.apply, display.original_apply = nil, nil, nil
        display.open, display.screen = false, screen
    end
    if not visible then display.open, display.pressed = false, nil; return end
    if not display.host then return end
    local slot = setting_slot(screen)
    if not slot then return end
    if not display.open or display.slot ~= slot then
        --设置面板淡入期间应用按钮可能尚未参与命中等下一帧再接入
        local apply = screen:GetElementAtScreenPosition(Vector:Instance(0.35, 0.935, 0, 0))
        while apply and apply:GetName() ~= 'BUTTON_APPLY' and apply:GetName() ~= 'MML_SETTINGS_APPLY' do apply = apply:GetParent() end
        if not apply then return end
        local area = apply:GetParent()
        local original_apply = apply:GetName() == 'BUTTON_APPLY' and apply or area:FindElementByName('BUTTON_APPLY')
        original_apply = original_apply or display.original_apply
        if not original_apply or original_apply:GetParent() ~= area then return end
        --每次打开从已确认模式开始取消或关闭不会保留未应用选择
        display.slot, display.selected = slot, display.mode
        local slider = slot:FindElementByName('SLIDER')
        display.original_left = slider:FindElementByName('BUTTON_LEFT')
        display.original_right = slider:FindElementByName('BUTTON_RIGHT')
        display.original_value = slider:FindElementByName('VALUE')
        display.value = slider:FindElementByName('MML_WINDOW_VALUE')
        if not display.value then
            --选择器的原生VALUE动态生成先尝试同名配方再复制其实际文字渲染状态
            display.value = slider:CreateElementFromSubRecipe('VALUE')
            if not display.value then
                display.value = UIText:new()
                display.value:CopyText(display.original_value)
                display.value:SetWindowAlignment(UIWINDOWALIGNMENT_CENTER)
                display.value:SetAnchor(Vector:Instance(0.5, 0.5, 0, 0))
                display.value:SetPosition(Vector:Instance(0, 0, 0, 0))
                display.value:SetSize(Vector:Instance(260, 36, 0, 0))
                display.value:SetAlignment(TEXTALIGNMENT_CENTER)
                display.value:SetColor(0, 0, 0, 1)
                display.value:SetColorMode(UICOLOR_STATIC)
            end
            if not display.value:IsUITextBase() then error('设置文字配方不可用') end
            display.value:SetName('MML_WINDOW_VALUE')
            slider:AddChild(display.value)
        end
        display.left = control(slider, 'BUTTON_LEFT', 'MML_WINDOW_LEFT', function() display.select(-1) end)
        display.right = control(slider, 'BUTTON_RIGHT', 'MML_WINDOW_RIGHT', function() display.select(1) end)
        --原版应用按钮仍负责其余设置自有按钮只在原回调后提交窗口模式
        display.original_apply = original_apply
        display.apply = control(area, 'BUTTON_APPLY', 'MML_SETTINGS_APPLY', function()
            local mode = display.selected
            screen:SimulateClick(display.original_apply)
            --先完成原设置助手的缓存写回下一帧再要求引擎改变显示模式
            display.queued, display.queued_frame = mode, gGame:GetBigFrameIndex()
        end)
        display.original_apply:Hide()
        display.open = true
    end
    display.original_apply:Hide()
    display.refresh()
end

--<summary>
--处理设置状态和后台确认结果
--</summary>
function display.tick()
    display.attach()
    --设置输入在普通帧和暂停帧均独立执行不要求主菜单管理面板存在
    if display.open then
        if gGame:IsActive() and gGame:IsCursorOnGameWindow() then
            local element = display.screen:GetElementAtScreenPosition(gGame:GetCursorPosition())
            display.pointer(gGame:IsMouseButtonPressedForTheFirstTime(65536),
                gGame:IsMouseButtonReleasedForTheFirstTime(65536), element)
        else display.pressed = nil end
    end
    if display.queued and gGame:GetBigFrameIndex() > display.queued_frame then
        local mode = display.queued
        display.queued = nil
        display.request(mode)
    end
    if display.pending then
        local file = io.open('MaxyModLoader/display-response.txt', 'rb')
        if file then
            local id, status, message = file:read('*l'), file:read('*l'), file:read('*l')
            file:close()
            if id == display.pending then
                display.pending = nil
                if status == 'ok' and labels[message] then
                    display.mode, display.selected, display.error = message, message, nil
                else
                    display.selected = display.mode
                    display.error = message or '显示模式切换失败'; api.log('display', display.error)
                end
            end
        end
        --失败保持上次已确认模式且不重复请求状态变更
        if display.pending and os.time() - display.requested_at > 15 then
            display.pending, display.error = nil, '窗口辅助服务未及时确认显示切换'
            api.log('display', display.error)
        end
    end
end

--<summary>
--读取由原版设置助手独立管理的实际设置行
--</summary>
function display.state()
    local screen = api.mcp and api.mcp.screen(false, 'Settings')
    local panel = screen and screen:FindElementByName('Settings')
    local result = {available = panel ~= nil, visible = panel and panel:IsVisible() or false, elements = {},
        host = display.host, mode = display.mode, error = display.error or ''}
    if not result.visible then return result end
    --原版选择器没有挂进普通界面树从已核验设置行的箭头命中取得它的根节点
    local slot = display.open and display.slot or setting_slot(screen)
    if not slot or slot:GetName() ~= 'SETTING_SLOT' then return result end
    --只遍历这一行不读取内存地址或执行外部源码
    --<summary>
    --返回设置行的有界子树以验证实际控件归属
    --</summary>
    local function visit(node, path, depth)
        if depth > 8 or #result.elements >= 50 then return end
        local current = path .. '/' .. (node:GetName() or '')
        table.insert(result.elements, {path = current, name = node:GetName(), kind = tolua.type(node),
            id = node:GetId(), visible = node:IsVisible(), enabled = node:IsEnabled(),
            font = node:IsUITextBase() and node:GetFontHeight() or nil})
        local child = node:GetFirstChild()
        while child do visit(child, current, depth + 1); child = child:GetNextSibling() end
    end
    visit(slot, '', 0)
    result.selected, result.pending = display.selected, display.pending or false
    result.installed = display.apply ~= nil
    return result
end

--普通帧和暂停帧共享设置逻辑异常会记录且不会中断游戏原有回调
for _, name in ipairs({'OnTick', 'OnPauseTick'}) do
    local previous = LuaGameDelegate[name]
    LuaGameDelegate[name] = function(delegate, ...)
        local ok, message = pcall(display.tick)
        if not ok then display.error = tostring(message); api.log('display', display.error) end
        return previous(delegate, ...)
    end
end

--<summary>
--同步辅助进程复核后的模式
--</summary>
function display.ready(mode)
    if mode ~= 'windowed' and mode ~= 'fullscreen' and mode ~= 'borderless' then error('显示模式无效') end
    display.host, display.mode = true, mode
    --MCP在设置打开期间切换时也同步已确认文字避免旧选择看似仍未应用
    if display.pending then display.selected = mode end
    return {mode = display.mode, host = display.host}
end

--辅助进程根据固定启动脚本的位置定位游戏且启动时不显示系统窗口
local launcher = io.open('MaxyModLoader/display-host.vbs', 'rb')
if launcher and os.getenv('OS') == 'Windows_NT' then
    launcher:close()
    os.execute('wscript.exe //B //Nologo "MaxyModLoader\\display-host.vbs"')
elseif launcher then launcher:close() end
