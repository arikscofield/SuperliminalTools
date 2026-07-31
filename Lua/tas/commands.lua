-- Defines all of the tas.* commands that can be ran
--
-- every command returns `tas` back, so you can chain (e.g. tas.forward(5).wait(5)


local Frame = require("tas.frame")

local Commands = {}

-- Safety cap for wait commands
local WAIT_MAX = 1500

function Commands.build(sink, game)
    local tas = { sink = sink, game = game }

    -- Sticky/Held button state: hold() sets it, release() clears it, and every
    -- frame emitted in between carries it.
    local held = {}
    for _, name in ipairs(Frame.BUTTONS) do
        held[name] = false
    end
    
	-- Sticky/Held move axis state: hold_forward(), release_forward(), etc
	-- in LINEAR speed units (not axis values). Converted
    -- to axis values once, in emit().
	local held_move = { x = 0, y = 0 }


	-- Lock functions for locking movement/aim every frame based on some function
	local move_lock = nil
	local look_lock = nil
    
	-- True while a rotate/spin is in flight
	local spinning = false

    -- One-shot values that attach to the next frame emitted, then clear.
    local pending_speed = nil
    local pending_reset = false
    
	-------------------------------------------------------- helpers
	
    -- Get the current sensitivity (pitch, yaw); fallback to 1 if unable to
    local function look_scale()
        if game then
            local sx, sy = game.look_scale()
            if sx and sx ~= 0 and sy and sy ~= 0 then return sx, sy end
        end
        return 1, 1
    end
	
    -- The motor normalizes the move vector, clamps its length to 1, then SQUARES it
	-- so a request for linear speed s has to be emitted with magnitude
    -- sqrt(s).
    local function axes_for_request(x, y)
        local len = math.sqrt(x * x + y * y)
        if len <= 0 then return 0, 0 end
        local s = (len > 1) and 1 or len     -- clamp exactly like the motor
        local m = math.sqrt(s) / len         -- unit vector * sqrt(s)
        return x * m, y * m
    end
	
	-- Linear move request at an angle off your current facing, 0 = forward, 90 = right.
    local function axes_for(deg, speed)
        local r = math.rad(deg or 0)
        speed = speed or 1
        return axes_for_request(math.sin(r) * speed, math.cos(r) * speed)
    end

	-- World yaw from the player toward (x, z), or nil once within `tol`.
    local function yaw_toward(x, z, tol)
        local px, _, pz = game.player_pos()
        local dx, dz = x - px, z - pz
        if (dx * dx + dz * dz) <= tol * tol then return nil end
        return math.deg(math.atan2(dx, dz))
    end
	
	local function pos_axis(axis)
        local x, y, z = game.player_pos()
        if axis == "x" then return x end
        if axis == "y" then return y end
        if axis == "z" then return z end
        error('wait: axis must be "x", "y" or "z", got ' .. tostring(axis))
    end
	
	-- ensures we have a live-connection to the game
    local function need_game()
        assert(game, "this command only works live in-game (needs the game bridge)")
        return game
    end
	
	-- check that the button is a valid button
	local function check_button(name)
        assert(Frame.is_button(name),
            "no such button: " .. tostring(name) ..
            " (expected one of: " .. table.concat(Frame.BUTTONS, ", ") .. ")")
        return name
    end

    -- The single primitive. Builds one frame from sticky state + overrides
    -- and hands it to the sink `count` times.
    local function emit(count, overrides)
        count = count or 1
        assert(count >= 0, "frame count must not be negative")
        if count == 0 then return tas end

        local f = Frame.blank()
        for button, value in pairs(held) do f[button] = value end
		f["Move Horizontal"], f["Move Vertical"] = axes_for_request(held_move.x, held_move.y)
        if overrides then
            for name, value in pairs(overrides) do
                assert(f[name] ~= nil or name == Frame.SPEED,
                    "unknown column: " .. tostring(name))
                f[name] = value
            end
        end

        -- Speed and checkpoint reset are events: they belong to the first
        -- frame of this block only, never to all `count` of them.
        if pending_speed or pending_reset then
            f[Frame.SPEED] = pending_speed
            f[Frame.RESET] = pending_reset
            sink:push(f, 1)
            f[Frame.SPEED] = nil
            f[Frame.RESET] = false
            pending_speed, pending_reset = nil, false
            count = count - 1
        end
		
		if count == 0 then return tas end

		-- An explicit look/move this frame takes priority over its lock
		local manual_look = overrides and (overrides["Look Horizontal"] or overrides["Look Vertical"])
		local manual_move = overrides and (overrides["Move Horizontal"] or overrides["Move Vertical"])
		local live_look = look_lock and not manual_look and not spinning
		local live_move = move_lock and not manual_move
		
		if not (live_look or live_move) then
			sink:push(f, count)
			return tas
		end
		
		-- A lock is active; go one frame at a time and re-correct look/move angles each frame
		for _ = 1, count do
			if live_look then
				local yerr, perr = look_lock()
				if yerr then
					local sx, sy = look_scale()
					f["Look Horizontal"] = yerr / sx
					f["Look Vertical"] = perr / sy
				end
			end
			
			if live_move then
				f["Move Horizontal"], f["Move Vertical"] = move_lock()
				if f["Move Horizontal"] == 0 and f["Move Vertical"] == 0 then
					move_lock = nil
					live_move = false
				end
			end
			
			sink:push(f, 1)
		end
		
        return tas
    end


    ---------------------------------------------------------------- meta stuff

    -- Level the demo targets. checkpoint defaults to -1, meaning "start of level".
    function tas.level(id, checkpoint)
        sink:set_level(id, checkpoint or -1)
        return tas
    end
	
	-- Write the recorded frames out. Path ending in .lua writes a standalone
    -- script, anything else writes the CSV demo format.
    function tas.save(path)
        assert(sink.save, "this sink does not support save()")
        assert(not pending_speed and not pending_reset,
            "speed()/reset_checkpoint() was called but no frame followed it")
        return sink:save(path)
    end
	
	-- Declare an auto-save target. The host calls __tas_autosave when playback
    -- ends -- script finished, level finished, or a manual stop.
    local auto_path = nil
    function tas.record(path)
        assert(sink.save, "this sink does not support save()")
        auto_path = path
        return tas
    end

    __tas_autosave = function()
		local frames = sink:frame_count()
        print(string.format("run finished: %d frames (%.2fs)", frames, frames * Frame.FIXED_DT))

        local path = auto_path
        auto_path = nil
        if not path or not sink.save then return end
        if sink:frame_count() == 0 then return end
        print("saved " .. sink:save(path))
    end
	
	
	-- Jump straight to a checkpoint. Use it to skip the parts of a run you're not working on.
    function tas.warp_to_checkpoint(index)
        need_game()
        game.warp_to_checkpoint(index)
        emit(1)                          -- warp lands at the end of this frame
        return tas.wait_until_grounded(60)
    end

    ------------------------------------------------------------------- time / waits

	-- Emit `frames` frames of whatever is currently held/locked (default 1 frame)
    function tas.wait(frames)
        return emit(frames)
    end
	
	-- Emit blank frames (keeping held buttons) until pred() is true, or until
    -- `max` frames pass. `pred` is a Lua function returning a boolean.
    function tas.wait_until(pred, max)
        need_game()
        max = max or WAIT_MAX
        for _ = 1, max do
            if pred() then return tas end
            emit(1)
        end
		error("wait_until: predicate never came true within max time frame. (" .. max .. " frames")
    end

	-- Wait until the player is standing on the ground
    function tas.wait_until_grounded(max)
		need_game()
        return tas.wait_until(function() return game.grounded() end, max)
    end

	-- Wait until the player leaves the ground
    function tas.wait_until_airborne(max)
		need_game()
        return tas.wait_until(function() return not game.grounded() end, max)
    end
	
	-- Wait until a Jump press will actually jump: grounded and off cooldown
    function tas.wait_can_jump(max)
        need_game()
        return tas.wait_until(function() return game.can_jump() end, max)
    end

	-- Wait until reticle is over something grabbable
	function tas.wait_until_grabbable(max)
        need_game()
        return tas.wait_until(function() return game.is_ready_to_grab() end, max)
    end
	
	-- Wait until an axis ("x"/"y"/"z") of the player position is above `value`
	function tas.wait_until_above(axis, value, max)
        need_game()
        return tas.wait_until(function() return pos_axis(axis) > value end, max)
    end

	-- Wait until an axis ("x"/"y"/"z") of the player position is below `value`
    function tas.wait_until_below(axis, value, max)
        need_game()
        return tas.wait_until(function() return pos_axis(axis) < value end, max)
    end

    -- Wait until an axis crosses `value`, picking the direction based on where you
    -- are when it's called. aka: "walk until z reaches 18.5".
    function tas.wait_until_pos(axis, value, max)
        need_game()
        if pos_axis(axis) < value then
            return tas.wait_until_above(axis, value, max)
        end
        return tas.wait_until_below(axis, value, max)
    end

    -- Wait until within `dist` meters (default 0.25) of given world point (x, z).
    -- Horizontal distance only; y is ignored
    function tas.wait_until_near(x, z, dist, max)
        need_game()
        dist = dist or 0.25
        return tas.wait_until(function()
            local px, _, pz = game.player_pos()
            local dx, dz = x - px, z - pz
            return (dx * dx + dz * dz) <= dist * dist
        end, max)
    end

        -- Wait until the game registers a new checkpoint (index goes up).
    function tas.wait_until_checkpoint(max)
        need_game()
        local start = game.checkpoint_index()
        return tas.wait_until(function() return game.checkpoint_index() > start end, max)
    end


    ---------------------------------------------------------------- movement

	-- Raw camera-relative move axes for `frames` frames.
    -- x = strafe (-1 left, 1 right), y = forward/back (1 forward).
    function tas.move(x, y, frames)
        local mh, mv = axes_for_request(x or 0, y or 0)
        return emit(frames, { ["Move Horizontal"] = mh, ["Move Vertical"] = mv })
    end
	
	-- Raw axis values, no speed compensation. Only for reproducing a recorded
    -- frame exactly; prefer move() everywhere else.
    function tas.move_axes(x, y, frames)
        return emit(frames, {
            ["Move Horizontal"] = x or 0,
            ["Move Vertical"] = y or 0,
        })
    end
	
	-- forward / back / left / right, each with multiple options:
    --
    --   tas.forward(frames, speed?)  -- speed 0->1 (default 1) for `frames` frames
    --   tas.hold_forward(speed)      -- sticky, default speed 1, until release
    --   tas.release_forward()        -- clear that axis
    local DIRECTIONS = {
        forward = {  0,  1 },
        back    = {  0, -1 },
        left    = { -1,  0 },
        right   = {  1,  0 },
    }

    for name, dir in pairs(DIRECTIONS) do
        local hx, hy = dir[1], dir[2]
        local key = (hx ~= 0) and "x" or "y"
        local sign = (hx ~= 0) and hx or hy

        tas[name] = function(frames, speed)
			return tas.move(hx * (speed or 1), hy * (speed or 1), frames)
        end

        tas["hold_" .. name] = function(speed)
			held_move[key] = sign * (speed or 1)
            return tas
        end

        tas["release_" .. name] = function()
			held_move[key] = 0
            return tas
        end
    end


    -- Move at an arbitrary angle relative to current facing.
    -- 0 = forward, 90 = right. speed is linear 0 to 1
    function tas.move_dir(deg, speed, frames)
        local x, y = axes_for(deg, speed)
        return tas.move(x, y, frames)
    end
	
	
	-------------- absolute world movement
	
	-- Move along an absolute WORLD direction for `frames` frames, regardless of
    -- where you're looking. Same angle convention as look(): 0 = +Z, 90 = +X.
    -- speed is linear 0..1. Recomputed every frame, so it stays true even while
    -- the camera is turning.
    function tas.move_world(yaw, speed, frames)
        need_game()
        frames = frames or 1
        assert(frames >= 0, "move_world: frames must not be negative")
        for _ = 1, frames do
            local mh, mv = game.move_axes_for(yaw or 0, speed or 1)
            emit(1, { ["Move Horizontal"] = mh, ["Move Vertical"] = mv })
        end
        return tas
    end

    -- Same, but the direction is whatever you're facing at the moment of the
    -- call -- i.e. "run straight ahead for N frames, then look wherever".
    function tas.move_forward_world(speed, frames)
        need_game()
        return tas.move_world(game.facing(), speed, frames)
    end

    -- Walk toward a world point for at most `frames` frames, re-aiming every
    -- frame; stops early once within `tol` metres. y is ignored.
    function tas.move_to_point(x, z, speed, frames, tol)
        need_game()
        frames = frames or 1
        tol = tol or 0.25
        for _ = 1, frames do
            local yaw = yaw_toward(x, z, tol)
            if not yaw then return tas end
            local mh, mv = game.move_axes_for(yaw, speed or 1)
            emit(1, { ["Move Horizontal"] = mh, ["Move Vertical"] = mv })
        end
        return tas
    end
	
	---------------- Held / Sticky movement keys 

    -- Sticky set raw axis movement
    function tas.hold_move(x, y)
		held_move.x, held_move.y = x or 0, y or 0
        return tas
    end

	-- Sticky version of move_dir
    function tas.hold_move_dir(deg, speed)
        return tas.hold_move(axes_for(deg, speed))
    end

	-- Clear both sticky move axes.
    function tas.release_move()
        return tas.hold_move(0, 0)
    end
	
	
	------------------ Locking movement
	-- The lock_* moving verbs below re-correct every frame until unlock_move().
    -- A lock stands down for any call that sets movement itself
    -- (forward/right/etc), then resumes.
	
	-- Lock movement to a world direction no matter the viewing angle. Same angle convention as look:
    -- 0 = +Z, 90 = +X. speed is a fraction of max, 0..1.
    function tas.lock_move(yaw, speed)
        need_game()
        move_lock = function() return game.move_axes_for(yaw or 0, speed or 1) end
        return tas
    end

    -- Lock to whatever direction you're facing right now, then keep going that
    -- way regardless of where you look afterwards.
    function tas.lock_move_forward(speed)
        need_game()
        return tas.lock_move(game.facing(), speed)
    end

    -- Walk at a world point, re-aiming every frame. Movement cuts out once
    -- within `tol` metres so you don't oscillate around it. y is ignored.
    function tas.lock_move_to_point(x, z, speed, tol)
        need_game()
        tol = tol or 0.25
        move_lock = function()
            local yaw = yaw_toward(x, z, tol)
            if not yaw then return 0, 0 end
            return game.move_axes_for(yaw, speed or 1)
        end
        return tas
    end

	-- Walk to a given object handle
    function tas.lock_move_at(handle, speed, tol)
        need_game()
        assert(handle, "lock_move_at: nil handle")
        tol = tol or 0.25
        move_lock = function()
            local tx, _, tz = game.target_pos(handle)
            local yaw = yaw_toward(tx, tz, tol)
            if not yaw then return 0, 0 end
            return game.move_axes_for(yaw, speed or 1)
        end
        return tas
    end

    function tas.unlock_move() move_lock = nil; return tas end
    
    ------------------------------------------------------------------ Looking / Mouse Movement

    -- Look axes are per-frame mouse deltas, so dx is applied on EACH of the
    -- specified number of frames. look_axis(2, 0, 30) turns by 60 units total, not 2.
    function tas.look_axis(dx, dy, frames)
        return emit(frames, {
            ["Look Horizontal"] = dx or 0,
            ["Look Vertical"] = dy or 0,
        })
    end

    -- Turn by a total of `yaw`/`pitch` DEGREES, spread evenly over `frames` frames.
	-- Relative to current facing; see look() for an absolute version
    function tas.turn(yaw, pitch, frames)
        frames = frames or 1
        assert(frames >= 1, "turn: frames must be >= 1")
        local sx, sy = look_scale()
        return tas.look_axis(((yaw or 0) / frames) / sx, ((pitch or 0) / frames) / sy, frames)
    end
	
	-- Aim `frames` of player motion ahead of where the eye is now. 0 restores the
    -- naive behaviour. Only affects aim_error*, so it feeds every look/lock verb.
    function tas.aim_lead(frames)
        need_game()
        game.set_aim_lead(frames or 0)
        return tas
    end

    -- Helper - Aim somewhere over a fix number of frames
    -- mouse movement is determined by the given error function
	-- based on where we are currently looking each frame
    local function sweep(frames, err_fn)
        frames = frames or 1
        assert(frames >= 1, "frames must be >= 1")
        local sx, sy = look_scale()
        for left = frames, 1, -1 do
            local yerr, perr = err_fn()
            tas.look_axis((yerr / left) / sx, (perr / left) / sy, 1)
        end
        return tas
    end

    -- Absolute: look at the exact world pitch and yaw no matter your current facing direction
    function tas.look(yaw, pitch, frames)
        need_game()
        return sweep(frames, function() return game.aim_error_dir(yaw or 0, pitch or 0) end)
    end

    -- look at a given handle from `nearest()`
    function tas.look_at(handle, frames)
        assert(handle, "look_at: no target handle")
        need_game()
        return sweep(frames, function() return game.aim_error(handle) end)
    end

    -- look at given world coordinates
    function tas.look_at_point(x, y, z, frames)
        need_game()
        return sweep(frames, function() return game.aim_error_point(x, y, z) end)
    end
	
	-- The lock_* aiming verbs below re-correct every frame until unlock_look().
    -- Pass `frames` to sweep onto the target first; without it the first locked
    -- frame snaps. A lock stands down for any call that sets look axes itself
    -- (turn/look_axis/spin), then resumes.

    -- lock view onto a specific pitch and yaw until unlocked
    function tas.lock_look(yaw, pitch, frames)
        need_game()
		if frames and frames >= 1 then
			tas.look(yaw, pitch, frames)
		end
        look_lock = function() return game.aim_error_dir(yaw or 0, pitch or 0) end
        return tas
    end

    -- lock view onto a specific object handle location until unlocked
    function tas.lock_look_at(handle, frames)
        need_game()
        assert(handle, "lock_look_at: nil handle")
		if frames and frames > 1 then
			tas.look_at(handle, frames)
		end
        look_lock = function() return game.aim_error(handle) end
        return tas
    end

    -- lock view onto a specific world point until unlocked
    function tas.lock_look_at_point(x, y, z, frames)
        need_game()
		if frames and frames > 1 then
			tas.look_at_point(x, y, z, frames)
		end
        look_lock = function() return game.aim_error_point(x, y, z) end
        return tas
    end

    function tas.unlock_look() look_lock = nil; return tas end
	
	----------------------------------------------- object rotation / spinning
	
	-- Degrees of world-up spin per 1.0 of Look Horizontal. Live, the game reports it
    -- (it knows your pitch, the y-axis-only flag and the invert pref). Offline you
    -- state your assumptions: opts.pitch (default 0, i.e. looking level) and
    -- opts.y_only. Negative because positive spin comes from negative Look Horizontal.
    local function spin_rate(opts)
        opts = opts or {}
        if game and not opts.offline then
            local s = game.spin_scale()
            assert(s ~= 0, "spin: no spinnable object grabbed")
            return s
        end
        if opts.y_only then return -Frame.SPIN_RATE_Y_ONLY end
        return -Frame.SPIN_RATE * math.cos(math.rad(opts.pitch or 0))
    end

    -- Spin the held object `deg` degrees about world up, over `frames` frames.
    function tas.spin(deg, frames, start_latch, end_latch, opts)
        frames = frames or 1
		if start_latch == nil then start_latch = false end
		if end_latch == nil then end_latch = false end
        assert(frames >= 1, "spin: frames must be >= 1")
        local per = ((deg or 0) / frames) / spin_rate(opts)

        local was_held = held["Rotate"]
        spinning = true
        tas.hold("Rotate")
        if start_latch then emit(1, { ["Look Horizontal"] = 0, ["Look Vertical"] = 0 }) end   -- latch skipUpdate
        emit(frames, { ["Look Horizontal"] = per, ["Look Vertical"] = 0 })
         if not was_held then
            tas.release("Rotate")
            if end_latch then emit(1, { ["Look Horizontal"] = 0, ["Look Vertical"] = 0 }) end
        end
        spinning = false
        return tas
    end
	
	-- Spin the held object to an absolute world yaw. 
    function tas.spin_to(yaw, frames, opts)
        need_game()
        local cur = game.grabbed_yaw()
		assert(cur ~= -999, "spin_to: nothing is held!")
        return tas.spin((((yaw or 0) - cur + 180) % 360) - 180, frames, opts)
    end

    ---------------------------------------------------------------- Buttons (Jump, Grab)

	-- Press and keep pressed until release(). Does not emit a frame by itself;
    -- future frames that emits carry it.
    function tas.hold(button)
        held[check_button(button)] = true
        return tas
    end

	-- Stop pressing. Takes effect on the next frame emitted.
    function tas.release(button)
        held[check_button(button)] = false
        return tas
    end

    -- Hold for specified number of frames (default 1), then release. Emits the frames itself.
    function tas.tap(button, frames)
        tas.hold(button)
        emit(frames or 1)
        return tas.release(button)
    end
    
	-- Grab is a TOGGLE, so grab() and ungrab() are the same single-frame tap;
    function tas.grab() return tas.tap("Grab") end
    function tas.ungrab() return tas.tap("Grab") end
    
	-- Jump for `frames` frames. Jumps held for longer go higher
    function tas.jump(frames) return tas.tap("Jump", frames) end
	
	-- Wait for the jump to be available, then take it.
    function tas.jump_when_ready(frames, max)
        need_game()
        tas.wait_can_jump(max)
        return tas.jump(frames)
    end
	
	-- Wait for the reticle to actually be on something grabbable, then take it.
    -- Grab is a toggle, so this refuses to fire while already holding something.
    function tas.grab_when_ready(max)
        need_game()
        assert(not game.is_grabbing(), "grab_when_ready: already holding something")
        tas.wait_until_grabbable(max)
        tas.grab()
        return tas.wait_until(function() return game.is_grabbing() end, 10)
    end

    --------------------------------------------------------------- playback

    -- Playback speed multiplier. Persists until the next speed() call.
    -- Applies to the next frame emitted, so call it before the wait/move.
    function tas.speed(multiplier)
        assert(multiplier and multiplier > 0, "speed multiplier must be positive")
        pending_speed = multiplier
        return tas
    end

    -- Reset checkpoint. `defer` determines if the frame is emitted immediately, or 
	-- the reset checkpoint is deferred (done on the next emitted frame)
    function tas.reset_checkpoint(defer)
        pending_reset = true
		if not defer then emit(1) end
        return tas
    end

    ---------------------------------------------------------------- escape hatch

    -- Raw access when no verb fits: tas.frame({ Jump = true }, 5)
    function tas.frame(overrides, frames)
        return emit(frames, overrides)
    end

	-- Frames emitted so far.
    function tas.frame_count()
        return sink:frame_count()
    end

    
    ------------------------------------------------------------ game state
	
	-- Player world position, as x, y, z.
    function tas.pos()
        need_game()
        return game.player_pos()
    end

    -- Camera facing, as yaw, pitch in degrees.
    function tas.facing()
        need_game()
        return game.facing()
    end

    -- Horizontal speed in units/s.
    function tas.player_speed()
        need_game()
        return game.speed()
    end

    -- True while something is in hand.
    function tas.is_grabbing()
        need_game()
        return game.is_grabbing()
    end

    -- Index of the last checkpoint the game registered.
    function tas.checkpoint()
        need_game()
        return game.checkpoint_index()
    end
	
	-- Print nearby grabbables (name + distance) to the log, nearest first.
	-- Use it to find what name to filter on: local h = tas.nearest("radio")
	function tas.grabbables(max)
		need_game()
		return game.list_grabbables(max or 15)
	end

    -- Nearest grabbable object, optionally filtered by name substring.
    -- Returns a handle you pass to look_at / grab helpers, or nil if none.
	-- `name` matches any part of the object name. if `name` starts with "=", then it must match exactly
    function tas.nearest(name, index)
        need_game()
        local h = game.nearest(name, index or 1)
        if h < 0 then return nil end
        return h
    end


    return tas
end

return Commands