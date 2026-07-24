-- Offline sink: turns pushed frames into the mod's CSV demo format.
--
-- A sink only has to implement three methods
--
--   sink:set_level(levelId, checkpointId)
--   sink:push(frame, count)   -- frame is READ-ONLY and reused by the caller
--   sink:frame_count()
--
-- Anything else (to_csv, save) is CSV-specific and reached via tas.sink.

local Frame = require("tas.frame")

local CsvSink = {}
CsvSink.__index = CsvSink

function CsvSink.new()
  return setmetatable({
    rows = {},
    level = nil,
    checkpoint = nil,
  }, CsvSink)
end

-- Format numbers the same way everywhere
local function num(v)
  return string.format("%.6g", v)
end

local function bit(v)
  return v and "1" or "0"
end

local function row_to_string(frame)
  local cells = {}
  for _, name in ipairs(Frame.AXES) do
    cells[#cells + 1] = num(frame[name] or 0)
  end
  for _, name in ipairs(Frame.BUTTONS) do
    cells[#cells + 1] = bit(frame[name])
  end
  cells[#cells + 1] = bit(frame[Frame.RESET])
  -- Speed column is always present, usually empty.
  local speed = frame[Frame.SPEED]
  cells[#cells + 1] = speed and (num(speed) .. "x") or ""
  return table.concat(cells, ",")
end

function CsvSink:set_level(level, checkpoint)
  self.level = level
  self.checkpoint = checkpoint
end

function CsvSink:push(frame, count)
  local text = row_to_string(frame)
  for _ = 1, count do
    self.rows[#self.rows + 1] = text
  end
end

function CsvSink:frame_count()
  return #self.rows
end

function CsvSink:to_csv()
  local out = {}
  if self.level then
    out[#out + 1] = "Level: " .. self.level
  end
  if self.checkpoint and self.checkpoint >= 0 then
    out[#out + 1] = "Checkpoint: " .. tostring(self.checkpoint)
  end
  out[#out + 1] = table.concat(Frame.COLUMNS, ",")
  for _, row in ipairs(self.rows) do
    out[#out + 1] = row
  end
  return table.concat(out, "\n") .. "\n"
end

function CsvSink:save(path)
  assert(#self.rows > 0, "nothing to save: the script pushed zero frames")
  local file, err = io.open(path, "wb")
  if not file then
    error("could not write " .. path .. ": " .. tostring(err), 2)
  end
  file:write(self:to_csv())
  file:close()
  return path
end

return CsvSink