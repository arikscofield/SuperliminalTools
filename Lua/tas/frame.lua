-- Data model of each frame of the tas. Should match the csv format exactly

local Frame = {}

Frame.AXES = {
    "Move Horizontal",
    "Move Vertical",
    "Look Horizontal",
    "Look Vertical",
}

Frame.BUTTONS = { "Jump", "Grab", "Rotate" }

Frame.RESET = "Reset Checkpoint"
Frame.SPEED = "Speed"

-- Define the columsn headers
Frame.COLUMNS = {}
for _, name in ipairs(Frame.AXES) do
	Frame.COLUMNS[#Frame.COLUMNS + 1] = name
end
for _, name in ipairs(Frame.BUTTONS) do
	Frame.COLUMNS[#Frame.COLUMNS + 1] = name
end
Frame.COLUMNS[#Frame.COLUMNS + 1] = Frame.RESET
Frame.COLUMNS[#Frame.COLUMNS + 1] = Frame.SPEED


-- True if `name` is a real button. used for error messages
function Frame.is_button(name)
	for _, b in ipairs(Frame.BUTTONS) do
		if b == name then return true end
	end
	return false
end



-- A new/blank frame with all columns at its neutral value
function Frame.blank()
    local f = {}
    for _, axis in ipairs(Frame.AXES) do
        f[axis] = 0.0
    end
    for _, button in ipairs(Frame.BUTTONS) do
        f[button] = false
    end
    f[Frame.RESET] = false
    f[Frame.SPEED] = nil
    return f
end

return Frame