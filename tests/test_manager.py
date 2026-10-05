import pathlib

ROOT = pathlib.Path(__file__).resolve().parents[1]
from lupa.lua51 import LuaRuntime


def main():
    """
    <summary>
    验证界面释放后不访问旧原生对象以及Unicode代理项转换
    </summary>
    """
    #故意使旧对象的任何方法读取抛出异常模拟已释放原生界面的边界
    lua = LuaRuntime()
    lua.execute('''
MaxyModLoader = {mods = {}}
LuaGameDelegate = {OnTick = function() end, OnPauseTick = function() end}
UIButton, UITextBox, Vector = {}, {}, {}
LuaUnicodeString = {Instance = function()
    return {values = {}, AppendChar = function(self, value) table.insert(self.values, value) end}
end}
local fresh = {FindElementByName = function() return nil end}
gGame = {GetPreFSEUIScreen = function() return nil end}
gGameDelegate = {GetGameOverlayScreen = function() return fresh end}
dead = setmetatable({}, {__index = function() error('访问已释放的原生控件') end})
''')
    lua.execute((ROOT / 'MaxyModLoader.Core/Runtime/markdown.lua').read_text(encoding='utf-8'))
    lua.execute((ROOT / 'MaxyModLoader.Core/Runtime/manager.lua').read_text(encoding='utf-8'))
    lua.execute('''
local manager = MaxyModLoader.manager
--主菜单新场景先等待淡入帧界限不同界面重新开始计时
local first_screen, second_screen, menu = {}, {}, {}
assert(not manager.menu_fade_complete(first_screen, menu, 100))
assert(not manager.menu_fade_complete(first_screen, menu, 189))
assert(manager.menu_fade_complete(first_screen, menu, 190))
assert(not manager.menu_fade_complete(second_screen, menu, 191))
assert(not manager.menu_fade_complete(second_screen, menu, 280))
assert(manager.menu_fade_complete(second_screen, menu, 281))
manager.screen, manager.menu, manager.frame = dead, dead, dead
manager.detail, manager.footer = dead, dead
manager.buttons = {old = {element = dead, handler = function() error('旧按钮被调用') end}}
manager.tick()
assert(manager.frame == nil and manager.menu == nil)
assert(next(manager.buttons) == nil)
assert(not manager.state().open)
assert(not manager.activate('old'))
local value = manager.unicode('中文A😀').values
assert(#value == 5 and value[1] == 20013 and value[2] == 25991 and value[3] == 65)
assert(value[4] == 55357 and value[5] == 56832)
--标准语法树排版保留嵌套缩进表格列和不同的块级高度
local decode = function(value)
    local result = {}
    for index = 1, #value do table.insert(result, {text = value:sub(index, index), code = value:byte(index)}) end
    return result
end
local rows, height = MaxyModLoader.markdown.layout({
    {kind = 'heading', level = 2, runs = {{text = 'Heading'}}},
    {kind = 'paragraph', runs = {{text = 'Text with', strong = true}, {text = ' nested', strong = true, emphasis = true}}},
    {kind = 'list', ordered = true, start = 4, children = {
        {kind = 'item', children = {{kind = 'paragraph', runs = {{text = 'Outer'}}},
            {kind = 'list', children = {{kind = 'item', children = {{kind = 'paragraph', runs = {{text = 'Inner'}}}}}}}}}}},
    {kind = 'table', children = {
        {kind = 'table_row', header = true, children = {
            {kind = 'table_cell', children = {{kind = 'paragraph', runs = {{text = 'Key'}}}}},
            {kind = 'table_cell', alignment = 'right', children = {{kind = 'paragraph', runs = {{text = 'Value'}}}}}}},
        {kind = 'table_row', children = {
            {kind = 'table_cell', children = {{kind = 'paragraph', runs = {{text = 'Long value wraps'}}}}},
            {kind = 'table_cell', children = {{kind = 'paragraph', runs = {{text = '12'}}}}}}}}},
    {kind = 'code', language = 'lua', runs = {{text = 'a = 1' .. string.char(10) .. 'b = 2', code = true}}},
    {kind = 'paragraph', runs = {{text = 'After table'}}}
}, 170, decode)
local tables, nested, code, combined, ending = 0, false, 0, false, false
local previous = -1
for _, row in ipairs(rows) do
    assert(row.top > previous and row.height > 0)
    previous = row.top
    if row.decoration == 'table' then
        tables = tables + 1
        assert(row.columns == 2 and #row.runs > 0)
        if row.header then assert(row.runs[1].strong and row.runs[2].x > 85) end
    end
    if row.decoration == 'code' then code = code + 1 end
    for _, run in ipairs(row.runs) do
        if run.text == 'Inner' then nested = run.x >= 50 end
        if run.strong and run.emphasis then combined = true end
        if run.text == 'After table' then ending = true end
    end
end
assert(tables == 2 and nested and code == 3 and combined and ending)
assert(height == rows[#rows].top + rows[#rows].height)
local detail = manager.details({id = 'twom.example', name = 'Example', version = '1.0.0', status = 'loaded', author = 'Maxy',
    description_document = {{kind = 'heading', level = 1, runs = {{text = 'Example'}}},
        {kind = 'paragraph', runs = {{text = 'Description paragraph'}}}}, features = {},
    dependencies = {}, conflicts = {}, website = '', license = '', compatibility = ''})
local found = false
for _, row in ipairs(detail) do for _, run in ipairs(row.runs) do
    assert(run.text ~= 'Example')
    if run.text == 'Description paragraph' then found = true end
end end
assert(found)

--验证界面暂存状态阻止依赖断裂和冲突组合
MaxyModLoader.mods = {
    {id = 'base.mod', name = '基础模组', enabled = true, status = 'loaded'},
    {id = 'feature.mod', name = '功能模组', enabled = true, status = 'loaded', dependencies = {['base.mod'] = '1.0.0'}},
    {id = 'other.mod', name = '冲突模组', enabled = false, status = 'disabled', conflicts = {'feature.mod'}}
}
MaxyModLoader.mod_by_id = {}
for _, mod in ipairs(MaxyModLoader.mods) do MaxyModLoader.mod_by_id[mod.id] = mod end
manager.pending_states = {}
assert(not manager.has_changes())
assert(manager.desired_enabled(MaxyModLoader.mods[1]))
manager.pending_states['base.mod'] = false
assert(manager.has_changes())
local valid, reason = manager.validate_states({['base.mod'] = false, ['feature.mod'] = true, ['other.mod'] = false})
assert(not valid and reason:find('依赖'))
valid, reason = manager.validate_states({['base.mod'] = true, ['feature.mod'] = true, ['other.mod'] = true})
assert(not valid and reason:find('冲突'))
valid = manager.validate_states({['base.mod'] = true, ['feature.mod'] = false, ['other.mod'] = true})
assert(valid)
assert(manager.serialize_states({['base.mod'] = true, ['feature.mod'] = false, ['other.mod'] = true}) ==
    'MMLS1\\nbase.mod\\t1\\nfeature.mod\\t0\\nother.mod\\t1\\n')

--创建可见层级验证点击文字子控件也能激活其按钮
local function element(name, parent)
    return {GetName = function() return name end, GetParent = function() return parent end,
        IsVisible = function() return true end, IsEnabled = function() return true end,
        FindElementByName = function() return nil end,
        IsDescendantOf = function(self, ancestor)
            local node = parent
            while node do if node == ancestor then return true end; node = node:GetParent() end
            return false
        end}
end
manager.menu, manager.frame = element('menu'), element('frame')
local button = element('test', manager.menu)
local label = element('label', button)
local other = element('other', manager.menu)
local calls = 0
manager.buttons = {test = {element = button, handler = function() calls = calls + 1 end}}
assert(manager.pointed_button(label) == manager.buttons.test)
assert(not manager.pointer(false, true, label) and calls == 0)
manager.pointer(true, false, label)
assert(not manager.pointer(false, true, other) and calls == 0)
manager.pointer(true, false, label)
assert(manager.pointer(false, true, label) and calls == 1)
assert(not manager.pointer(false, true, label) and calls == 1)
--被选中的模组行保持橙色提示且未选中行恢复白色
manager.open = true
local list_label = element('MML_ROW_2_TEXT', manager.frame)
local list_button = element('MML_ROW_2', manager.frame)
local label_color
list_button.FindElementByName = function(self, name)
    if name == 'MML_ROW_2_TEXT' then return {SetColor = function(_, r, g, b, a) label_color = {r, g, b, a} end} end
end
manager.buttons = {MML_ROW_2 = {element = list_button, handler = function() end}}
manager.selection = 2
manager.pointer(false, false, list_button)
assert(label_color[1] == 1 and label_color[2] == 0.55 and label_color[3] == 0.16)
--鼠标离开后仍保留选择高亮取消选择则恢复白色
manager.pointer(false, false, nil)
assert(label_color[2] == 0.55)
manager.selection = 1
manager.pointer(false, false, nil)
assert(label_color[2] == 1 and label_color[3] == 1)
manager.open = false
manager.buttons = {test = {element = button, handler = function() calls = calls + 1 end}}
manager.open = true
assert(manager.pointed_button(label) == nil)

--真实帧必须调用界面原生命中和左键编码不能把按钮传给触摸查询
manager.open = false
manager.attach = function() end
local native_layout = manager.layout
manager.layout = function() end
local native_down, native_up = true, false
gGame.IsActive = function() return true end
gGame.IsCursorOnGameWindow = function() return true end
local cursor = {x = 0.5, y = 0.5}
gGame.GetCursorPosition = function() return cursor end
gGame.GetMouseWheel = function() return 0 end
gGame.GetNewTapForUIElement = function() error('不应调用触摸查询') end
gGame.IsMouseButtonPressedForTheFirstTime = function(self, code) assert(code == 65536); return native_down end
gGame.IsMouseButtonReleasedForTheFirstTime = function(self, code) assert(code == 65536); return native_up end
manager.screen = {GetElementAtScreenPosition = function(self, point) assert(point == cursor); return label end,
    FindElementByName = function() return nil end}
MaxyModLoader.log = function() end
manager.tick()
native_down, native_up = false, true
manager.tick()
assert(calls == 2)
manager.tick()
assert(calls == 2)

--失去焦点后的释放不能沿用先前的按钮按下
native_down, native_up = true, false
manager.tick()
gGame.IsActive = function() return false end
manager.tick()
gGame.IsActive = function() return true end
native_down, native_up = false, true
manager.tick()
assert(calls == 2)

--临时界面调整拒绝越权控件空分量超限和非有限数字且错误不会触及原生设置函数
local mutations = 0
local owned = {SetColor = function() mutations = mutations + 1 end,
    IsUIPicture = function() return false end}
manager.frame.FindElementByName = function(self, name) if name == 'MML_DETAIL' then return owned end end
for _, value in ipairs({'BUTTON_STARTNEW|color|1,1,1,1', 'MML_MISSING|color|1,1,1,1',
    'MML_DETAIL|color|1,,1,1,1', 'MML_DETAIL|color|1,1,1,1,', 'MML_DETAIL|color|1,1,1',
    'MML_DETAIL|color|1,1,1,2', 'MML_DETAIL|color|nan,1,1,1', 'MML_DETAIL|size|0,1',
    'MML_DETAIL|position|5000,1', 'MML_DETAIL|scale|1,1e999', 'MML_DETAIL|channel|0',
    'MML_DETAIL|script|1'}) do
    assert(not pcall(manager.adjust, value), value)
end
assert(mutations == 0)
assert(manager.adjust('MML_DETAIL|color|1,0.5,0,1').temporary and mutations == 1)
assert(manager.palette.MML_DETAIL[2] == 0.5)

--不同宽高比下前景面板必须位于视口内且始终留出边距
Vector.Instance = function(self, x, y) return {x = x, y = y} end
local frame_scale, frame_position
manager.frame.SetScale = function(self, value) frame_scale = value end
manager.frame.SetPosition = function(self, value) frame_position = value end
manager.buttons.BUTTON_MAXY_MODS = {element = {SetPosition = function() end}}
for _, aspect in ipairs({4 / 3, 16 / 10, 16 / 9, 21 / 9}) do
    gGame.GetScreenAspect = function() return aspect end
    manager.layout_width = nil
    native_layout()
    assert(frame_position.x >= 23.99)
    assert(frame_position.x + 1100 * frame_scale.x <= 720 * aspect - 23.99)
    assert(frame_position.y >= 0 and frame_position.y + 650 * frame_scale.y <= 720)
end

--程序化滚动必须限制在自有区域并将首尾条目定位到有效内容边界
manager.open = true
manager.rows, manager.detail_lines = {}, {}
local list_positions, detail_positions = {}, {}
for index = 1, 10 do
    local slot = index
    manager.rows[index] = {SetPosition = function(self, value) list_positions[slot] = value.y end}
end
for index = 1, 30 do
    local slot = index
    manager.detail_lines[index] = {SetPosition = function(self, value) detail_positions[slot] = value.y end}
end
manager.list_pane, manager.detail_pane = {}, {}
assert(manager.scroll('list|1').offset == 0 and list_positions[1] == 5)
assert(manager.scroll('list|10').offset == 164 and list_positions[10] + 56 == 410)
assert(manager.scroll('detail|1').offset == 0 and detail_positions[1] == 0)
assert(manager.scroll('detail|30').offset == 401 and detail_positions[30] + 26 == 350)
for _, value in ipairs({'other|1', 'list|0', 'list|11', 'detail|31', 'list|1.5', 'list|1e9', 'list|1|code'}) do
    assert(not pcall(manager.scroll, value))
end

--滚轮命中文字也必须定位到所属窗口并保持另一个窗口的位置
local list_label = {GetParent = function() return manager.list_pane end}
local detail_label = {GetParent = function() return manager.detail_pane end}
manager.move_scroll('list', 0)
manager.move_scroll('detail', 0)
manager.pressed = {}
assert(manager.wheel(-1, list_label) and manager.offsets.list == 48)
assert(manager.offsets.detail == 0 and manager.pressed == nil)
assert(manager.wheel(-120, detail_label) and manager.offsets.detail == 48)
assert(manager.offsets.list == 48)
assert(manager.wheel(120, detail_label) and manager.offsets.detail == 0)
assert(manager.wheel(120, detail_label) and manager.offsets.detail == 0)
assert(manager.wheel(-9999, list_label) and manager.offsets.list == 164)
assert(not manager.wheel(-1, {GetParent = function() return nil end}))
assert(not manager.wheel(0 / 0, detail_label) and not manager.wheel(math.huge, detail_label))
--滑块比例和拖动采用同一偏移模型短内容隐藏滑块
local thumb_position, thumb_size, bar_visible
local track = {SetVisible = function(self, value) bar_visible = value end, GetParent = function() return nil end}
local thumb = {SetPosition = function(self, value) thumb_position = value end,
    SetSize = function(self, value) thumb_size = value end}
manager.scrollbars = {list = {element = track, thumb = thumb, y = 110, height = 410}}
manager.move_scroll('list', 0)
local bar = manager.scrollbars.list
assert(bar_visible and thumb_position.y == 0 and thumb_size.y < 410)
manager.move_scroll('list', 164)
assert(math.abs(thumb_position.y + thumb_size.y - 410) < 0.001)
gGame.GetScreenAspect = function() return 16 / 9 end
manager.move_scroll('list', 0)
assert(manager.scroll_pointer(true, false, track, {y = (35 + 110 + 5) / 720}))
assert(manager.scroll_pointer(false, false, nil, {y = 2}))
assert(manager.offsets.list == 164 and manager.offsets.detail == 0)
assert(manager.scroll_pointer(false, true, nil, {y = 2}) and manager.drag == nil)
assert(not manager.scroll_pointer(false, false, nil, {y = 0.5}))
manager.update_scrollbar('list', 0, 410)
assert(not bar_visible)

--不同块高度的介绍首尾边界使用绝对几何而非行数乘固定步长
manager.detail_geometry, manager.detail_height = {}, 900
for index = 1, 30 do manager.detail_geometry[index] = {top = (index - 1) * 30, height = 30} end
assert(manager.scroll('detail|30').maximum == 550)
assert(detail_positions[30] + 30 == 350)
assert(manager.move_scroll('detail', -5).offset == 0 and detail_positions[1] == 0)
manager.detail_geometry, manager.detail_height = nil, nil

--原版入口必须允许原配方动作执行不能采用默认的立即切换模式
manager.open = false
local highlight_calls = 0
local entry = {GetName = function() return 'BUTTON_MAXY_MODS' end,
    IsVisible = function() return true end, IsEnabled = function() return true end,
    IsDescendantOf = function() return false end, FindElementByName = function() return nil end,
    SetHighlight = function(self, value, immediate) assert(immediate == false); highlight_calls = highlight_calls + 1 end}
manager.buttons = {BUTTON_MAXY_MODS = {element = entry, handler = function() end}}
manager.pointer(false, false, entry)
assert(highlight_calls == 1)
manager.open = false
assert(not manager.wheel(-1, list_label))
assert(not pcall(manager.scroll, 'list|1'))
''')
    print('通过：界面引用清理、UTF16字符、原生鼠标处理、独立滚轮边界与自有控件调整边界')


    #原生物品查询只读白名单参数缺失返回未注册不虚构默认物品
    lua.execute('''
gGameDelegate = {}
MaxyModLoader.log = function() end
local calls = 0
gKosovoItemConfig = {GetEntryWithName = function(self, name)
    calls = calls + 1
    if name == 'MML_Test' then return {Value = 90, StackSize = 1, BulletsPerShot = 1} end
end}
item_calls = function() return calls end
''')
    lua.execute((ROOT / 'MaxyModLoader.Core/Runtime/mcp.lua').read_text(encoding='utf-8'))
    lua.execute('''
local bridge = MaxyModLoader.mcp
local value = bridge.dispatch('item_config', 'MML_Test')
assert(value.registered and value.properties.Value == 90 and value.properties.BulletsPerShot == 1)
assert(not bridge.dispatch('item_config', 'Missing').registered)
assert(not pcall(bridge.item_config, '../MML_Test') and item_calls() == 2)
gKosovoItemConfig = nil
assert(not pcall(bridge.item_config, 'MML_Test'))
--已关闭设置页留下的原生命中缓存不得被模拟点击
bridge.hit_test = function()
    return {IsEnabled = function() return true end}, {chain = {{visible = true, enabled = true}, {visible = false, enabled = true}}}
end
assert(not pcall(bridge.dispatch, 'ui_click_point', '0.5,0.5'))
--非玩法阶段拒绝暂停但允许解除暂停避免介绍流程被诊断工具冻结
local core, user_pause = false, false
gGameDelegate.IsCoreGameplayPhase = function() return core end
gGame.SetUserPause = function(self, value) user_pause = value end
gGame.IsPaused = function() return user_pause end
assert(not pcall(bridge.dispatch, 'pause', 'true') and not user_pause)
assert(bridge.dispatch('pause', 'false').paused == false)
core = true
assert(bridge.dispatch('pause', 'true').paused and user_pause)
--隐藏界面可供目录诊断读取但不能成为可点击界面
local hidden = {IsVisible = function() return false end, GetFirstChild = function() return {} end}
gGame.GetPreFSEUIScreen = function() return hidden end
gGameDelegate.GetGameOverlayScreen = function() return nil end
assert(bridge.screen() == nil and bridge.screen(true) == hidden)

--计时原生对象不能进入JSON而应报告类型正常数字仍保留
hidden.GetName = function() return 'hidden' end
hidden.IsVisible = function() return false end
gGameDelegate.IsDuringInteractivePrologue = function() return false end
gGame.IsGameplayPaused = function() return false end
gGame.IsLoadingScreenActive = function() return false end
gGame.IsActive = function() return true end
gGame.GetCurrentFrame = function() return 12 end
gGame.GetGameTime = function() return {} end
gGame.GetGameplayTime = function() return {} end
gScene = nil
tolua = {type = function() return 'UIScreen' end}
local state = bridge.state()
assert(state.timing.GetCurrentFrame == 12 and state.timing.GetGameTime.kind == 'table')
assert(bridge.json(state):find('UIScreen', 1, true))
--物资诊断拒绝错误物品数量角色和阶段无效请求不能调用发放接口
local stock, grants, core_play, scavenge, loading = 0, 0, true, false, false
local test_dweller = {GetDwellerName = function() return 'test' end,
    AddItems = function(self, name, amount) assert(name == 'MML_Test'); stock = stock + amount; grants = grants + 1 end}
gScene = {GetDwellerCount = function() return 1 end, GetDweller = function() return test_dweller end}
gKosovoGlobalState = {GetGlobalItemCount = function(self, name) assert(name == 'MML_Test'); return stock end}
gKosovoItemConfig = {GetEntryWithName = function(self, name) if name == 'MML_Test' then return {Value = 1} end end}
gGameDelegate.IsCoreGameplayPhase = function() return core_play end
gGameDelegate.IsScavenge = function() return scavenge end
gGame.IsLoadingScreenActive = function() return loading end
hidden.FindElementByName = function() return nil end
assert(bridge.dispatch('inventory_item', 'MML_Test').count == 0 and grants == 0)
for _, argument in ipairs({'MML_Test|0|0', 'MML_Test|21|0', 'MML_Test|1|16', 'MML_Test|1|1',
    'Missing|1|0', '../MML_Test|1|0', 'MML_Test|1.5|0', 'MML_Test|1|0|extra'}) do
    assert(not pcall(bridge.dispatch, 'give_item', argument) and grants == 0)
end
core_play = false
assert(not pcall(bridge.dispatch, 'give_item', 'MML_Test|1|0'))
core_play, scavenge = true, true
assert(not pcall(bridge.dispatch, 'give_item', 'MML_Test|1|0'))
scavenge, loading = false, true
assert(not pcall(bridge.dispatch, 'give_item', 'MML_Test|1|0'))
loading = false
local granted = bridge.dispatch('give_item', 'MML_Test|2|0')
assert(granted.before == 0 and granted.after == 2 and granted.diagnostic and grants == 1)
gScene = nil
assert(not pcall(bridge.dispatch, 'inventory_item', 'MML_Test'))
--返回编码失败仍写入关联错误而不是丢失回复造成不确定超时
local original_dispatch = bridge.dispatch
bridge.dispatch = function() return {invalid = function() end} end
local request_id = string.rep('a', 32)
local captured = ''
local original_open, original_remove, original_rename, original_clock = io.open, os.remove, os.rename, os.clock
os.clock = function() return 100 end
os.remove, os.rename = function() end, function() end
io.open = function(path, mode)
    if mode == 'rb' then return {read = function() return 'MML1\\n' .. request_id .. '\\ngame_state\\n\\n' end, close = function() end} end
    return {write = function(self, value) captured = value end, close = function() end}
end
bridge.poll()
io.open, os.remove, os.rename, os.clock = original_open, original_remove, original_rename, original_clock
bridge.dispatch = original_dispatch
assert(captured:find(request_id, 1, true) and captured:find('"ok":false', 1, true))

''')
    print('通过：MCP物品查询、非玩法暂停保护与隐藏界面诊断')


if __name__ == '__main__':
    main()
