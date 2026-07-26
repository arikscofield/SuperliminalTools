-- Tees frames to an inner sink (the live one) while capturing them, so a live
-- run can be dumped as a standalone offline artifact (CSV or a raw-frame .lua).
local Frame = require("tas.frame")

local function num(v) return string.format("%.9g", v or 0) end
local function bit(v) return v and "1" or "0" end

local RecordSink = {}
RecordSink.__index = RecordSink

function RecordSink.new(inner)
  return setmetatable({ inner = inner, rows = {}, level = nil, checkpoint = nil }, RecordSink)
end

function RecordSink:set_level(level, checkpoint)
  self.level, self.checkpoint = level, checkpoint
  if self.inner and self.inner.set_level then self.inner:set_level(level, checkpoint) end
end

function RecordSink:frame_count()
  return self.inner and self.inner:frame_count() or #self.rows
end

function RecordSink:push(frame, n)
  -- Snapshot first: the caller reuses/mutates `frame` after this returns, and
  -- the live inner sink yields (suspends) mid-push, so capture before forwarding.
  local snap = {}
  for _, col in ipairs(Frame.COLUMNS) do snap[col] = frame[col] end
  self.rows[#self.rows + 1] = { f = snap, n = n }
  if self.inner then self.inner:push(frame, n) end
end

-- Exact offline demo, identical to what an offline CsvSink run would save.
function RecordSink:to_csv()
  local out = {}
  if self.level then out[#out+1] = "Level: " .. self.level end
  if self.checkpoint and self.checkpoint >= 0 then
    out[#out+1] = "Checkpoint: " .. tostring(self.checkpoint)
  end
  out[#out+1] = table.concat(Frame.COLUMNS, ",")
  for _, r in ipairs(self.rows) do
    local f, cells = r.f, {}
    for _, a in ipairs(Frame.AXES) do cells[#cells+1] = num(f[a]) end
    for _, b in ipairs(Frame.BUTTONS) do cells[#cells+1] = bit(f[b]) end
    cells[#cells+1] = bit(f[Frame.RESET])
    local sp = f[Frame.SPEED]
    cells[#cells+1] = sp and (num(sp) .. "x") or ""
    local line = table.concat(cells, ",")
    for _ = 1, r.n do out[#out+1] = line end   -- expand run length
  end
  return table.concat(out, "\n") .. "\n"
end

-- Standalone offline .lua: one tas.frame() per run, only non-neutral columns.
function RecordSink:to_script()
  local out = { 'local tas = require("tas").new()' }
  if self.level then
    out[#out+1] = string.format("tas.level(%q, %d)", self.level, self.checkpoint or -1)
  end
  for _, r in ipairs(self.rows) do
    local f, parts = r.f, {}
    for _, a in ipairs(Frame.AXES) do
      if (f[a] or 0) ~= 0 then parts[#parts+1] = string.format("[%q]=%s", a, num(f[a])) end
    end
    for _, b in ipairs(Frame.BUTTONS) do
      if f[b] then parts[#parts+1] = string.format("[%q]=true", b) end
    end
    if f[Frame.RESET] then parts[#parts+1] = string.format("[%q]=true", Frame.RESET) end
    if f[Frame.SPEED] then parts[#parts+1] = string.format("[%q]=%s", Frame.SPEED, num(f[Frame.SPEED])) end
    out[#out+1] = string.format("tas.frame({%s}, %d)", table.concat(parts, ", "), r.n)
  end
  out[#out+1] = "tas.save(...)   -- pass an output path"
  return table.concat(out, "\n") .. "\n"
end

-- In-game there is no `io` (MoonSharp's io package is stripped under Unity), so
-- C# hands us __tas_write; offline we use the stock library.
local function write(path, text)
  if __tas_write then return __tas_write(path, text) end
  local file, err = io.open(path, "wb")
  if not file then error("could not write " .. path .. ": " .. tostring(err), 2) end
  file:write(text); file:close()
  return path
end

-- tas.save(path) reaches this; .lua path -> script, anything else -> CSV.
function RecordSink:save(path)
  assert(#self.rows > 0, "nothing recorded")
  if path:match("%.lua$") then return write(path, self:to_script()) end
  return write(path, self:to_csv())
end

return RecordSink