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
local native_down, native_up = true, false
gGame.IsActive = function() return true end
gGame.IsCursorOnGameWindow = function() return true end
local cursor = {x = 0.5, y = 0.5}
gGame.GetCursorPosition = function() return cursor end
gGame.GetNewTapForUIElement = function() error('不应调用触摸查询') end
gGame.IsMouseButtonPressedForTheFirstTime = function(self, code) assert(code == 65536); return native_down end
gGame.IsMouseButtonReleasedForTheFirstTime = function(self, code) assert(code == 65536); return native_up end
manager.screen = {GetElementAtScreenPosition = function(self, point) assert(point == cursor); return label end}
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
''')
    print('通过：界面引用清理、UTF16字符、文字命中、拖出取消与原生鼠标帧处理')


if __name__ == '__main__':
    main()
