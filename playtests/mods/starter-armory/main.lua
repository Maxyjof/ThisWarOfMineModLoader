local items = {
    "MML_AK74", "MML_M4A1", "MML_FAMAS", "MML_G36", "MML_SCARH", "MML_FNFAL", "MML_AUG", "MML_Galil",
    "MML_SIG550", "MML_AN94", "MML_M16A4", "MML_AKM", "MML_MP5", "MML_Uzi", "MML_PPSh41", "MML_P90",
    "MML_Vector", "MML_UMP45", "MML_MP7", "MML_Thompson", "MML_Glock17", "MML_USP45", "MML_FiveSeven",
    "MML_DesertEagle", "MML_Beretta92", "MML_M1911", "MML_P226", "MML_CZ75", "MML_Remington870",
    "MML_BenelliM4", "MML_SPAS12", "MML_Mossberg500", "MML_Saiga12", "MML_DoubleBarrel", "MML_M14",
    "MML_DragunovSVD", "MML_VSSVintorez", "MML_SKS", "MML_M249", "MML_RPK74",
    "MML_AmmoPack1", "MML_AmmoPack2", "MML_AmmoPack3", "MML_AmmoPack4",
    "MML_ReinforcedVest", "MML_ReinforcedHelmet", "MML_HeavyCrowbar", "MML_FieldPickaxe", "MML_FieldLockpick",
    "MML_SturdyShovel"
}

local ammunition = {
    {name = "PistolShells", amount = 30},
    {name = "ShotgunAmmo", amount = 30},
    {name = "RifleAmmo", amount = 30},
    {name = "Ammo", amount = 30}
}

local pending_campaign = false
local completed_campaign = false
local last_attempt_day = nil

--<summary>
--注册每个新战役的庇护所初始物资补足事件
--</summary>
local function install(context)
    --在昼夜事件和新战役场景就绪后重复尝试直到整套物资完成
    local function try_grant(scene)
        if not pending_campaign or not scene or scene:GetDwellerCount() == 0 then return end
        --同一游戏日只尝试一次避免首日昼夜事件和场景就绪事件重复发放
        local current_day = scene:GetCurrentDay()
        if last_attempt_day == current_day then return end
        last_attempt_day = current_day
        if not gKosovoItemConfig or type(gKosovoItemConfig.GetEntryWithName) ~= "function" then
            context.log("开局物资等待原生物品配置就绪，将在后续昼夜事件重试")
            return
        end

        --按当前全局库存补足目标数量重复执行也不会累加
        local dweller = scene:GetDweller(0)
        local function top_up(name, target)
            --只增加缺少的数量保留玩家已有物资并核验写入结果
            local current = context.game.inventory.global_count(name)
            if current < target then dweller:AddItems(name, target - current) end
            --物资接口执行后立即读取公共库存确认已达到目标
            local actual = context.game.inventory.global_count(name)
            if actual < target then error("开局物资写入后数量不足：" .. name .. "，当前=" .. tostring(actual)) end
        end

        --逐项处理已注册内容缺失项保留待办并输出有限诊断样本
        local missing = {}
        local failed = {}
        local function grant_if_registered(name, amount)
            if gKosovoItemConfig:GetEntryWithName(name) then
                --单项失败不阻断其余物资发放后续游戏日会重试失败项
                local ok, failure = pcall(top_up, name, amount)
                if not ok then failed[#failed + 1] = {name = name, reason = tostring(failure)} end
            else
                missing[#missing + 1] = name
            end
        end
        for _, item in ipairs(items) do grant_if_registered(item, 1) end
        for _, item in ipairs(ammunition) do grant_if_registered(item.name, item.amount) end
        if #missing > 0 or #failed > 0 then
            local sample = {}
            for index = 1, math.min(#missing, 8) do sample[index] = missing[index] end
            local failure_sample = {}
            for index = 1, math.min(#failed, 4) do
                failure_sample[index] = failed[index].name .. "=" .. failed[index].reason
            end
            context.log("开局物资未全部核验，已继续处理其他项目并等待下个游戏日重试：未注册=" .. #missing ..
                "项，写入失败=" .. #failed .. "项，未注册示例=" .. table.concat(sample, ",") ..
                "，失败示例=" .. table.concat(failure_sample, " | "))
            return
        end

        --全部内容核验完成后停止本战役重试新战役会重新开启
        context.log("新战役开局物资已核验：模组物品50种至少各1件，4类弹药至少各30发，接收者=" .. dweller:GetDwellerName())
        pending_campaign = false
        completed_campaign = true
    end

    --昼夜事件只重试新战役待办不把读档首日误判成新战役
    context.events.on("game.day.begin", function(scene)
        try_grant(scene)
    end)

    --仅场景首次就绪事件开启新战役待办避免与首日事件重复发放
    context.events.on("game.scene.ready", function(scene, first_time)
        --已成功发放的当前战役不会被同一首日的迟到场景事件重新开启
        if first_time and scene and scene:GetCurrentDay() == 1 and not completed_campaign then pending_campaign = true end
        try_grant(scene)
    end)
end

return {
    --<summary>
    --安装开局测试物资发放器
    --</summary>
    on_load = function(context)
        install(context)
    end
}
