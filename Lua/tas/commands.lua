-- The tas.* verbs. Every one of these is a thin wrapper over emit(),
-- which is the only place that touches the sink.

local Frame = require("tas.frame")
local clamp

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

  -- One-shot values that attach to the next frame emitted, then clear.
  local pending_speed = nil
  local pending_reset = false

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
      sink:push(f, count)
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

  ------------------------------------------------------------------- time

  function tas.wait(n)
    return emit(n)
  end

  ---------------------------------------------------------------- movement

  -- x = strafe (-1 left, 1 right), y = forward/back (1 forward).
  function tas.move(x, y, n)
    return emit(n, {
      ["Move Horizontal"] = x or 0,
      ["Move Vertical"] = y or 0,
    })
  end

  function tas.forward(v, n) return tas.move(0, v, n) end
  function tas.hold_forward(v)
	sticky_axes["Move Vertical"] = v or 1
	return tas
  end
  function tas.release_forward()
	sticky_axes["Move Vertical"] = 0
	return tas
  end
  
  function tas.back(v, n)    return tas.move(0, -v, n) end
  function tas.hold_back(v)
	sticky_axes["Move Vertical"] = -(v or 1)
	return tas
  end
  function tas.release_back()
	sticky_axes["Move Vertical"] = 0
	return tas
  end
  
  function tas.left(v, n)    return tas.move(-v, 0, n) end
  function tas.hold_left(v)
	sticky_axes["Move Horizontal"] = -(v or 1)
	return tas
  end
  function tas.release_left()
	sticky_axes["Move Horizontal"] = 0
	return tas
  end
  
  function tas.right(v, n)   return tas.move(v, 0, n) end
  function tas.hold_right(v)
	sticky_axes["Move Horizontal"] = v or 1
	return tas
  end
  function tas.release_right()
	sticky_axes["Move Horizontal"] = 0
	return tas
  end
  
  -- Sticky-hold axis directions


  -- Look axes are per-frame mouse deltas, so dx is applied on EACH of the
  -- n frames. look(2, 0, 30) turns by 60 units total, not 2.
  function tas.look(dx, dy, n)
    return emit(n, {
      ["Look Horizontal"] = dx or 0,
      ["Look Vertical"] = dy or 0,
    })
  end

  -- Spread a total turn evenly over n frames, which is usually what you want.
  function tas.turn(total_x, total_y, n)
    n = n or 1
    return tas.look((total_x or 0) / n, (total_y or 0) / n, n)
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

  -- Hold for n frames (default 1), then release. Emits the frames itself.
  function tas.tap(button, n)
    tas.hold(button)
    emit(n or 1)
    return tas.release(button)
  end
  
  function tas.grab(n)
	return tas.tap("Grab")
  end
  
  function tas.ungrab(n)
	return tas.tap("Grab")
  end
  
  function tas.jump(n)
	return tas.tap("Jump", n)
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
    return tas
  end

  ---------------------------------------------------------------- escape hatch

  -- Raw access when no verb fits: tas.frame({ Jump = true }, 5)
  function tas.frame(overrides, n)
    return emit(n, overrides)
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
  
  
  
  ------------------------------------------------------------ live-only

  -- Everything below needs the running game. Offline it errors clearly.
  local function need_game()
    assert(game, "this command only works live in-game (needs the game bridge)")
    return game
  end

  -- Emit blank frames (keeping held buttons) until pred() is true, or until
  -- max frames pass. pred is a Lua function returning a boolean.
  function tas.wait_until(pred, max)
    need_game()
    max = max or 600                    -- 12s safety cap so a bad predicate can't hang the game
    for _ = 1, max do
      if pred() then return tas end
      emit(1)                            -- one frame, then yields back to the game
    end
    return tas
  end

  function tas.wait_until_grounded(max)
    return tas.wait_until(function() return game.grounded() end, max)
  end

  function tas.wait_until_airborne(max)
    return tas.wait_until(function() return not game.grounded() end, max)
  end

  -- Wait until the game registers a new checkpoint (index goes up).
  function tas.wait_until_checkpoint(max)
    need_game()
    local start = game.checkpoint_index()
    return tas.wait_until(function() return game.checkpoint_index() > start end, max)
  end

  -- Nearest grabbable object, optionally filtered by name substring.
  -- Returns a handle you pass to look_at / grab helpers, or nil if none.
  function tas.nearest(name)
    need_game()
    local h = game.nearest(name)
    if h < 0 then return nil end
    return h
  end

  -- The core aim loop: err_fn() returns (yaw_err, pitch_err) in degrees; we
  -- feed the Look axes proportionally until both are within tolerance.
  -- If aiming turns the WRONG way, flip opts.yaw_sign / opts.pitch_sign.
  local function aim(err_fn, opts)
    need_game()
    opts = opts or {}
    local gain      = opts.gain or 0.6
    local tol       = opts.tolerance or 0.5     -- degrees
    local max_step  = opts.max_step or 15       -- max look per frame
    local budget    = opts.max_frames or 300
    local ysign     = opts.yaw_sign or 1
    local psign     = opts.pitch_sign or 1

    for _ = 1, budget do
      local yerr, perr = err_fn()
      if math.abs(yerr) <= tol and math.abs(perr) <= tol then
        return tas
      end
      tas.look(
        clamp(ysign * gain * yerr, -max_step, max_step),
        clamp(psign * gain * perr, -max_step, max_step),
        1)                                       -- one frame per step
    end
    return tas
  end

  -- Turn to face a target handle (tracks it if it moves).
  function tas.look_at(handle, opts)
    assert(handle, "look_at: no target (nearest() returned nil?)")
    return aim(function() return game.aim_error(handle) end, opts)
  end

  -- Turn to face an absolute world point.
  function tas.look_at_point(x, y, z, opts)
    return aim(function() return game.aim_error_point(x, y, z) end, opts)
  end

  -- Turn to an absolute world direction: yaw 0 = +Z, pitch 0 = horizon, +up.
  function tas.look_absolute(yaw, pitch, opts)
    return aim(function() return game.aim_error_dir(yaw, pitch or 0) end, opts)
  end

  return tas
end


-- Small numeric clamp, shared by the aim loop.
function clamp(v, lo, hi)
  if v < lo then return lo elseif v > hi then return hi else return v end
end

return Commands