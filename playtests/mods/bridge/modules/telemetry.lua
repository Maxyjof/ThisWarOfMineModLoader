local context = ...
local module = {counts = {}, rows = {}, originals = {}}

--<summary>
--记录真实回调参数并保存独立于游戏存档的测试数据
--</summary>
function module.record(kind, fields)
    --限制记录数量避免长时间运行造成日志无限增长
    module.counts[kind] = (module.counts[kind] or 0) + 1
    if #module.rows >= (context.config.event_limit or 1000) then return end
    local parts = {context.config.run_id or "unknown", kind}
    local keys = {}
    for key in pairs(fields or {}) do table.insert(keys, key) end
    table.sort(keys)
    for _, key in ipairs(keys) do
        table.insert(parts, key .. "=" .. tostring(fields[key]):gsub("[\t\r\n]", " "))
    end
    local line = table.concat(parts, "\t")
    table.insert(module.rows, line)
    context.log(line)

    --数据写入加载器目录不写入游戏原生存档
    if io and io.open then
        local file = io.open("MaxyModLoader/playtests.tsv", "a")
        if file then file:write(line .. "\n"); file:close() end
    end
end

--<summary>
--将当前场景中的真实角色参数写入测试记录
--</summary>
function module.snapshot(scene, reason)
    --逐个读取原生角色实体避免把配置表的值当作实际游戏状态
    if not scene or not scene.GetDwellerCount then return end
    module.record("scene.snapshot", {reason = reason, dwellers = scene:GetDwellerCount(), day = scene:GetCurrentDay()})
    for index = 0, scene:GetDwellerCount() - 1 do
        local dweller = scene:GetDweller(index)
        module.record("character.snapshot", {name = dweller:GetDwellerName(), hungry = dweller:GetParameterValue("Hungry"),
            tired = dweller:GetParameterValue("Tired"), sick = dweller:GetParameterValue("Sick"), wounded = dweller:GetParameterValue("Wounded")})
    end
end

--保留修改之前的游戏配置便于同时加载的模组进行前后对比
if igParams and igParams.ScavengeConfig then
    for name, params in pairs(igParams.ScavengeConfig) do module.originals[name] = params.Duration end
end
return module
