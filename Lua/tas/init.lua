-- Assembles the tas API over a sink, choosing the backend automatically:
--   offline (plain `lua run.lua`)      -> CsvSink, no game bridge
--   in-game (C# set __tas_live = true) -> LiveSink + the `game` bridge

local CsvSink = require("tas.sink_csv")
local Commands = require("tas.commands")

local M = {}
M.CsvSink = CsvSink

function M.new(sink)
    if not sink then
        if __tas_live then
            local live = require("tas.sink_live").new()
			-- route the live-inputs through the recorder so it can be dumped to "offline" raw csv/lua
			sink = require("tas.sink_record").new(live)
        else
            sink = CsvSink.new()
        end
    end
    -- __tas_game is the C# GameState bridge live, and nil offline. Commands that
    -- need it assert clearly when it's missing.
    return Commands.build(sink, __tas_game)
end

return M