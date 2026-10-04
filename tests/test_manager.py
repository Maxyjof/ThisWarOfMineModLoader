import pathlib
import sys

ROOT = pathlib.Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT / 'local/tools'))
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
    lua.execute((ROOT / 'MaxyModLoader.Core/Runtime/manager.lua').read_text(encoding='utf-8'))
    lua.execute('''
local manager = MaxyModLoader.manager
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


if __name__ == '__main__':
    main()
