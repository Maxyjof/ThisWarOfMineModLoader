local context = ...
local module = {}

--<summary>
--真实新一天回调中降低疲劳并读取最终角色参数
--</summary>
function module.install(telemetry)
    --保留原版昼夜推进只在它完成后应用辅助规则
    context.events.on("game.day.begin", function(scene)
        for index = 0, scene:GetDwellerCount() - 1 do
            local dweller = scene:GetDweller(index)
            local before = dweller:GetParameterValue("Tired")
            dweller:SetParameterValue("Tired", math.max(0, before - context.config.daily_tired_relief))
            dweller:SolveParameterDependency()
            telemetry.record("recovery.applied", {name = dweller:GetDwellerName(), before = before,
                after = dweller:GetParameterValue("Tired")})
        end
    end)
end
return module
