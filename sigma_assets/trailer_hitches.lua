-- Two tow hitches to bolt onto a vehicle: a ball hitch for a car, and a truck's fifth wheel (the trailer can't roll on it).
local hitch = require "burners.hitch"

template "ball_hitch" (hitch.item { mass = 8 })
template "truck_hitch" (hitch.item { mass = 150, roll = "Locked" })
