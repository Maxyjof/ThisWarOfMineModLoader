import pathlib
import sys

ROOT = pathlib.Path(__file__).resolve().parents[1]
try:
    from lupa.lua51 import LuaRuntime
except ImportError:
    #本机依赖回退仅用于验证不随加载器分发
    sys.path.insert(0, str(ROOT / 'local/tools'))
    from lupa.lua51 import LuaRuntime


def main():
    """
    <summary>
    验证设置淡入取消应用和后台确认之间的状态边界
    </summary>
    """
    #界面替身不代表游戏实际渲染截图和模式切换另行验证
    lua = LuaRuntime()
    lua.execute('''
files, frame, now, clicks, apply_ready = {}, 1, 100, 0, false
os.time = function() return now end
os.getenv = function() return nil end
io.open = function(path, mode)
    if mode == 'wb' then
        files[path] = ''
        return {write = function(self, value) files[path] = files[path] .. value end, close = function() end}
    end
    if not files[path] then return nil end
    local lines = {}
    for line in files[path]:gmatch('([^\\n]*)\\n') do table.insert(lines, line) end
    local index = 0
    return {read = function() index = index + 1; return lines[index] end, close = function() end}
end
os.rename = function(source, target) files[target], files[source] = files[source], nil; return true end
local function node(name, parent)
    local self = {name = name, parent = parent, children = {}, visible = true, font = 18}
    function self:GetName() return self.name end
    function self:SetName(value) self.name = value end
    function self:GetParent() return self.parent end
    function self:FindElementByName(value)
        for _, child in ipairs(self.children) do if child.name == value then return child end end
    end
    function self:AddChild(child) child.parent = self; table.insert(self.children, child) end
    function self:CreateElementFromSubRecipe(value)
        if value == 'VALUE' then return nil end
        return node(value, self)
    end
    function self:IsVisible() return self.visible end
    function self:Hide() self.visible = false end
    function self:SetVisible(value) self.visible = value end
    function self:SetText(value) self.text = value end
    function self:CopyText(value) self.font = value.font end
    function self:IsUITextBase() return true end
    function self:RaiseFlag() end
    function self:SetWindowAlignment() end
    function self:SetAnchor() end
    function self:SetPosition() end
    function self:SetSize() end
    function self:SetAlignment() end
    function self:SetColor() end
    function self:SetColorMode() end
    return self
end
panel, area, slot = node('Settings'), node('PANEL'), node('SETTING_SLOT')
title, slider = node('TITLE'), node('SLIDER')
slot:AddChild(title); slot:AddChild(slider)
original_value, original_left, original_right = node('VALUE'), node('BUTTON_LEFT'), node('BUTTON_RIGHT')
slider:AddChild(original_value); slider:AddChild(original_left); slider:AddChild(original_right)
original_apply = node('BUTTON_APPLY'); area:AddChild(original_apply)
screen = {
    FindElementByName = function() return panel end,
    GetElementAtScreenPosition = function(self, point)
        if point.y > 0.9 then return apply_ready and original_apply or nil end
        if point.x < 0.5 then return nil end
        return original_right
    end,
    SimulateClick = function(self, value) assert(value == original_apply); clicks = clicks + 1; panel:Hide() end
}
Vector = {Instance = function(self, x, y) return {x = x, y = y} end}
UIText = {new = function() return node('') end}
gGame = {GetBigFrameIndex = function() return frame end}
gConfigHelper = {GetFullScreen = function() return false end}
LuaGameDelegate = {OnTick = function() end, OnPauseTick = function() end}
MaxyModLoader = {log = function() end, manager = {buttons = {}, unicode = function(value) return value end},
    mcp = {screen = function() return screen end}}
''')
    lua.execute((ROOT / 'MaxyModLoader.Core/Runtime/display.lua').read_text(encoding='utf-8'))
    lua.execute('''
local display = MaxyModLoader.display
display.ready('borderless')
display.tick()
assert(not display.open and not display.apply)
apply_ready = true
display.tick()
assert(display.open and display.value.font == original_value.font)
assert(not original_value.visible and title.text == '窗口模式' and display.value.text == '无边框全屏')
assert(display.left.visible and not display.right.visible)
display.select(-1); display.select(-1)
assert(display.selected == 'windowed' and display.mode == 'borderless' and not display.left.visible)
--取消关闭后重新打开从实际模式初始化不写入偏好
panel:Hide(); display.tick(); panel.visible = true; display.tick()
assert(display.selected == 'borderless' and not files['MaxyModLoader/display-request.txt'])
display.select(-1)
MaxyModLoader.manager.buttons.MML_SETTINGS_APPLY.handler()
assert(clicks == 1 and not display.pending)
frame = frame + 1; display.tick()
assert(display.pending and files['MaxyModLoader/display-request.txt']:find('fullscreen'))
assert(not MaxyModLoader.manager.buttons.MML_WINDOW_LEFT.available())
assert(not pcall(display.request, 'windowed'))
--旧响应不能确认新请求随后只接受关联标识相同的结果
files['MaxyModLoader/display-response.txt'] = 'old\\nok\\nfullscreen\\n'
display.tick(); assert(display.pending and display.mode == 'borderless')
files['MaxyModLoader/display-response.txt'] = display.pending .. '\\nok\\nfullscreen\\n'
display.tick(); assert(not display.pending and display.mode == 'fullscreen')
assert(not pcall(display.request, 'injected'))
display.request('windowed'); now = now + 16; display.tick()
assert(not display.pending and display.mode == 'fullscreen' and display.error)
''')
    print('通过：原版设置淡入等待、端点命中、字体复制、取消、单次应用和关联确认')


if __name__ == '__main__':
    main()
