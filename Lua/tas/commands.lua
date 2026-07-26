-- The tas.* verbs. Every one of these is a thin wrapper over emit(),
-- which is the only place that touches the sink.

local Frame = require("tas.frame")

local Commands = {}

function Commands.build(sink, game)
    local tas = { sink = sink, game = game }

    -- Sticky button state: hold() sets it, release() clears it, and every
    -- frame emitted in between carries it.
    local held = {}
    for _, name in ipairs(Frame.BUTTONS) do
        held[name] = false
    end
    
    local sticky_axes = {}
    for _, axis in ipairs(Frame.AXES) do
	sticky_axes[axis] = 0
    end

	-- function for locking move to a world direction. recomputed per frame to account for camera movement
	local move_lock = nil

    -- function for constant camera looking at a certain point (locked onto)
    local look_lock = nil
	
	-- True while a spin is in flight. The camera is frozen during Rotate, so any
	-- look_lock has to stand down or it winds up against an error that can't shrink.
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
	
	-- FPSInputController normalizes the move vector then SQUARES its magnitude,
    -- so asking for fraction `s` of max speed means emitting sqrt(s).
    local function magnitude_for(speed)
        speed = speed or 1
        if speed <= 0 then return 0 end
        if speed > 1 then speed = 1 end
        return math.sqrt(speed)
    end
	
	-- Camera-relative axes for an angle off your current facing.
    -- 0 = forward, 90 = right. speed is a fraction of max, 0..1.
    local function axes_for(deg, speed)
        local r = math.rad(deg or 0)
        local m = magnitude_for(speed)
        return math.sin(r) * m, math.cos(r) * m
    end

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

    -- The single primitive. Builds one frame from sticky state + overrides
    -- and hands it to the sink `count` times.
    local function emit(count, overrides)
        count = count or 1
        assert(count >= 0, "frame count must not be negative")
        if count == 0 then return tas end

        local f = Frame.blank()
        for button, value in pairs(held) do f[button] = value end
	for axis, value in pairs(sticky_axes) do f[axis] = value end
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

        if count > 0 then
            -- An explicit look/move this frame takes priority over its lock
            local manual_look = overrides and (overrides["Look Horizontal"] or overrides["Look Vertical"])
			local manual_move = overrides and (overrides["Move Horizontal"] or overrides["Move Vertical"])
			local live_look = look_lock and not manual_look and not spinning
			local live_move = move_lock and not manual_move
			
            if live_look or live_move then
                -- we have a current lock; go one frame at a time and constantly correct look/move angles
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
            else
                sink:push(f, count)
            end
        end
		
        return tas
    end

    local function check_button(name)
        assert(Frame.is_button(name),
            "no such button: " .. tostring(name) ..
            " (expected Jump, Grab or Rotate)")
        return name
    end

    ---------------------------------------------------------------- metadata

    -- checkpoint defaults to -1, meaning "start of level".
    function tas.level(id, checkpoint)
        sink:set_level(id, checkpoint or -1)
        return tas
    end

    ------------------------------------------------------------------- time/waits

    function tas.wait(frames)
        return emit(frames)
    end
	
	-- Emit blank frames (keeping held buttons) until pred() is true, or until
    -- max frames pass. pred is a Lua function returning a boolean.
    function tas.wait_until(pred, max)
        need_game()
        max = max or 600                                        -- 12s safety cap so a bad predicate can't hang the game
        for _ = 1, max do
            if pred() then return tas end
            emit(1)                                                        -- one frame, then yields back to the game
        end
		error("wait_until: predicate never came true within max time frame. (" .. max .. " frames")
        return tas
    end

    function tas.wait_until_grounded(max)
        return tas.wait_until(function() return game.grounded() end, max)
    end

    function tas.wait_until_airborne(max)
        return tas.wait_until(function() return not game.grounded() end, max)
    end
	
	-- Wait until a Jump press will actually launch: grounded and off cooldown.
    function tas.wait_can_jump(max)
        need_game()
        return tas.wait_until(function() return game.can_jump() end, max)
    end

    -- Wait for the jump to be available, then take it. `frames` is how long Jump
    -- is held — the motor adds height for as long as you hold it (jumping.extraHeight),
    -- so 1 is a short hop and ~25 is a full one.
    function tas.jump_when_ready(frames, max)
        need_game()
        tas.wait_can_jump(max)
        return tas.jump(frames)
    end
	
	function tas.wait_until_grabbable(max)
        need_game()
        return tas.wait_until(function() return game.is_ready_to_grab() end, max)
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
	
	function tas.wait_until_above(axis, value, max)
        need_game()
		max = max or 1500
        return tas.wait_until(function() return pos_axis(axis) > value end, max)
    end

    function tas.wait_until_below(axis, value, max)
        need_game()
		max = max or 1500
        return tas.wait_until(function() return pos_axis(axis) < value end, max)
    end

    -- Wait until an axis crosses `value`, picking the direction from where you
    -- are when it's called. Usually what you mean: "walk until z reaches 18.5".
    function tas.wait_until_pos(axis, value, max)
        need_game()
        if pos_axis(axis) < value then
            return tas.wait_until_above(axis, value, max)
        end
        return tas.wait_until_below(axis, value, max)
    end

    -- Horizontal distance to a world point; y ignored, matching the move locks.
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

    -- x = strafe (-1 left, 1 right), y = forward/back (1 forward).
    function tas.move(x, y, frames)
        return emit(frames, {
            ["Move Horizontal"] = x or 0,
            ["Move Vertical"] = y or 0,
        })
    end

    function tas.forward(speed, frames)
		if frames then return tas.move(0, speed, frames or 1)
		else return tas.move(0, 1, speed or 1)
		end
	end
    function tas.hold_forward(speed)
		sticky_axes["Move Vertical"] = speed or 1
		return tas
    end
    function tas.release_forward()
		sticky_axes["Move Vertical"] = 0
		return tas
    end
    
    function tas.back(speed, frames)
		if frames then return tas.move(0, -speed, frames or 1)
		else return tas.move(0, -1, speed or 1)
		end
	end
    function tas.hold_back(speed)
		sticky_axes["Move Vertical"] = -(speed or 1)
		return tas
    end
    function tas.release_back()
		sticky_axes["Move Vertical"] = 0
		return tas
    end
    
    function tas.left(speed, frames)
		if frames then return tas.move(-speed, 0, frames or 1)
		else return tas.move(-1, 0, speed or 1)
		end
	end
    function tas.hold_left(speed)
		sticky_axes["Move Horizontal"] = -(speed or 1)
		return tas
    end
    function tas.release_left()
		sticky_axes["Move Horizontal"] = 0
		return tas
    end
    
    function tas.right(speed, frames)
		if frames then return tas.move(speed, 0, frames or 1)
		else return tas.move(1, 0, speed or 1)
		end
	end
    function tas.hold_right(speed)
		sticky_axes["Move Horizontal"] = speed or 1
		return tas
    end
    function tas.release_right()
		sticky_axes["Move Horizontal"] = 0
		return tas
    end
	
	
	------------------------------------------------------- analog movement

    

    -- Move at an arbitrary angle relative to current facing.
    -- speed is a fraction of the max speed for that heading, 0..1.
    function tas.move_dir(deg, speed, frames)
        local x, y = axes_for(deg, speed)
        return tas.move(x, y, frames)
    end

    -- Sticky versions. The per-axis hold_* verbs already compose into a
    -- diagonal, but these save you doing the trig at every call site.
    function tas.hold_move(x, y)
        sticky_axes["Move Horizontal"] = x or 0
        sticky_axes["Move Vertical"]   = y or 0
        return tas
    end

    function tas.hold_move_dir(deg, speed)
        return tas.hold_move(axes_for(deg, speed))
    end

    function tas.release_move()
        return tas.hold_move(0, 0)
    end
    
    ------------------------------------------------------------------ mouse movement (offline)

    -- Look axes are per-frame mouse deltas, so dx is applied on EACH of the
    -- specified number of frames. look(2, 0, 30) turns by 60 units total, not 2.
    function tas.look_axis(dx, dy, frames)
        return emit(frames, {
            ["Look Horizontal"] = dx or 0,
            ["Look Vertical"] = dy or 0,
        })
    end

    -- Spread a total turn evenly over specified number of frames, which is usually what you want.
    function tas.turn(yaw, pitch, frames)
        frames = frames or 1
        assert(frames >= 1, "turn: frames must be >= 1")
        local sx, sy = look_scale()
        return tas.look_axis(((yaw or 0) / frames) / sx, ((pitch or 0) / frames) / sy, frames)
    end
	
	----------------------------------------------- rotation (offline)
	
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
    function tas.spin(deg, frames, opts)
        frames = frames or 1
        assert(frames >= 1, "spin: frames must be >= 1")
        local per = ((deg or 0) / frames) / spin_rate(opts)

        local was_held = held["Rotate"]
        spinning = true
        tas.hold("Rotate")
        -- emit(1, { ["Look Horizontal"] = 0, ["Look Vertical"] = 0 })   -- latch skipUpdate
        emit(frames, { ["Look Horizontal"] = per, ["Look Vertical"] = 0 })
         if not was_held then
            tas.release("Rotate")
            --emit(1, { ["Look Horizontal"] = 0, ["Look Vertical"] = 0 })
        end
        spinning = false
        return tas
    end

    ---------------------------------------------------------------- buttons

    function tas.hold(button)
        held[check_button(button)] = true
        return tas
    end

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
    
    function tas.grab()
	return tas.tap("Grab")
    end
    
    function tas.ungrab()
	return tas.tap("Grab")
    end
    
    function tas.jump(frames)
	return tas.tap("Jump", frames)
    end

    --------------------------------------------------------------- playback

    -- Playback speed multiplier. Persists until the next speed() call.
    -- Applies to the next frame emitted, so call it before the wait/move.
    function tas.speed(multiplier)
        assert(multiplier and multiplier > 0, "speed multiplier must be positive")
        pending_speed = multiplier
        return tas
    end

    -- Ask the mod to reload the checkpoint on the next frame emitted.
    function tas.reset_checkpoint()
        pending_reset = true
		if game then emit(1) end
        return tas
    end

    ---------------------------------------------------------------- escape hatch

    -- Raw access when no verb fits: tas.frame({ Jump = true }, 5)
    function tas.frame(overrides, frames)
        return emit(frames, overrides)
    end

    function tas.frame_count()
        return sink:frame_count()
    end

    ------------------------------------------------------------------- output

    -- CSV-only. A live sink has no save(), which is the correct error there.
    function tas.save(path)
        assert(sink.save, "this sink does not support save()")
        assert(not pending_speed and not pending_reset,
            "speed()/reset_checkpoint() was called but no frame followed it")
        return sink:save(path)
    end
	
	-- Declare an auto-save target. The host calls __tas_autosave when playback
    -- ends -- script finished, level finished, or a manual stop. That's the only
    -- way to catch level end: the coroutine is never resumed past the scene swap.
    local auto_path = nil

    function tas.record(path)
        assert(sink.save, "this sink does not support save()")
        auto_path = path
        return tas
    end

    __tas_autosave = function()
        local path = auto_path
        auto_path = nil                       -- one save per run
        if not path or not sink.save then return end
        if sink:frame_count() == 0 then return end
        print("saved " .. sink:save(path))
    end
    
    
    
    ------------------------------------------------------------ live-only
	
	
	-- Jump straight to a checkpoint. Free -- unlike reset_checkpoint(), which
    -- reloads the scene. Use it to skip the parts of a run you're not working on.
    function tas.warp_to_checkpoint(index)
        need_game()
        game.warp_to_checkpoint(index)
        emit(1)                          -- warp lands at the end of this frame
        return tas.wait_until_grounded(60)
    end

    --------------------------- mouse movement (live)
	
	-- Aim `frames` of player motion ahead of where the eye is now. 0 restores the
    -- naive behaviour. Only affects aim_error*, so it feeds every look/lock verb.
    function tas.aim_lead(frames)
        need_game()
        game.set_aim_lead(frames or 0)
        return tas
    end

    -- Print nearby grabbables (name + distance) to the log, nearest first.
      -- Use it to find what name to filter on: local h = tas.nearest("radio")
      function tas.grabbables(max)
        need_game()
        return game.list_grabbables(max or 15)
      end

    -- Nearest grabbable object, optionally filtered by name substring.
    -- Returns a handle you pass to look_at / grab helpers, or nil if none.
    function tas.nearest(name, index)
        need_game()
        local h = game.nearest(name, index or 1)
        if h < 0 then return nil end
        return h
    end

    -- aim somewhere over a fix number of frames
    -- mouse movement is determined by the given error function
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

    -- absolute: look at the exact pitch and yaw no matter your current facing direction
    function tas.look(yaw, pitch, frames)
        need_game()
        return sweep(frames, function() return game.aim_error_dir(yaw or 0, pitch or 0) end)
    end

    -- look at a given handle
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

    -- lock view onto a specific pitch and yaw until unlocked
    function tas.lock_look(yaw, pitch, frames)
        need_game()
		if frames and frames > 1 then
			tas.look(yaw, pitch, frames)
		end
        look_lock = function() return game.aim_error_dir(yaw or 0, pitch or 0) end
        return tas
    end

    -- lock view onto a specific handle location until unlocked
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
	
	
	-------------------------------------------------- object spinning (live)

    -- Absolute world yaw for the held object -- needs to know where it is now.
    function tas.spin_to(yaw, frames, opts)
        need_game()
        local cur = game.grabbed_yaw()
        return tas.spin((((yaw or 0) - cur + 180) % 360) - 180, frames, opts)
    end
	
	
	------------------------------------ world-relative movement (live)

    -- Lock movement to a world direction. Same angle convention as look:
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

    return tas
end

return Commands