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

--<summary>
--注册每个新战役的庇护所初始物资补足事件
--</summary>
local function install(context)
    --等待第1天庇护所事件确保原生库存和物品注册已经初始化
    context.events.on("game.day.begin", function(scene)
        --只处理新战役首日旧战役的持久数据标记不能阻止新存档发放
        if not scene or scene:GetCurrentDay() ~= 1 then return end
        if scene:GetDwellerCount() == 0 then return end

        --先检查全部物品已注册再开始写入避免依赖未加载导致半套物资
        if not gKosovoItemConfig then error("原生物品配置尚未就绪") end
        for _, item in ipairs(items) do
            if not gKosovoItemConfig:GetEntryWithName(item) then error("测试物品尚未注册：" .. item) end
        end
        for _, item in ipairs(ammunition) do
            if not gKosovoItemConfig:GetEntryWithName(item.name) then error("原版弹药尚未注册：" .. item.name) end
        end

        --按当前全局库存补足目标数量使同一首日事件重复触发也不会累加
        local dweller = scene:GetDweller(0)
        local function top_up(name, target)
            --只增加缺少的数量保留玩家已有物资并核验写入结果
            local current = context.game.inventory.global_count(name)
            if current < target then dweller:AddItems(name, target - current) end
            --物资接口执行后立即读取公共库存确认已达到目标
            local actual = context.game.inventory.global_count(name)
            if actual < target then error("开局物资写入后数量不足：" .. name .. "，当前=" .. tostring(actual)) end
        end
        for _, item in ipairs(items) do top_up(item, 1) end
        for _, item in ipairs(ammunition) do top_up(item.name, item.amount) end

        --记录实测结果便于玩家确认本次新战役收到物资
        context.log("新战役开局物资已核验：模组物品50种至少各1件，4类弹药至少各30发，接收者=" .. dweller:GetDwellerName())
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
