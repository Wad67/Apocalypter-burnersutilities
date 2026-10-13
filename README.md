# Burner's Utilities

A BepInEx 5 plugin for Apocalypter: small things in one mod, each switched on and off on its own in the
settings.

## Ride Along

Standing on something that moves carries you with it. The game sets the player's walking velocity from the keys
alone, so a moving vehicle slides out from under you. Now the velocity of whatever you stand on, at your feet, is
added to yours (a turning vehicle takes you round with it). Jump off and you keep that speed until you land on
something that isn't moving. Footsteps and head bob go by your own steps, not the vehicle's travel.

## Ropes

### The kits

Two toolboxes of their own, made by SigmaMod from `BurnersUtilities/sigma_assets/` (the game's toolbox under
their own ids), so they turn up wherever the game has toolboxes and in Apocaspawner:

- **Rope Kit**: 12 mm polyester rope, 30 kN.
- **Steel Cable Kit**: 12 mm steel wire rope, 93 kN. Not in the starting kit: it is found and sold where the
  game's repair boxes are, a quarter as likely as anything else in those lists.

They work like the game's toolbox: look at one with an empty hand and press Use to take it out, Use on a kit
again (or X) to put it away. While a kit is out you can't pick things up, so a left click on a loose item ties
to it instead of grabbing it. Both are drawn with the game's own rope texture, which has a strand of fibre rope
and one of steel cable.

### Tying

With a kit out (instructions at the top left):

- **Left click** a point on anything, then a second point: a rope is tied between them at that length.
- **Right click** drops a rope you have started, or cuts the rope you aim at.
- **Mouse wheel** aimed at a rope pays it out or takes it in, 10 cm a notch.

A rope only pulls. It keeps its two ends from getting further apart than its length and lets them come
together, swing and turn, so it can tow a car, hang a load or tie something down. Tied between two things that
can't move, it just hangs there. Pulled harder than its breaking strength, it snaps. Slack, it hangs in a curve.

The rope follows what its ends become. Pick up a roped item and carry it, and whatever is on the other end is
dragged along behind you once the rope is tight; put it down and it is an ordinary roped thing again. Fit it to a
vehicle or lock it into a load bed and the rope is tied to the vehicle; two ends locked into the same vehicle
just hang between them.

Ropes are not saved yet: they are gone after loading a save.

### The rope table

`BepInEx/config/BurnersUtilities.ropes.cfg` says what each kit ties: one section per kit, named by the kit's ID
(its `ID` FSM, set in the kit's `.json`), with its name, breaking strength, thickness, and the game texture and
pixel rows it is drawn with. Another kit is another variant of the toolbox with its own ID and a section here.
Delete the file to get the shipped values back; edits apply to ropes tied after saving it.

## Trailer hitches

Two items, made by SigmaMod from `BurnersUtilities/sigma_assets/trailer_hitches.glb` and `.json`. They turn up
where loose car parts do (every spawn list that holds the game's bumpers or car doors, each hitch as likely as
one of those), and in Apocaspawner:

- **Ball Hitch**: the ball and drawbar the game's cars carry (about 8 kg).
- **Truck Hitch**: the Rustchief's fifth wheel (about 150 kg).

They bolt on like the game's plastic crate. Hold one against a vehicle and press Use to bolt it there with the
wrench. Like a wheel, it only comes off again with the toolbox. Bolted on, it works like a vehicle's own hitch. Aim at it to see whether a
trailer is in reach, and left click to hitch or unhitch. The trailer's coupler has to be within about 0.4 m of the
hitch, with the trailer behind it. The hitch's front is the drawbar's end, or the saddle's closed end.

A hitch bolts onto a trailer as well as onto a car, so trailers can be chained: bolt a hitch to the back of a
hitched trailer and hitch the next one to it.

On the ball the trailer turns any way. On the fifth wheel it turns on the kingpin and rocks fore and aft, but
can't roll on the saddle; how far it rocks is up to where the trailer meets the truck. Taking a hitch off unhitches
its trailer.

All of this is the items' own JSON (the game's crate and hitch FSMs), so there is no setting for it. Nothing saves
the hitching itself: if a trailer comes back loose after loading, hitch it again.

## Faster save loading

Easy Save, which the game saves with, finds a key in a save file by reading the file from the top and skipping
every key before it, and does that again for every key the game loads. A save of a few MB with a few hundred keys
is read hundreds of times over on every load.

This reads each save once and remembers where each key's entry is. When the game loads a key, Easy Save is handed
just that entry and reads it with its own code as before, so what comes out is the same. A key that isn't in the
save is answered at once. If the file changes (you save), it is read again. Only the game's plain save files are
handled this way; saving is untouched. Switch it off with `Saves.FastLoad` if you ever suspect it.

## Faster terrain texturing

The terrain generator (MapMagic) spends most of its time on one simple job: blending each terrain layer's weight
map into the terrain's texture map. It reads every layer through a slow interface, several times per layer per pixel,
hundreds of millions of times per tile. This does the same sums in the same order on plain arrays, so the terrain
comes out exactly the same (checked bit for bit), in a fraction of the time. `Terrain.FastTexturing` switches it off.

## Faster PlayMaker events

Much of the game runs on PlayMaker FSMs, and they often send an event to an object and everything under it. To find
who gets it, PlayMaker goes through every FSM in the world and checks, by walking up its parents, whether it sits
under that object. With a few thousand FSMs in a loaded world, the few hundred events sent as everything starts up
cost over a million of those walks: the seconds-per-frame stall just after a load. This asks the object for its own
FSMs instead, and does the same for events sent to one object's FSMs by name. The same FSMs get the event, in the
same order, with the same data. `PlayMaker.FastEvents` switches it off.

## Settings

`BepInEx/config/com.burner.apocalypter.burnersutilities.cfg`, or the Apocasetter Mods menu.

| Setting | Default | |
|---|---|---|
| `Ride Along.Enabled` | on | Standing on a moving vehicle carries you with it. |
| `Ropes.Enabled` | on | The rope kits and ropes. Off, the kits are inert toolboxes. |
| `Ropes.Reach` | 3 m | How far you can tie a rope. |
| `Ropes.PutAwayKey` | X | Key that puts the ropes away. |
| `Saves.FastLoad` | on | Read each save once and look keys up directly. Takes effect on the next load. |
| `Terrain.FastTexturing` | on | Faster terrain texture blending, same result. Takes effect on the next tile generated. |
| `PlayMaker.FastEvents` | on | Find who gets a PlayMaker event directly instead of searching every FSM. |
| `Debug.VerboseLog` | off | Extra diagnostics, and an FSM dump in `BepInEx/BurnersUtilities-fsm-dump.txt`. |
| `Debug.TerrainSeams` | off | Log how far terrain tiles disagree at their edges, in `BepInEx/BurnersUtilities-seams.csv`. |

## Requirements

SigmaMod (0.3.0 or later, for the trailer hitches' JSON shorthand and the rarer steel cable kit), for the kits and the hitches. Without it Ride Along still works.

## Install

Unzip the release into `Apocalypter/BepInEx/plugins`, so that `BepInEx/plugins/BurnersUtilities/BurnersUtilities.dll`
exists. Remove the older `RideAlong` and `RopeMod` folders if you had them.

## Build

```
dotnet build -c Release
```

Builds against the game's own assemblies (`../Apocalypter_Data/Managed`, `../BepInEx/core`) and deploys the
DLL and `sigma_assets` to `BepInEx/plugins/BurnersUtilities/`. Set `GameDir` if the repo is not in the game
folder.
