-- The nail gun (Burner's Utilities): a game tool renamed, carrying the nails it drives as FSM values the mod reads.
local nail_gun = { fsm = { ItemName = { ItemName = "Nail Gun" } } }
nail_gun.playmaker = {
  NailGun = {
    variables = { float = {
      NailLength = 0.1,     -- m
      NailDiameter = 0.006, -- m
      NailShear = 9,        -- kN a nail holds before it shears
    } },
    states = { idle = {} },
  },
}
template "nail_gun" (nail_gun)
