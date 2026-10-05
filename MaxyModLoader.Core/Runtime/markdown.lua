local api = MaxyModLoader
local markdown = {}
api.markdown = markdown

--<summary>
--将已解析的Markdown语法树排成带有真实块边界的原生文字行
--</summary>
function markdown.layout(blocks, width, decode)
    local rows, top = {}, 0
    --拉丁文字根据字形宽度估算中文使用全角宽度换行不插入额外空格
    local function advance(item, size, code)
        if item.code >= 0x2e80 then return size end
        if code then return size * 0.6 end
        if item.text:match('[ilI%.,:;!|%s]') then return size * 0.3 end
        if item.text:match('[MW@%%]') then return size * 0.86 end
        return size * 0.55
    end
    --每一行携带绝对内容坐标高度和装饰所有滚动偏移共享这些几何信息
    local function add(row)
        row.top = top
        top = top + row.height
        table.insert(rows, row)
    end
    --空白是块间距不再伪装成固定高度的文字行
    local function gap(height)
        if top > 0 then add({runs = {}, height = height}) end
    end
    --保留复合行内样式软换行已由标准解析器合并硬换行仍强制断行
    local function inline(runs, left, available, size, decoration, align)
        local current, used = {}, 0
        local function flush(force)
            if #current == 0 and not force then return end
            local shift = align == 'right' and available - used or align == 'center' and (available - used) / 2 or 0
            for _, run in ipairs(current) do run.x = run.x + math.max(0, shift) end
            add({runs = current, height = math.ceil(size * 1.5), decoration = decoration,
                left = left, width = available})
            current, used = {}, 0
        end
        for _, source in ipairs(runs or {}) do
            --图片当前用明确的替代说明呈现不能把图片路径作为Lua执行
            local value = source.image and source.image ~= '' and '[图片：' .. source.text .. ']' or source.text or ''
            for _, item in ipairs(decode(value)) do
                if item.code == 10 then flush(true)
                else
                    local step = item.code == 9 and size * 2.4 or advance(item, size, source.code)
                    if used + step > available and used > 0 then flush(false) end
                    local previous = current[#current]
                    if previous and previous.source == source then
                        previous.text, previous.width = previous.text .. item.text, previous.width + step
                    else
                        table.insert(current, {text = item.text, x = left + used, width = step, size = size,
                            strong = source.strong, emphasis = source.emphasis, strike = source.strike,
                            code = source.code, link = source.link, source = source})
                    end
                    used = used + step
                end
            end
        end
        flush(false)
    end
    local walk
    --表格逐列分别排版之后合并为同一行高度保持列与分隔线对齐
    local function table_block(block, left, available)
        local columns = 0
        for _, row in ipairs(block.children or {}) do columns = math.max(columns, #(row.children or {})) end
        if columns == 0 then return end
        local cell_width = available / columns
        for _, source_row in ipairs(block.children or {}) do
            local start_top, start_count, cells, row_height = top, #rows, {}, 0
            for column, cell in ipairs(source_row.children or {}) do
                rows, top = {}, 0
                --单元格可有多行正文表头样式只影响当前列
                local runs = {}
                local cell_runs = {}
                for _, child in ipairs(cell.children) do
                    for _, run in ipairs(child.runs) do table.insert(cell_runs, run) end
                end
                for _, run in ipairs(cell_runs) do
                    local copy = {}
                    for key, value in pairs(run) do copy[key] = value end
                    copy.strong = source_row.header or copy.strong
                    table.insert(runs, copy)
                end
                inline(runs, left + (column - 1) * cell_width + 8, cell_width - 16, 17, nil, cell.alignment)
                cells[column], row_height = rows, math.max(row_height, top)
            end
            local combined = {runs = {}, height = math.max(28, row_height + 10), decoration = 'table',
                left = left, width = available, columns = columns, header = source_row.header}
            for _, cell_rows in ipairs(cells) do
                for _, row in ipairs(cell_rows) do
                    for _, run in ipairs(row.runs) do run.y = row.top + 5; table.insert(combined.runs, run) end
                end
            end
            --恢复外层文档后追加整行单元格不会改变后续块的原点
            rows, top = block._outer_rows, start_top
            while #rows > start_count do table.remove(rows) end
            add(combined)
        end
    end
    --递归处理标准解析器产生的容器嵌套深度已在打包时限制
    walk = function(block, left, available, decoration)
        local kind = block.kind
        if kind == 'heading' then
            gap(12)
            inline(block.runs, left, available, math.max(19, 27 - (block.level or 2) * 2), 'heading')
            gap(5)
        elseif kind == 'paragraph' then inline(block.runs, left, available, 18, decoration); gap(7)
        elseif kind == 'rule' then gap(6); add({runs = {}, height = 12, decoration = 'rule', left = left, width = available})
        elseif kind == 'code' then
            gap(5)
            if block.language and block.language ~= '' then inline({{text = block.language, code = true}}, left + 9, available - 18, 14, 'code') end
            inline(block.runs, left + 9, available - 18, 16, 'code'); gap(8)
        elseif kind == 'quote' then
            for _, child in ipairs(block.children or {}) do walk(child, left + 18, available - 18, 'quote') end
        elseif kind == 'list' then
            for index, item in ipairs(block.children or {}) do
                local first = #rows + 1
                for _, child in ipairs(item.children or {}) do walk(child, left + 25, available - 25, decoration) end
                if rows[first] then
                    table.insert(rows[first].runs, 1, {text = block.ordered and tostring((block.start or 1) + index - 1) .. '.' or '-',
                        x = left, width = 23, size = 18})
                end
            end
        elseif kind == 'table' then
            gap(5); block._outer_rows = rows; table_block(block, left, available); block._outer_rows = nil; gap(10)
        else
            if block.runs then inline(block.runs, left, available, 18, decoration) end
            for _, child in ipairs(block.children or {}) do walk(child, left, available, decoration) end
        end
    end
    for _, block in ipairs(blocks or {}) do walk(block, 0, width, nil) end
    return rows, top
end
