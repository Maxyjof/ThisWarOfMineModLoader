local api = MaxyModLoader
--离线Lua测试和未知平台没有原生UI时不安装界面
if not LuaGameDelegate or not UIButton or not UITextBox or not Vector then return end
local manager = {open = false, selection = 1, buttons = {}}
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
    --复制的配方可能带有原菜单淡入和禁用颜色动画自有控件不沿用这些动作
    element:RemoveAllActions()
    element:SetName(name)
    element:SetWindowAlignment(UIWINDOWALIGNMENT_NONE)
    element:SetAspectScaling(UIWINDOWASPECTSCALING_NONE)
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
    --交互容器不绘制默认黑色矩形装饰由原版配方单独承担
    element = element or UIElement:new()
    element:SetName(name)
    --运行时新建按钮必须显式加入鼠标可聚焦集合否则只会绘制而不参与原生命中
    element:RaiseFlag(UIFLAG_FOCUSABLEWITHMOUSE)
    element:SetColor(1, 1, 1, 0)
    element:SetColorMode(UICOLOR_STATIC)
    element:SetSize(vector(width, 44))
    element:SetPosition(vector(x, y))
    if not attached then parent:AddChild(element) end
    text(element, name .. '_TEXT', value, 8, 5, width - 16, 36, 20)
    manager.buttons[name] = {element = element, handler = handler}
    return element
end

--<summary>
--从本机原版配方创建独立装饰控件并保持官方资源在游戏安装内
--</summary>
local function decoration(parent, source, recipe, name, x, y, width, height)
    if not source then error('原版装饰配方尚未就绪：' .. recipe) end
    local element = source:CreateElementFromSubRecipe(recipe)
    if not element then error('原版装饰配方不存在：' .. recipe) end
    --装饰不参与鼠标命中避免遮挡前景列表和返回按钮
    element:SetName(name)
    element:ClearFlag(UIFLAG_FOCUSABLEWITHMOUSE)
    element:SetWindowAlignment(UIWINDOWALIGNMENT_NONE)
    element:SetAnchor(vector(0, 0))
    element:SetScale(Vector:Instance(1, 1, 1, 1))
    element:SetAspectScaling(UIWINDOWASPECTSCALING_NONE)
    element:SetRotation(0, 0, 0)
    element:RemoveAllActions()
    if element:IsUIPicture() then element:SetRenderGatheringChannel(0) end
    element:SetPosition(vector(x, y))
    element:SetSize(vector(width, height))
    element:SetColorMode(UICOLOR_STATIC)
    element:SetVisible(true)
    parent:AddChild(element)
    return element
end

--<summary>
--将列表标题按显示宽度截断完整名称仍保留在模组介绍中
--</summary>
local function caption(value, limit)
    local parts, width = {}, 0
    for _, item in ipairs(characters(value)) do
        local weight = item.code < 128 and 1 or 2
        if width + weight > limit then return table.concat(parts) .. '…' end
        table.insert(parts, item.text)
        width = width + weight
    end
    return table.concat(parts)
end

--<summary>
--整理所选模组的完整介绍而不是只显示模组ID
--</summary>
function manager.details(mod)
    local blocks = {}
    --元数据直接构造语义节点作者说明使用打包时生成的标准Markdown语法树
    local function block(kind, value, level)
        table.insert(blocks, {kind = kind, level = level, runs = {{text = value}}})
    end
    block('heading', '版本信息', 2)
    block('paragraph', '版本：' .. mod.version .. '  |  状态：' .. (statuses[mod.status] or mod.status))
    block('paragraph', '作者：' .. (mod.author ~= '' and mod.author or '未提供'))
    local document = mod.description_document or {{kind = 'paragraph', runs = {{text = mod.description or '作者尚未提供内容介绍'}}}}
    for index, node in ipairs(document) do
        local title = {}
        for _, run in ipairs(node.runs or {}) do table.insert(title, run.text) end
        --同名顶级标题已在面板顶部显示其他标题和所有嵌套结构完整保留
        if not (index == 1 and node.kind == 'heading' and node.level == 1 and table.concat(title) == mod.name) then
            table.insert(blocks, node)
        end
    end
    if #(mod.features or {}) > 0 then
        block('heading', '功能说明', 2)
        local list = {kind = 'list', children = {}}
        for _, feature in ipairs(mod.features) do
            table.insert(list.children, {kind = 'item', children = {{kind = 'paragraph', runs = {{text = feature}}}}})
        end
        table.insert(blocks, list)
    end
    block('rule', '')
    block('heading', '技术信息', 3)
    block('paragraph', '标识：' .. mod.id)
    block('paragraph', '兼容说明：' .. (mod.compatibility or '未提供'))
    --依赖排序显示使不同运行时的介绍顺序保持一致
    local dependencies = {}
    for id, version in pairs(mod.dependencies or {}) do table.insert(dependencies, id .. '，最低版本' .. version) end
    table.sort(dependencies)
    block('paragraph', '依赖：' .. (#dependencies > 0 and table.concat(dependencies, '，') or '无'))
    block('paragraph', '冲突：' .. (#(mod.conflicts or {}) > 0 and table.concat(mod.conflicts, '，') or '无'))
    block('paragraph', '主页：' .. (mod.website ~= '' and mod.website or '未提供'))
    block('paragraph', '许可：' .. (mod.license ~= '' and mod.license or '未指定'))
    if mod.error and mod.error ~= '' then block('heading', '错误详情', 3); block('paragraph', mod.error) end
    return api.markdown.layout(blocks, 595, characters)
end

--<summary>
--将缺少语法树的兼容文本作为普通段落显示
--</summary>
function manager.markdown(value)
    --正式描述全部由打包阶段的标准解析器生成不再运行简化的正则替代解析器
    return api.markdown.layout({{kind = 'paragraph', runs = {{text = tostring(value or '')}}}}, 595, characters)
end

--<summary>
--刷新完整模组列表和滚动介绍并保持当前选择
--</summary>
function manager.refresh()
    for index = 1, #api.mods do
        local mod = api.mods[index]
        local row = manager.rows[index]
        row:SetVisible(mod ~= nil)
        if mod then
            row:FindElementByName('MML_ROW_' .. index .. '_TEXT'):SetText(unicode(caption(mod.name, 30)))
            --选择颜色由刷新本身维护鼠标停在游戏窗口外时也保持所选条目可辨认
            row:FindElementByName('MML_ROW_' .. index .. '_TEXT'):SetColor(1, index == manager.selection and 0.55 or 1,
                index == manager.selection and 0.16 or 1, 1)
            row:FindElementByName('MML_ROW_' .. index .. '_STATUS'):SetText(unicode(statuses[mod.status] or mod.status))
        end
    end
    local mod = api.mods[manager.selection]
    local lines = mod and manager.details(mod) or manager.markdown('当前没有发现模组')
    local visible = {}
    for _, row in ipairs(lines) do
        local fragments = {}
        for _, run in ipairs(row.runs) do table.insert(fragments, run.text) end
        table.insert(visible, table.concat(fragments))
    end
    manager.visible_text = table.concat(visible, '\n')
    --介绍按行放入原生滚动区域旧控件删除后立即丢弃引用避免访问释放对象
    manager.detail_pane:DeleteChildren()
    manager.detail_anchor = nil
    manager.detail_lines = {}
    manager.detail_geometry = {}
    manager.detail_height = 0
    --细线和低对比底色只绘制Markdown内部装饰保留外层炭笔贴图
    local function shape(parent, name, x, y, width, height, color)
        local element = UIElement:new()
        element:SetName(name)
        element:SetWindowAlignment(UIWINDOWALIGNMENT_NONE)
        element:SetAnchor(vector(0, 0))
        element:SetPosition(vector(x, y))
        element:SetSize(vector(math.max(1, width), height))
        element:SetColorMode(UICOLOR_STATIC)
        element:SetColor(unpack(color))
        element:ClearFlag(UIFLAG_FOCUSABLEWITHMOUSE)
        parent:AddChild(element)
    end
    for index, row in ipairs(lines) do
        --实际高度与绝对原点用于裁剪和滚动标题表格及代码块不共用固定行高
        local container = UIElement:new()
        container:SetName('MML_DETAIL_LINE_' .. index)
        container:SetWindowAlignment(UIWINDOWALIGNMENT_NONE)
        container:SetAnchor(vector(0, 0))
        container:SetSize(vector(625, row.height))
        container:SetColorMode(UICOLOR_STATIC)
        container:SetColor(1, 1, 1, 0)
        manager.detail_pane:AddChild(container)
        manager.detail_lines[index] = container
        manager.detail_geometry[index] = {top = row.top, height = row.height}
        manager.detail_height = math.max(manager.detail_height, row.top + row.height)
        local left, width = row.left or 0, row.width or 595
        if row.decoration == 'code' then shape(container, 'MML_CODE_BG_' .. index, left - 7, 0, width + 14, row.height, {1, 1, 1, 0.08})
        elseif row.decoration == 'quote' then shape(container, 'MML_QUOTE_' .. index, left - 13, 0, 2, row.height, {0.7, 0.62, 0.48, 0.8})
        elseif row.decoration == 'rule' then shape(container, 'MML_RULE_' .. index, left, 5, width, 1, {1, 1, 1, 0.3})
        elseif row.decoration == 'table' then
            shape(container, 'MML_TABLE_BOTTOM_' .. index, left, row.height - 1, width, 1, {1, 1, 1, 0.32})
            for column = 1, row.columns - 1 do
                shape(container, 'MML_TABLE_COLUMN_' .. index .. '_' .. column, left + width * column / row.columns, 0, 1, row.height, {1, 1, 1, 0.18})
            end
        end
        for span, run in ipairs(row.runs) do
            local size, color, x, y = run.size or 18, {1, 1, 1, 1}, run.x or 0, run.y or 0
            if row.decoration == 'heading' then color = {1, 0.45, 0, 1}
            elseif run.code then color = {0.85, 0.72, 0.54, 1}
            elseif run.link and run.link ~= '' then color = {0.72, 0.82, 0.86, 1}
            elseif row.decoration == 'quote' then color = {0.83, 0.83, 0.83, 1} end
            --粗体增加原生字形笔画而非仅改颜色强调与链接下划线和删除线独立组合
            if run.strong then
                local bold = text(container, 'MML_DETAIL_BOLD_' .. index .. '_' .. span, run.text, x + 0.6, y, 625 - x, size * 1.5, size)
                bold:SetColor(unpack(color))
            end
            local rendered = text(container, 'MML_DETAIL_SPAN_' .. index .. '_' .. span, run.text, x, y, 625 - x, size * 1.5, size)
            rendered:SetColor(unpack(color))
            if run.strike then shape(container, 'MML_STRIKE_' .. index .. '_' .. span, x, y + size * 0.62, run.width, 1, color) end
            if run.emphasis or run.link and run.link ~= '' then
                shape(container, 'MML_UNDERLINE_' .. index .. '_' .. span, x, y + size * 1.25, run.width, 1, color)
            end
        end
    end
    --介绍更换后从顶部显示列表仍保留自己的滚动位置
    manager.offsets = manager.offsets or {}
    manager.move_scroll('detail', 0)
    manager.frame:FindElementByName('MML_MOD_TITLE'):SetText(unicode(mod and caption(mod.name, 46) or '暂无模组'))
    manager.footer:SetText(unicode('共' .. #api.mods .. '个模组  鼠标滚轮滚动列表和介绍  增删或启用变更需要重新构建并重启游戏'))
end

--<summary>
--从原版列表配方创建具有原生裁剪和命中的独立滚动窗口
--</summary>
local function scroll_pane(parent, name, x, y, width, height)
    local pane = manager.scroll_template:CreateElementFromSubRecipe('SCROLL')
    if not pane or tolua.type(pane) ~= 'UIScrollPane' then error('原版滚动窗口配方不可用') end
    --只删除新副本的占位内容保留引擎滚动实现和原版裁剪参数
    pane:DeleteChildren()
    pane:RemoveAllActions()
    pane:SetName(name)
    pane:SetWindowAlignment(UIWINDOWALIGNMENT_NONE)
    pane:SetAspectScaling(UIWINDOWASPECTSCALING_NONE)
    pane:SetAnchor(vector(0, 0))
    pane:SetScale(Vector:Instance(1, 1, 1, 1))
    pane:SetRotation(0, 0, 0)
    pane:SetPosition(vector(x, y))
    pane:SetSize(vector(width, height))
    pane:SetColorMode(UICOLOR_STATIC)
    pane:SetColor(1, 1, 1, 1)
    pane:RaiseFlag(UIFLAG_FOCUSABLEWITHMOUSE)
    parent:AddChild(pane)
    return pane
end

--<summary>
--移动自有内容并限制到真实内容边界原生窗口仅负责裁剪和命中
--</summary>
function manager.move_scroll(region, offset)
    local elements = region == 'list' and manager.rows or region == 'detail' and manager.detail_lines
    if not elements then error('滚动区域无效') end
    local height, stride = region == 'list' and 410 or 350, region == 'list' and 57 or 25
    local inset, last_height = region == 'list' and 5 or 0, region == 'list' and 56 or 26
    local content_height = region == 'detail' and manager.detail_height or inset + math.max(0, #elements - 1) * stride + last_height
    local maximum = math.max(0, content_height - height)
    offset = math.max(0, math.min(offset, maximum))
    --避免原生聚焦接口居中和继承旧偏移所有内容使用同一绝对偏移
    for index, element in ipairs(elements) do
        local geometry = region == 'detail' and manager.detail_geometry and manager.detail_geometry[index]
        element:SetPosition(vector(0, (geometry and geometry.top or inset + (index - 1) * stride) - offset))
    end
    manager.offsets = manager.offsets or {}
    manager.offsets[region] = offset
    manager.update_scrollbar(region, maximum, height)
    return {region = region, count = #elements, offset = offset, maximum = maximum, source = '游戏内滚动窗口'}
end

--<summary>
--创建细线滑动条使可滚动内容和当前位置始终可辨认
--</summary>
function manager.create_scrollbar(region, x, y, height)
    local track = UIElement:new()
    track:SetName('MML_' .. region:upper() .. '_SCROLL_TRACK')
    track:SetWindowAlignment(UIWINDOWALIGNMENT_NONE)
    track:SetPosition(vector(x, y))
    track:SetSize(vector(14, height))
    track:SetColorMode(UICOLOR_STATIC)
    track:SetColor(1, 1, 1, 0)
    track:RaiseFlag(UIFLAG_FOCUSABLEWITHMOUSE)
    manager.frame:AddChild(track)
    --较宽透明命中区域便于拖动细线视觉保持原版炭笔界面的简洁
    local rail = UIElement:new()
    rail:SetName('MML_' .. region:upper() .. '_SCROLL_RAIL')
    rail:SetWindowAlignment(UIWINDOWALIGNMENT_NONE)
    rail:SetPosition(vector(6, 0))
    rail:SetSize(vector(2, height))
    rail:SetColorMode(UICOLOR_STATIC)
    rail:SetColor(1, 1, 1, 0.25)
    rail:ClearFlag(UIFLAG_FOCUSABLEWITHMOUSE)
    track:AddChild(rail)
    local thumb = UIElement:new()
    thumb:SetName('MML_' .. region:upper() .. '_SCROLL_THUMB')
    thumb:SetWindowAlignment(UIWINDOWALIGNMENT_NONE)
    thumb:SetColorMode(UICOLOR_STATIC)
    thumb:SetColor(0.85, 0.85, 0.85, 1)
    thumb:ClearFlag(UIFLAG_FOCUSABLEWITHMOUSE)
    track:AddChild(thumb)
    manager.scrollbars = manager.scrollbars or {}
    manager.scrollbars[region] = {element = track, thumb = thumb, y = y, height = height}
end

--<summary>
--根据内容比例和独立偏移更新滑块长度位置及可见性
--</summary>
function manager.update_scrollbar(region, maximum, height)
    local bar = (manager.scrollbars or {})[region]
    if not bar then return end
    bar.maximum = maximum
    bar.length = math.max(28, height * height / (height + maximum))
    bar.travel = height - bar.length
    bar.top = maximum > 0 and bar.travel * manager.offsets[region] / maximum or 0
    bar.element:SetVisible(maximum > 0)
    bar.thumb:SetSize(vector(4, bar.length))
    bar.thumb:SetPosition(vector(5, bar.top))
end

--<summary>
--处理滑块拖动和轨道定位使用界面逻辑坐标保持缩放后的命中一致
--</summary>
function manager.scroll_pointer(down, up, element, cursor)
    if not manager.open then manager.drag = nil; return false end
    local scale = math.min(1, (720 * gGame:GetScreenAspect() - 48) / 1100)
    local y = (cursor.y * 720 - 35) / scale
    if down then
        local current = element
        for depth = 1, 24 do
            if not current then break end
            for region, bar in pairs(manager.scrollbars or {}) do
                if current == bar.element and bar.maximum and bar.maximum > 0 then
                    local local_y = y - bar.y
                    local inside = local_y >= bar.top and local_y <= bar.top + bar.length
                    manager.drag = {region = region, grab = inside and local_y - bar.top or bar.length / 2}
                    manager.pressed = nil
                    break
                end
            end
            if manager.drag then break end
            current = current:GetParent()
        end
    end
    if not manager.drag then return false end
    local drag = manager.drag
    local bar = manager.scrollbars[drag.region]
    --即使鼠标移出轨道仍保持拖动并钳制到首尾松开或失焦立即结束
    manager.move_scroll(drag.region, (y - bar.y - drag.grab) / math.max(1, bar.travel) * bar.maximum)
    if up then manager.drag = nil end
    return true
end

--<summary>
--将自有滚动窗口滚到指定条目供MCP核验真实裁剪效果
--</summary>
function manager.scroll(argument)
    manager.attach()
    if not manager.open then error('模组管理界面尚未打开') end
    local region, encoded = (argument or ''):match('^(list)|(%d+)$')
    if not region then region, encoded = (argument or ''):match('^(detail)|(%d+)$') end
    local index = tonumber(encoded)
    local elements = region == 'list' and manager.rows or region == 'detail' and manager.detail_lines
    if not elements or not index or index < 1 or index > #elements then error('滚动目标无效') end
    local height, stride = region == 'list' and 410 or 350, region == 'list' and 57 or 25
    local geometry = region == 'detail' and manager.detail_geometry and manager.detail_geometry[index]
    local center = geometry and geometry.top + geometry.height / 2 or (index - 1) * stride + stride / 2
    local result = manager.move_scroll(region, center - height / 2)
    result.index = index
    return result
end

--<summary>
--仅将真实滚轮输入应用到鼠标所在窗口两个窗口各自保存偏移
--</summary>
function manager.wheel(value, element)
    if not manager.open or value == 0 or value ~= value or math.abs(value) == math.huge then return false end
    local region
    --文字和行按钮命中时沿父链查找避免只能在窗口空白处滚动
    for depth = 1, 24 do
        if not element then break end
        if element == manager.list_pane then region = 'list'; break end
        if element == manager.detail_pane then region = 'detail'; break end
        for name, bar in pairs(manager.scrollbars or {}) do
            if element == bar.element then region = name; break end
        end
        if region then break end
        element = element:GetParent()
    end
    if not region then return false end
    --兼容归一化滚轮和Windows刻度单位一次输入最多移动三格
    local steps = math.abs(value) >= 120 and value / 120 or value
    steps = math.max(-3, math.min(steps, 3))
    local result = manager.move_scroll(region, ((manager.offsets or {})[region] or 0) - steps * 48)
    --滚动时取消按下记录防止文字移位后释放鼠标误激活另一行
    manager.pressed, manager.drag = nil, nil
    manager.last_scroll = result
    return true
end

--<summary>
--切换独立管理面板并恢复原主菜单
--</summary>
function manager.show(visible)
    manager.open = visible
    manager.drag, manager.pressed = nil, nil
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
        manager.detail_pane, manager.list_pane, manager.scroll_template = nil, nil, nil
        manager.offsets, manager.last_scroll, manager.scrollbars, manager.drag = {}, nil, {}, nil
        manager.detail_lines, manager.footer, manager.visible_text = {}, nil, ''
        manager.detail_geometry, manager.detail_height = nil, nil
        manager.skin_applied, manager.skin_error = nil, nil
        manager.palette = nil
        manager.layout_width = nil
        manager.screen, manager.open, manager.pressed = screen, false, nil
    end
    if not menu then return end
    if manager.frame then return end
    --游戏界面以720逻辑高度缩放横坐标根据当前宽高比计算
    manager.menu, manager.screen, manager.buttons = menu, screen, {}
    manager.template = menu:FindElementByName('BUTTON_STARTNEW')
    manager.scroll_template = screen:FindElementByName('WorkshopScenarioSelect')
    if not manager.template or not manager.scroll_template then return end
    --完整复用原主菜单按钮保留原字体箭头尺寸与状态动画只更改名称和显示文字
    local entry = menu:CreateElementFromSubRecipe('BUTTON_STARTNEW')
    if not entry then error('原版主菜单按钮配方不可用') end
    entry:SetName('BUTTON_MAXY_MODS')
    entry:SetWindowAlignment(UIWINDOWALIGNMENT_NONE)
    entry:SetAnchor(vector(0, 0))
    entry:SetPosition(vector(720 * gGame:GetScreenAspect() * 0.79 - 330, 466))
    entry:RaiseFlag(UIFLAG_FOCUSABLEWITHMOUSE)
    entry:FindElementByName('BUTTON_NAME'):SetText(unicode('模组管理'))
    menu:AddChild(entry)
    manager.buttons.BUTTON_MAXY_MODS = {element = entry, handler = function() manager.show(true) end}
    manager.frame = screen:FindElementByName('MML_MANAGER')
    local attached = manager.frame ~= nil
    manager.frame = manager.frame or UIElement:new()
    manager.frame:SetName('MML_MANAGER')
    manager.frame:SetSize(vector(1100, 650))
    manager.frame:SetWindowAlignment(UIWINDOWALIGNMENT_NONE)
    manager.frame:SetPosition(vector((720 * gGame:GetScreenAspect() - 1100) / 2, 35))
    manager.frame:SetColor(1, 1, 1, 0)
    manager.frame:SetColorMode(UICOLOR_STATIC)
    if not attached then screen:AddChild(manager.frame) end
    --原版界面配方延迟加载时暂时保留功能背景后续帧再应用材质
    local background = manager.frame:FindElementByName('MML_BACKGROUND')
    local background_attached = background ~= nil
    background = background or UIElement:new()
    background:SetName('MML_BACKGROUND')
    background:SetSize(vector(1100, 650))
    background:SetColor(0.035, 0.035, 0.035, 0.98)
    background:SetColorMode(UICOLOR_STATIC)
    if not background_attached then manager.frame:AddChild(background) end
    text(manager.frame, 'MML_TITLE', 'MaxyModLoader 模组管理', 30, 20, 1030, 50, 30)
    manager.list_pane = scroll_pane(manager.frame, 'MML_LIST_SCROLL', 30, 110, 345, 410)
    manager.detail_pane = scroll_pane(manager.frame, 'MML_DETAIL_SCROLL', 425, 168, 625, 350)
    manager.rows = {}
    for index = 1, #api.mods do
        local slot = index
        manager.rows[index] = button(manager.list_pane, 'MML_ROW_' .. index, '', 0, 5 + (index - 1) * 57, 345, function()
            manager.selection = slot
            manager.refresh()
        end)
        manager.rows[index]:SetSize(vector(345, 58))
        manager.rows[index]:GetFirstChild():SetSize(vector(329, 58))
        --名称和运行状态分开绘制用字号层级区分主要信息
        local label = manager.rows[index]:FindElementByName('MML_ROW_' .. index .. '_TEXT')
        label:SetFont('NotoSansCJKsc-Medium.otf', 18, true)
        label:SetSize(vector(329, 28))
        text(manager.rows[index], 'MML_ROW_' .. index .. '_STATUS', '', 8, 32, 329, 23, 14)
    end
    manager.offsets = {list = 0, detail = 0}
    manager.create_scrollbar('list', 333, 110, 410)
    manager.create_scrollbar('detail', 1040, 168, 350)
    manager.move_scroll('list', 0)
    text(manager.frame, 'MML_MOD_TITLE', '', 425, 122, 625, 42, 24)
    manager.footer = text(manager.frame, 'MML_FOOTER', '', 30, 600, 1040, 42, 16)
    button(manager.frame, 'MML_CLOSE', '返回主菜单', 850, 530, 215, function() manager.show(false) end)
    manager.frame:SetVisible(false)
    manager.open = false
    manager.skin_applied = false
    api.log('manager', '独立模组管理入口已创建')
end

--<summary>
--复用原版创意工坊的背景刷痕和炭笔板材替换功能验证版外观
--</summary>
function manager.skin()
    if manager.skin_applied or not manager.frame then return end
    local source = manager.screen:FindElementByName('WorkshopScenarioSelect')
    if not source then return end
    local top = source:FindElementByName('PANEL_TOP')
    if not top then return end
    --装饰集中放在最先绘制的背景容器中不会盖住已创建的按钮和文字
    local background = manager.frame:FindElementByName('MML_BACKGROUND')
    background:SetColor(1, 1, 1, 0)
    --只复制原版静态背景图片不复制带有后处理和发光层的背景容器
    local width = 720 * gGame:GetScreenAspect()
    --原版图片底部有透明留白放大到安全覆盖区域防止面板下方出现纯白断层
    decoration(background, source, 'bg', 'MML_NATIVE_FOREST', -(width - 1100) / 2, -35, width, 1050)
    decoration(background, top, 'white paint', 'MML_NATIVE_HEADER', 0, 0, 640, 85):SetColor(0.84, 0.82, 0.78, 0.82)
    decoration(background, top, 'black paint', 'MML_NATIVE_BRAND', 655, 0, 445, 75)
    --<summary>
    --用完整炭笔纹理绘制内容板不叠加纯黑底板或重复笔触
    --</summary>
    local function panel(name, texture, x, y, panel_width, panel_height)
        --新图片使用完整UV不继承原版图集裁剪范围素材随普通部署包加入
        local picture = UIPicture:new()
        picture:SetName(name)
        picture:ClearFlag(UIFLAG_FOCUSABLEWITHMOUSE)
        picture:SetWindowAlignment(UIWINDOWALIGNMENT_NONE)
        picture:SetAnchor(vector(0, 0))
        picture:SetAspectScaling(UIWINDOWASPECTSCALING_NONE)
        picture:SetRenderGatheringChannel(0)
        picture:SetTexture('UI/MaxyModLoader/' .. texture .. '.dds')
        picture:SetPosition(vector(x, y))
        picture:SetSize(vector(panel_width, panel_height))
        picture:SetColorMode(UICOLOR_STATIC)
        picture:SetColor(1, 1, 1, 1)
        background:AddChild(picture)
    end
    panel('MML_NATIVE_LIST', 'BrushList', -6, 78, 398, 474)
    panel('MML_NATIVE_DETAIL', 'BrushDetail', 371, 78, 728, 474)
    --沿用原版刷痕按钮背景但使用自有交互容器不复制原版创意工坊回调
    for index, bounds in ipairs({{845, 535, 225, 64}}) do
        decoration(background, top, 'black paint', 'MML_NATIVE_BUTTONS_' .. index, bounds[1], bounds[2], bounds[3], bounds[4]):SetColor(0, 0, 0, 1)
        local mirrored = decoration(background, top, 'black paint', 'MML_NATIVE_BUTTONS_MIRROR_' .. index,
            bounds[1] + bounds[3], bounds[2], bounds[3], bounds[4])
        mirrored:SetScale(Vector:Instance(-1, 1, 1, 1))
        mirrored:SetColor(0, 0, 0, 1)
    end
    local title = manager.frame:FindElementByName('MML_TITLE')
    title:SetText(unicode('模组管理'))
    title:SetColor(1, 0.45, 0, 1)
    title:SetSize(vector(480, 50))
    text(manager.frame, 'MML_BRAND', 'MaxyModLoader', 694, 23, 350, 40, 24):SetColor(1, 1, 1, 1)
    text(manager.frame, 'MML_MOD_TITLE', '', 425, 122, 625, 42, 24):SetColor(1, 0.45, 0, 1)
    manager.footer:SetColor(0.75, 0.75, 0.75, 1)
    --当前配方的静态文字颜色在游戏显示回调后恢复防止旧菜单动作残留
    manager.palette = {MML_TITLE = {1, 0.45, 0, 1},
        MML_BRAND = {1, 1, 1, 1}, MML_FOOTER = {0.15, 0.15, 0.15, 1},
        MML_NATIVE_HEADER = {0.84, 0.82, 0.78, 0.82},
        MML_NATIVE_FOREST = {1, 1, 1, 1}, MML_NATIVE_BRAND = {0, 0, 0, 1}, MML_MOD_TITLE = {1, 0.45, 0, 1}}
    for index = 1, #api.mods do manager.palette['MML_ROW_' .. index .. '_STATUS'] = {0.65, 0.65, 0.65, 1} end
    --面板内上下留白统一为原版对话框的节奏并让内容由滚动窗口裁剪
    for index, row in ipairs(manager.rows) do row:SetSize(vector(345, 56)) end
    --返回按钮拥有完整刷痕背景文字居中使边缘透明区留出空间
    for _, name in ipairs({'MML_CLOSE'}) do
        local label = manager.frame:FindElementByName(name .. '_TEXT')
        label:SetFont('NotoSansCJKsc-Medium.otf', 18, true)
        label:SetAlignment(TEXTALIGNMENT_CENTER)
        label:SetPosition(vector(8, 18))
        manager.buttons[name].element:SetSize(vector(name == 'MML_CLOSE' and 215 or name:match('^MML_LIST') and 170 or 185, 64))
    end
    manager.skin_applied = true
    manager.layout_width = nil
    api.log('manager', '原版界面配方样式已应用')
end

--<summary>
--按当前屏幕宽高比缩放管理界面避免较窄窗口裁掉两侧内容
--</summary>
function manager.layout()
    local width = 720 * gGame:GetScreenAspect()
    if manager.layout_width == width then return end
    local scale = math.min(1, (width - 48) / 1100)
    --统一变换整个面板保持绘制位置和原生命中区域使用同一套坐标
    manager.frame:SetScale(Vector:Instance(scale, scale, 1, 1))
    manager.frame:SetPosition(vector((width - 1100 * scale) / 2, 35))
    manager.buttons.BUTTON_MAXY_MODS.element:SetPosition(vector(width * 0.79 - 330, 466))
    local forest = manager.frame:FindElementByName('MML_NATIVE_FOREST')
    if forest then
        forest:SetPosition(vector(-(width - 1100 * scale) / (2 * scale), -35 / scale))
        forest:SetSize(vector(width / scale, 1050 / scale))
    end
    manager.layout_width = width
end

--<summary>
--调整加载器拥有的界面属性供MCP对照实际截图迭代布局
--</summary>
function manager.adjust(argument)
    manager.attach()
    local name, property, encoded = (argument or ''):match('^([A-Za-z0-9_]+)|([a-z]+)|(.+)$')
    if not name or not (name:match('^MML_') or name:match('^BUTTON_MAXY_MODS')) then error('只能调整加载器自有控件') end
    local element = manager.frame and manager.frame:FindElementByName(name)
    element = element or manager.menu and manager.menu:FindElementByName(name)
    if name == 'MML_MANAGER' then element = manager.frame end
    if not element then error('自有控件不存在') end
    --只接受固定属性和有限数字不会执行脚本或访问原生地址
    if encoded:match('^,') or encoded:match(',$') or encoded:match(',,') then error('属性值不能包含空分量') end
    local values = {}
    for value in encoded:gmatch('[^,]+') do
        local number = tonumber(value)
        if not number or number ~= number or number == math.huge or number == -math.huge then error('属性值必须为有限数字') end
        table.insert(values, number)
    end
    if property == 'color' then
        if #values ~= 4 then error('颜色需要四个分量') end
        for _, value in ipairs(values) do if value < 0 or value > 1 then error('颜色分量超出范围') end end
        element:SetColor(unpack(values))
        --临时颜色调整纳入当前会话配色避免下一帧被默认样式覆盖
        manager.palette = manager.palette or {}
        manager.palette[name] = values
    elseif property == 'position' or property == 'size' or property == 'scale' then
        if #values ~= 2 then error('向量需要两个分量') end
        for _, value in ipairs(values) do
            if math.abs(value) > 4096 or property ~= 'position' and value <= 0 then error('向量分量超出范围') end
        end
        if property == 'position' then element:SetPosition(vector(values[1], values[2]))
        elseif property == 'size' then element:SetSize(vector(values[1], values[2]))
        else element:SetScale(Vector:Instance(values[1], values[2], 1, 1)) end
    elseif property == 'channel' then
        if #values ~= 1 or values[1] ~= 0 or not element:IsUIPicture() then error('仅允许自有图片恢复默认绘制通道') end
        element:SetRenderGatheringChannel(0)
    else error('不支持此界面属性') end
    return {name = name, property = property, values = values, temporary = true}
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
            --设置扩展按钮只能在其所属页面可见且后台服务可用时触发
            if item.available and not item.available() then return nil end
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
        --第三参数为立即切换标记必须传false才能执行原配方的补间动作
        if name == 'BUTTON_MAXY_MODS' then item.element:SetHighlight(item == pointed, false) end
        local label = item.element:FindElementByName(name .. '_TEXT')
        if label then
            local row = name:match('^MML_ROW_(%d+)$')
            local selected = row and tonumber(row) == manager.selection
            if item == pointed or selected then label:SetColor(1, 0.55, 0.16, 1)
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
    --样式失败只停用样式安装不能阻断已经正常工作的鼠标交互
    if not manager.skin_error then
        local ok, message = pcall(manager.skin)
        if not ok then manager.skin_error = tostring(message); api.log('manager.skin', manager.skin_error) end
    end
    manager.layout()
    --每帧只恢复自有静态控件的配色不更改原版控件或鼠标悬停颜色
    if manager.open then
        for name, color in pairs(manager.palette or {}) do
            local element = manager.frame:FindElementByName(name)
            if element then element:SetColor(unpack(color)) end
        end
    end
    local cursor = gGame:GetCursorPosition()
    local element = manager.screen:GetElementAtScreenPosition(cursor)
    local down = gGame:IsMouseButtonPressedForTheFirstTime(65536)
    local up = gGame:IsMouseButtonReleasedForTheFirstTime(65536)
    local wheel = gGame:GetMouseWheel()
    --保存实际命中和最近输入边沿让MCP诊断显示真实鼠标是否经过管理逻辑
    manager.input = {active = gGame:IsActive(), on_window = gGame:IsCursorOnGameWindow(),
        x = cursor.x, y = cursor.y, pointed = element and element:GetName() or '', down = down, up = up, wheel = wheel}
    if wheel ~= 0 and manager.open then
        manager.last_wheel = manager.input
        api.log('manager.wheel', tostring(wheel) .. '/' .. manager.input.pointed)
    end
    if down or up then
        manager.last_input = manager.input
        api.log('manager.input', tostring(down) .. '/' .. tostring(up) .. '/' .. manager.input.pointed)
    end
    --鼠标左键编码由当前版本的公开绑定映射核验不能使用触摸接口冒充鼠标接口
    if not gGame:IsActive() or not gGame:IsCursorOnGameWindow() then
        manager.pressed, manager.drag = nil, nil
        return
    end
    if wheel ~= 0 then manager.wheel(wheel, element) end
    if not manager.scroll_pointer(down, up, element, cursor) then manager.pointer(down, up, element) end
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
        list_count = #api.mods, detail_lines = #(manager.detail_lines or {}), text = manager.visible_text or '',
        font_height = manager.detail_lines and manager.detail_lines[1] and manager.detail_lines[1]:GetFirstChild() and
            manager.detail_lines[1]:GetFirstChild():GetFontHeight() or 0,
        entry_font_height = manager.buttons.BUTTON_MAXY_MODS and manager.buttons.BUTTON_MAXY_MODS.element:FindElementByName('BUTTON_NAME'):GetFontHeight() or 0,
        original_font_height = manager.template and manager.template:FindElementByName('BUTTON_NAME'):GetFontHeight() or 0,
        clicks = manager.click_count or 0, input = manager.input or {}, last_input = manager.last_input or {},
        offsets = manager.offsets or {}, last_scroll = manager.last_scroll or {},
        last_wheel = manager.last_wheel or {}, scrolling = manager.list_pane ~= nil and manager.detail_pane ~= nil,
        skin = manager.skin_applied or false, skin_error = manager.skin_error or '', error = manager.failed or false}
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
