-- Live sink: hands each frame to the C# host by yielding the coroutine.
-- Same three-method contract as CsvSink

local LiveSink = {}
LiveSink.__index = LiveSink

function LiveSink.new()
  return setmetatable({ level = nil, checkpoint = nil, count = 0 }, LiveSink)
end

function LiveSink:set_level(level, checkpoint)
  self.level = level
  self.checkpoint = checkpoint
end

function LiveSink:frame_count()
  return self.count
end

function LiveSink:push(frame, n)
  for _ = 1, n do
    self.count = self.count + 1
    -- Pause here. C# reads this frame's columns, advances the game one step,
    -- then resumes us. On resume, game state reflects the frame we just gave.
    coroutine.yield(frame)
  end
end

return LiveSink