-- Splits a run into checkpoint-anchored sections so you can iterate on one of them
-- without simulating everything before it. Sections before `from` are skipped
-- entirely; the first one that runs warps to its checkpoint first.
local Frame = require("tas.frame")

local Sections = {}

function Sections.new(tas, from)
    from = from or 0
    local n = -1
    local self = {}

    function self.section(name, checkpoint, fn)
        n = n + 1
		checkpoint = checkpoint - 1
        if n < from then return self end
        if n == from and checkpoint and checkpoint >= 0 then
            print(string.format("[skip] starting at section %d, warping to checkpoint %d", n, checkpoint))
            tas.warp_to_checkpoint(checkpoint)
        end
		
		local start = tas.frame_count()
        print(string.format("[section %d] %s  (frame %d)", n, name, tas.frame_count()))
        fn()
		local took = tas.frame_count() - start
		print(string.format("[section %d] %s  took %d frames (%.2fs), total %d",
            n, name, took, took * Frame.FIXED_DT, tas.frame_count()))
        return self
    end

    return self
end

return Sections