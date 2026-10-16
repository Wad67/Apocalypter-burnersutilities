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

Three toolboxes of their own, made by SigmaMod from `BurnersUtilities/sigma_assets/` (the game's toolbox under
their own ids), so they turn up wherever the game has toolboxes and in Apocaspawner:

- **Rope Kit**: 12 mm polyester rope, 30 kN.
- **Steel Cable Kit**: 12 mm steel wire rope, 93 kN. Not in the starting kit: it is found and sold where the
  game's repair boxes are, a quarter as likely as anything else in those lists.
- **Hose Kit**: 19 mm bore PVC hose, which tears at about 50 kg (see Hoses below). Found and sold where gasoline
  and diesel cans are, half as likely as anything else in those lists.

They work like the game's toolbox: look at one with an empty hand and press Use to take it out, Use on a kit
again (or X) to put it away. While a kit is out you can't pick things up, so a left click on a loose item ties
to it instead of grabbing it. Both are drawn with the game's own rope texture, which has a strand of fibre rope
and one of steel cable; the hose is plain black rubber.

### Tying

With a kit out (instructions at the top left):

- **Left click** a point on anything, then the other end: a rope is tied between them at that length.
- **Middle click**, before the other end, runs the rope over that point, as over a pulley. Any number of them.
- **Right click** drops a rope you have started, or cuts the rope you aim at.
- **Mouse wheel** aimed at a rope pays it out or takes it in, 10 cm a notch.

A rope only pulls. It keeps its length from being stretched and lets its ends come together, swing and turn, so
it can tow a car, hang a load or tie something down. It runs freely over the points in between, as over 1:1
pulleys: one tension all along it, and only its total length is held. Over a branch or a beam, it can hoist a
load by pulling the other end. Tied between things that can't move, it just hangs there. It stretches like a real
one, in proportion to the pull, up to its stretch at break (fibre rope 15%, steel cable 2%, hose 50%), so a hose
between a truck and its trailer rides out their swaying, and a tow rope takes up a jerk. It never pulls harder
than its breaking strength; asked to for more than a moment, it snaps. Slack, each run hangs in a curve.

Each point stays on the thing it was tied to and follows what that thing becomes. Pick up a roped item and the
rope pulls on it like on anything else (see Hands): whatever is on the other end is dragged along as hard as your
hands pull, and something heavier holds the item back at the end of the rope (held too far from your hand, you
let go of it). Put it down and it is an ordinary roped thing again. Fit it to a vehicle or lock it into a load bed and the rope pulls on the vehicle; take it out again
and the rope comes with it.

Ropes are not saved yet: they are gone after loading a save.

### Hoses

A hose is tied like a rope and behaves like one, a weak one, but it also carries liquid between its two ends.
Tie each end to a liquid container: a can, a barrel, a dew collector, a vehicle's tank. An end tied to the thing
a container sits in (a dew collector, around its water) goes onto that container, at its top; with more than one
in it, the one nearest where you tied. If both hold the same liquid (gasoline, diesel, motor oil, water), it runs
downhill through the hose: out of the container whose surface is higher, until that one is empty, the other is
full, or its surface has come down to the far end of the hose. Raise one end to start it, lower it to stop it.
The hose counts as primed.

How fast is the pipe flow for that drop through that length of hose: about half a litre a second for fuel or
water with a metre of drop through 3 m of hose, less through a longer one, and a trickle for motor oil, which is
some 150 times thicker. A surface is estimated from the container's shape, as far up it as it is full. While it
runs you hear the game's own pouring sound for that liquid at the outlet, as loud as what it carries (full
at a litre a second; a plant or radiator topped up a few millilitres at a time is barely heard). With a kit out, aim at a hose to see
what it carries and how fast.

Hoses aren't saved either.

### The rope table

`BepInEx/config/BurnersUtilities.ropes.cfg` says what each kit ties: one section per kit, named by the kit's ID
(its `ID` FSM, set in the kit's `.lua`), with its name, breaking strength, thickness, and the game texture and
pixel rows it is drawn with (or a plain colour), and its `stretch` at break. A kit with a `bore` ties hoses.
`[liquid: <tag>]` sections give each liquid a hose carries, by the tag the game gives its containers, a viscosity
and the sound it runs with. Another kit is another variant of the toolbox with its own ID and a section here.
Delete the file to get the shipped values back; edits apply to ropes tied after saving it. A file in an older
format is kept as `.old` and replaced.

With `Debug.VerboseLog` on, the log has every rope and hose tied (its points, what each pulls on, its stiffness),
cut, snapped (with its pull, stretch and points) or gone with a thing it was tied to, its points changing body,
and what each hose is doing (its containers, surfaces, amounts and flow, as that changes and every 10 s while it
flows).

## Hands

The game holds a grabbed thing by putting it in front of you every frame, its motion wiped: nothing it is tied,
nailed or jointed to can hold it back, and they fight it, shaking. Here your hands hold it by force instead. The
game's grab does everything else as before (the prompt, scrolling it nearer or further, turning it, dropping and
throwing it), but the thing hangs on your hand as on a spring with a damper (`Hands.Stiffness`, 1500 N/m), carried
along with the hand's motion and easing into place, and turned the same way. It keeps its weight, and your hands
carry it: a heavy part presses you down, and whatever you stand on (the bed of a sprung trailer sinks). Over about
120 kg it is more than `Hands.Strength` can carry, and you drop it.

**Heaving:** keep the left button held after the click that takes hold, and you go past your strength, up to
`Hands.HeaveStrength` (5500 N: just enough for a 500 kg part, the heaviest in the game), at a price. Everything
past your strength comes off your health, armour or not (`Hands.StrainDamage`, 2 health a second for each kN
over), and you grunt and groan, more often and louder the harder it is. Holding a 200 kg part costs about 1.5
health a second; a 500 kg one about 7.5, dead in under 15 seconds. Let the button go and you are back to your own
strength: still too heavy, you drop it. It is your arms and back that strain, whatever for: swinging a part hard,
or dragging a car, hurts as much as lifting. Weight shows: moved 30 cm, a 1 kg
thing is there in 0.08 s, 5 kg in 0.2 s, 20 kg in a third of a second, 60 kg in 0.6 s, each swinging a little
past. A held thing rests on and slides along the ground and buildings (the game lets it pass through everything),
but still passes through anything with a body of its own: vehicles (so parts still go in), other things, you. A
dropped or thrown thing keeps the motion your hand gave it. A thing taken out of a load bed gets its body
when you grab it (the game would give it one when you drop it), as heavy as the game makes it.

**Anything with a body that moves** can be grabbed the same way, not only the game's items: a car, a trailer, a
part. Look at it within 2 m with empty hands (the game's "grab: lmb" shows) and left click: you hold it by that
point, at that distance (scroll: nearer or further), and it turns about your grip as it will. Left click lets go.
Where the game does something with a click (a door, a handle, a control: anything listening for the mouse or for
Use, in front of the body or on it), the click is the game's, not a grab. So is a click on another mod's control that
takes clicks the Unity way (`OnMouseDown`: SigmaMod's levers and switches, Engine Sim's foot crank).

**Your arm has a reach** (60 cm). Within it the hold gives; past it your arm is straight, and whatever pulls the
thing pulls you. Every pull your hands make pulls back on you, as on an 80 kg body on its feet, and your feet push
on what you stand on: your feet hold up to three quarters of your weight (about 590 N), and what you stand on takes
the push, so you can hold a car that rolls gently; pulled harder, you are dragged, sliding, and only your feet's
friction goes into what is under them. Pulled up, you are lighter on your feet (and they grip less); pulled up
harder than your weight, you leave the ground, and can pull yourself up on something fixed. The hold feels how
heavy the thing really is at your grip (a lamp tied to a structure is as heavy as the structure), so hanging from
it, it is you that moves. Seated in a vehicle, the vehicle takes all of it. So you can't pull along the thing you
stand or sit on: it pulls against itself, and at most rocks. Hang on to a car that drives off and you are towed
behind it. Your hands never pull harder than `Hands.Strength` (1200 N, about what a grip holds); asked for more for
a quarter of a second, they let go. A roped or nailed thing held fast does the same: it holds you back at arm's
length, or you let go.

`Hands.HoldByForce` switches back to the game's own holding (and no grabbing of other bodies, and no pulling).

## Nail gun

The game's .22 pipe revolver under its own id, made by SigmaMod from `BurnersUtilities/sigma_assets/nail_gun.lua`:
the **Nail Gun**. Found and sold where repair boxes are, half as likely as anything else there, and in Apocaspawner.
Put it in a weapon slot and take it out like the revolver; it has the revolver's arms and model, but it doesn't
shoot, reload or aim.

- **Left click** fires a nail where you aim, within 2 m.
- **Right click** pulls out the nail whose head you aim at.

A nail goes in along your aim: through the first thing and on into whatever is behind it, as far as its length
reaches (it has to bite at least 5 mm). Something behind with no body of its own, a wall or the ground, holds it
as the world does, so you can nail things down. A nail that doesn't come out the far side, or meets nothing
behind, is only stuck in the first thing.

- **One nail**, or nails all on one line: a pivot. The two things turn freely about the nail, like an axle.
- **Two or more** otherwise: fixed together, as welded or bolted.

Nails hold as hard as they are strong: each takes its shear strength (9 kN for the shipped 100 x 6 mm spikes),
and twisting is resisted by a single nail's bending or by several nails' pull across their spread. Pulled
harder, all the nails between the two things tear out. Pull one out and the rest hold as they now can.

Nails stay in what they went into and follow what it becomes: locked into a load bed, fitted, or picked up, they
hold between whatever bodies it is part of then (two things on one body need nothing). Grab a nailed thing and
your hands pull on its nails like on anything else (see Hands): nailed to something light, the light thing comes
along; nailed to something fixed or too heavy, it stays where it is and you let go of it, unless you pull harder
than the nails hold.

The nails are in the item's own `NailGun` FSM in its `.lua` (`NailLength`, `NailDiameter` in m, `NailShear` in
kN). Nails are not saved yet.

## Trailer hitches

Two items, made by SigmaMod from `BurnersUtilities/sigma_assets/trailer_hitches.glb` and `.lua` (the hitch itself is `burners/hitch.lua`, a Lua library other assets can use). They turn up
where loose car parts do (every spawn list that holds the game's bumpers or car doors, each hitch as likely as
one of those), and in Apocaspawner:

- **Ball Hitch**: the ball and drawbar the game's cars carry (about 8 kg).
- **Truck Hitch**: the Rustchief's fifth wheel (about 150 kg).

They bolt on like the game's plastic crate. Hold one against a vehicle and press Use to bolt it there with the
wrench. Like a wheel, it only comes off again with the toolbox (the game's, or Ultimate Tools' wrench). Bolted on, it works like a vehicle's own hitch. Aim at it to see whether a
trailer is in reach, and left click to hitch or unhitch. The trailer's coupler has to be within about 0.4 m of the
hitch, with the trailer behind it. The hitch's front is the drawbar's end, or the saddle's closed end.

A hitch bolts onto a trailer as well as onto a car, so trailers can be chained: bolt a hitch to the back of a
hitched trailer and hitch the next one to it.

On the ball the trailer turns any way. On the fifth wheel it turns on the kingpin and rocks fore and aft, but
can't roll on the saddle; how far it rocks is up to where the trailer meets the truck. Taking a hitch off unhitches
its trailer.

All of this is the items' own JSON (the game's crate and hitch FSMs), so there is no setting for it. Nothing saves
the hitching itself: if a trailer comes back loose after loading, hitch it again.

## Lock volumes inside lock volumes

The game locks a loose item into a lock volume (a load bed, a crate) it sits in, every few seconds and on loading
a save. With one volume inside another, a crate in a trailer, it took the outer one: the crate's contents were
locked into the trailer, and stayed there when the crate came out. Items now lock into the innermost volume they
are in, not counting their own. `Physlock.InnermostVolume` switches it off. Items already moved into the trailer go
back into the crate the next time they lock (on loading the save).

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
| `Ropes.Enabled` | on | The rope and hose kits. Off, the kits are inert toolboxes. |
| `Ropes.Reach` | 3 m | How far you can tie a rope. |
| `Ropes.PutAwayKey` | X | Key that puts the ropes away. |
| `Hands.HoldByForce` | on | Hold grabbed things by force, so what they are tied or nailed to pulls back. |
| `Hands.Strength` | 1200 N | The most your hands pull or push with; more than that and they let go. |
| `Hands.HeaveStrength` | 5500 N | The most you give while heaving (the button held after taking hold). |
| `Hands.StrainDamage` | 2 | Health lost a second for each kN heaved past your strength. |
| `Hands.Stiffness` | 1500 N/m | How firmly your hands hold a thing where it is held. Lower: heavier, softer. |
| `Nails.Enabled` | on | The nail gun. Off, it is a plain .22 revolver. |
| `Physlock.InnermostVolume` | on | Items lock into the innermost lock volume they are in. |
| `Saves.FastLoad` | on | Read each save once and look keys up directly. Takes effect on the next load. |
| `Terrain.FastTexturing` | on | Faster terrain texture blending, same result. Takes effect on the next tile generated. |
| `PlayMaker.FastEvents` | on | Find who gets a PlayMaker event directly instead of searching every FSM. |
| `Debug.VerboseLog` | off | Extra diagnostics, and an FSM dump in `BepInEx/BurnersUtilities-fsm-dump.txt`. |
| `Debug.TerrainSeams` | off | Log how far terrain tiles disagree at their edges, in `BepInEx/BurnersUtilities-seams.csv`. |

## Requirements

SigmaMod (2.0 or later: the items are Lua assets), for the rope, cable and hose kits, the nail gun and the trailer hitches. Without it the
rest still works: Ride Along, the hands, the physlock fix and the speedups.

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
