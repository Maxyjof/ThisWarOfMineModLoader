local context = ...

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
--注册新存档庇护所物资发放事件
--</summary>
local function install()
    --已完成的测试物资发放在后续进程中保持幂等
    if context.storage.get("granted", false) then
        context.log("测试物资已发放过，本次跳过")
        return
    end

    --等待第1天庇护所事件确保幸存者和原生库存已经初始化
    context.events.on("game.day.begin", function(scene)
        if context.storage.get("granted", false) then return end
        if not scene or scene:GetDwellerCount() == 0 then return end

        --先检查全部物品已注册再开始写入避免依赖未加载导致半套物资
        if not gKosovoItemConfig then error("原生物品配置尚未就绪") end
        for _, item in ipairs(items) do
            if not gKosovoItemConfig:GetEntryWithName(item) then error("测试物品尚未注册：" .. item) end
        end
        for _, item in ipairs(ammunition) do
            if not gKosovoItemConfig:GetEntryWithName(item.name) then error("原版弹药尚未注册：" .. item.name) end
        end

        --使用已验证的幸存者物品接口发到第一名角色携带栏
        local dweller = scene:GetDweller(0)
        for _, item in ipairs(items) do dweller:AddItems(item, 1) end
        for _, item in ipairs(ammunition) do dweller:AddItems(item.name, item.amount) end

        --发放成功后写入跨进程标记避免每次启动重复添加
        context.storage.set("granted", true)
        context.log("开局物资已发放：模组物品50种各1件，4类弹药各30发，接收者=" .. dweller:GetDwellerName())
    end)
end

return {
    --<summary>
    --安装开局测试物资发放器
    --</summary>
    on_load = function()
        install()
    end
}
