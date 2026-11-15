# The 3D neighbourhood

Simitone can draw a neighbourhood as one connected 3D world — generated terrain, roads, water
and trees, with every lot standing in it — instead of the hand-painted map the game has always
shown.

It is **off by default**. To turn it on, go to *Options → Graphics → Neighbourhood View* and
choose **3D**.

- **2D** (the default) is the painted map, unchanged.
- **3D** is the generated world, seen from above. Choosing a house, the neighbourhood buttons and
  the zoom all work exactly as before; the painted map's little animations are hidden, because
  over a 3D world they would be floating labels rather than water.

If the world cannot be built — a damaged house file, a neighbourhood that will not fit on the
map, or a graphics card that will not take the picture — the neighbourhood opens as the painted
map anyway, and the reason is written to the log. The neighbourhood never fails to open because
of this.

Two separate settings, on purpose:

- **Neighbourhood View** is the map you pick a house from.
- **Surrounding Lots** is how much world is drawn *around the lot you are standing in*. Turning
  it off leaves a lot looking exactly as it always has.

**Editing a lot is remembered.** Bulldoze a house, rezone a lot, build something new — the next
time you return to the neighbourhood only the lot you changed is rebuilt, and the rest of the
street stays where you left it.

**Cost, and what "cached" means.** The first time you open a neighbourhood in 3D, Simitone reads
every lot in it and builds the world, which takes a moment. After that it is cached **twice over**,
and the second cache is the one that matters between sessions:

- **Within a session**, the finished world is held in memory, so coming back to the same
  neighbourhood is immediate.
- **Between sessions**, the generated map itself is written to
  `Neighbourhoods/generated/<slot>.raster` next to the layout sidecar — about three megabytes of
  the map's own six arrays. Generating it is several seconds of road layout, ground filling and
  seam solving, and the file means the second launch costs a file read rather than a regeneration.

The file is only ever used when it is stamped for *exactly* the neighbourhood being asked about —
same slot, same seed, same generator, same arrangement of lots. A file that is missing, from a
different neighbourhood, from an older build, truncated or scribbled on costs one regeneration and
nothing else; Simitone never half-trusts it, because a map made of tiles from two different
neighbourhoods would look entirely plausible and be wrong. Bulldoze a house or rezone a lot and the
arrangement has genuinely changed, so the map is generated again — but a lot's *furniture* changing
does not, because the map was never a function of that in the first place. The world is only ever
written to Simitone's own folder, never into your `UserData` neighbourhood files.

---

## The connected neighbourhood (Part B)

The 3D view draws a world. **Part B makes that world *live*.** One clock for the whole
neighbourhood instead of one per lot, memory of the lots you have left, neighbours that are real
lots rather than pictures, one switch path for every way of changing lot, and a session with
somewhere to be written down.

**It is off by default, in its own right.** *Neighbourhood View* decides what the map screen draws;
this decides whether a world runs underneath everything. They are kept apart on purpose: changing
one never changes the other, and putting the view back to 3D never silently switches Part B back on.

**To turn it on:** *Options → Graphics → Connected Neighbourhood*. It is a checkbox, and it is off
unless you tick it. Two more checkboxes sit beside it, one for each of the two capabilities that
are deliberately not part of it: **Simulate Neighbours** and **Walk Between Lots**. All three are
off unless you tick them, and ticking one does not tick the others.

Underneath, it is seven switches rather than one, each with its own key in `simitone.ini` (the
same file *Neighbourhood View* and *Dynamic Sun* use, so ticking a box cannot cost you either of
those). Every one of them defaults to **off**, and a missing, empty or hand-mangled value counts as
off — a switch nobody can predict is worse than a switch that waits to be asked for. Ticking the
first checkbox turns on the five it covers and only those; the other two keep whatever you had set
them to. The five that ship:

| Key in `simitone.ini` | What it turns on | Off means |
| --- | --- | --- |
| `NeighbourhoodWorld` | one clock, one active lot, one store of lots you have left | the 3D view as described above |
| `NeighbourhoodWorldSurroundings` | the houses you can see are real lots with their own contents | Part A's painted facades |
| `NeighbourhoodWorldTravel` | one switch path for the picker and for a sim asking to leave | today's lot switch, unchanged |
| `NeighbourhoodWorldServices` | build/buy, lot ownership, jobs and leaving a lot go through the world | the existing global-link stub |
| `NeighbourhoodWorldPersist` | the session (time, active lot, hops) is saved with the neighbourhood | the layout sidecar only |

The other key has a checkbox rather than a row in that table, because it is a question about one
capability rather than about the feature: `NeighbourhoodWorldOffscreen` is **Simulate Neighbours**.
It does not consult the first checkbox and the first does not consult it, in either direction —
unticking Connected Neighbourhood leaves it exactly as it was, and ticking it does not make the first
draw as on. What *is* true is that it is inert without the world, because it is a child of the world
in §7 of the plan and a capability with no world has nothing to act on. Its box says so in its own
tooltip, and neither box is greyed out.

**What the checkbox actually gives you today.** The world object exists for as long as you are in a
lot in a generated neighbourhood, and its clock advances with the frame — so the neighbourhood keeps
its own time rather than borrowing a lot's, including while you are on the map screen in no lot at
all. That clock is handed to a lot as it opens and taken back as it is put away, so a lot opens
where the neighbourhood has got to rather than where its own file was saved, and your time in a
saved lot is the neighbourhood's rather than the lot's. The lots around you are drawn two ways, and
which way is decided per lot: the ones close enough to walk to are real lots standing beside you in
the world, and the ones beyond them are facades — see *Near and far* below. Every way of changing
lot — the house picker, and a Sim asking to leave from inside a lot — goes through one switch path,
which stashes the lot you are leaving before the new one is loaded, resumes the state you left in it
when there is one, loads the lot's own file when there is not, and puts you on the doorstep of the
entrance you came through rather than in the middle of the lot. Build, buy, jobs and leaving a lot
are routed through the world instead of the stand-in global link. And what the world knows — its
time, which lot you are in, and the hops between lots — is written down with the neighbourhood's
own file in one atomic commit, so a crash mid-write leaves the previous session readable and a
damaged one is rebuilt rather than refused, and a session left behind is picked up again rather than
started from scratch.

**A walk takes your Sim with it.** With *Walk Between Lots* on, going from one lot to the next is
walked: the route is found over the generated streets, a street lot is built around that stretch of
map, your Sim is moved into it, and the camera follows. At the far end the Sim is handed over as
its own state and the destination lot **adopts it** — so the Sim that arrives is the Sim that
walked, with its name, its family and its inventory, rather than a fresh spawn from the lot's
network driver. Arrival still goes through the same one switch path a jump does, so the lot you
left is still put away, the hop is still recorded, and the hop is recorded as a walk rather than a
teleport. That is the difference you can see: walk from your house to next door and you are *you*,
standing on their doorstep.

**What a walk still is not.** The street lot behind the journey is a thin shell built for the stretch
of map the route crosses, not a full simulation world, so the lot you walked out of keeps living
underneath you and your Sim is *placed* along the pavement rather than routing itself — it will not
stop at a kerb or turn a corner on its own. Nothing of the walk is drawn from inside another lot's
screen. Leave the box alone and travel stays a discrete switch with a cut, which is the shippable
result and is what you get by default.

### Near and far

A lot around you is answered one of three ways, and the answer is worked out once per lot so that
no lot is ever drawn twice.

- **The lot you are standing in** is drawn by the lot view itself, in full, out of its own house
  file. It gets neither of the other two answers — a facade for it would stand a whole lot up to
  build the house you are already standing in.
- **A near lot** is a **real lot**, standing in the world beside yours with its own contents and
  its own terrain — a genuine, separately-loaded lot view of its own, with its own ground, its own
  walls and its own objects in it. It is selected two ways at once: you have to be able to *walk* to
  it over the generated streets, and it has to be within the drawn region's own margin of your
  property line — forty tiles of gap, which is exactly how far the surround draws ground. A gap, not
  a centre distance, so a lot across the road is in and a lot forty lot-widths away is not.
- **One lot is never both.** Each lot in the drawn region gets exactly one answer, worked out once,
  and the two halves of the decision are the same function: the lot the surround builds as a real
  lot is not also given a facade, and the facade pass is told which lots those are so it does not
  build one. Without that, a near neighbour would be drawn twice — once as a wall mesh standing at
  the right height, and once as a real lot occupying the same plot — which looks like a neighbour
  and is only wrong on the frames where the two copies' walls fight for the depth buffer.
- **A far lot** is a **facade** — its own walls, roof and floor, rendered once and then drawn as
  part of the neighbourhood behind your lot. Everything inside the drawn region that is not a near
  lot is one of these.

**The rule of thumb now: everything you can see is a real lot.** The two numbers used to be forty
and eight — forty tiles of *ground* worth drawing, and eight tiles of *lots* worth standing up —
and the gap between them was where flat pictures lived. They are now the same number, and that is
the whole of the change: the drawn region is the region of real lots. A lot you can see in the
street is a lot, with its own contents, standing where its facade was.

That leaves a **budget**, and it is worth knowing about because it is the only thing that can put a
flat house back. A real neighbouring lot is not a mesh: it is its own lot view, its own world, its
own room map, and with advanced lighting its own light map, walked twice a frame for as long as you
are on the lot — and all of them are built in one go when your lot loads, which is a pause you wait
for. So there is a ceiling on how many may be stood up at once, and it is set well above what any
ordinary neighbourhood asks for: the realistic demand at this region's size is **eight**. The budget
is **thirty-two**, chosen to be unreachable rather than tight, because a budget that bites on a
normal street would just be a second, smaller version of the old eight-tile rule. It binds on three
things only: a hand-written neighbourhood packed far tighter than the generator packs one, a map
whose lots are laid out closer than the usual margin, or a machine slow enough that the pause is
felt at eight.

**If the budget ever does bite, you get flatness at the far edge and never a hole.** The lots nearest
you are the ones kept — you are standing at the centre of the region and they are the ones you will
look at — and a lot that lost out is still drawn, as a facade, because it is still inside the region
the surround draws ground for. Ties are broken by house number, so it is the same lot losing out
every time rather than a different one each visit. And the log says so, naming the lots: "3 lot(s)
inside the drawn region are past the budget of 32 sub-worlds and will be drawn as facades instead".
A flat house with nothing in the log saying why is the failure this number exists to make visible.

**What a near neighbour contains, and where it came from.** A neighbour is built from its own
`HouseNN.iff` — the same file your own lot loads from, read by the same loader the facades are
drawn with. Earlier the neighbourhood could only use a lot's contents if it had them in memory, and
it only ever had those for lots somebody had actually walked into, so in a generated neighbourhood
every neighbour arrived as a bare, correctly shaped, correctly levelled plot with a mailbox, a bin
and maybe a car portal on it. The contents were never missing; they were sitting in each lot's own
file.

Reading them costs real work — a whole lot view and a whole simulation stood up and thrown away per
house — so it is done only when there is something to gain and only within a budget: a handful per
lot load, and a fixed number of milliseconds. What falls outside that budget is the bare plot again,
with a line in the log saying which lot and why, and it is picked up the next time you come to that
street. Results are cached against each house file's own fingerprint, so a lot you re-decorate is
re-read and a lot you did not touch is not, and the contents are then also kept in memory for the
rest of the session so a lot switch is free.

**Nothing on a neighbour is simulated.** A neighbour is built when your lot loads and stays as it
was built: its furniture does not move, its clock does not run, and nobody inside it is doing
anything. It is a real lot in every sense you can *see* — its own walls, ground, driveway and
contents — and not a live one.

### How big a thing is a tile

There is exactly one number in the whole of this, and everything else is counted in it.

**One generated map tile is one lot tile is three lot-view world units.** A lot in the generated
world is sized in tiles and a The Sims 1 lot is 64×64 tiles, so a generated tile and a lot tile are
the same thing and nothing has to be agreed about them. Three units a tile is the lot view's own
number — the one every wall, floor and object inside a lot is already measured in — so the
neighbourhood is drawn in the units your lot is drawn in, and putting the whole generated map into
your lot is a translation and a scale rather than a conversion anybody had to work out. A 64-tile
lot is 192 units square once it is standing in your lot view, and the corner of your property that
the layout packed it to is the origin of your lot view.

- **The street is six tiles of tarmac with a pavement tile either side**, so eighteen units of
  tarmac and twenty-four of whole corridor in the lot view's units; a driveway is one tile, so
  three. Six is not a choice: it is what your own lot view draws, and the surround now draws the
  same street rather than a second, narrower one.
- **The street you see is the street the lot view would draw, on the same tiles.** This was the
  single most visible thing wrong with the surround, and it is worth describing because the fix is
  a rule rather than a number. The surround used to lay the *city map's* street — its own band of
  tarmac in the gap the packer left between two properties — while the neighbouring lot standing on
  that ground drew its own street *inside* its own boundary. Two streets, one tile or five apart, and
  a player's front path appearing as a strip of pavement in the middle of their own lawn. The
  surround now reads the lot view's own road grammar — three tiles of tarmac, a tile of the lot's
  own ground, then the pavement, measured in from the lot's edge — and lays it at exactly the tiles
  a neighbouring lot will stamp it at, then fills the middle of the street between two facing bands.
  So the tarmac width now matches the lot view's exactly; the whole corridor is still slightly
  narrower, because the lot view leaves a wider margin on each side than the generator's packer
  does, and that gap is named and measured rather than papered over.
- **The ground is quantised**, in height as well as in plan. The generated map stores height as a
  byte per tile, which is a twelfth of a tile, so a step in the ground is a quarter of a unit — the
  finest height the map can express, and the reason a generated hillside reads as terraces rather
  than as a slope.
- **Your lot's own ground is at zero.** The whole drawn region is measured down to the *lowest*
  ground on your property's border ring, so the neighbourhood's ground is never left standing proud
  of your lawn all the way round it; and where the ground past your property line is lower than that,
  the surround draws a small bank down to your own ground, so the join is a join and not a step.
- **The drawn region is your property plus forty tiles on every side** — 144 tiles across, 432 units,
  with the fog finishing before its far corner rather than at it. It is also the region of real
  lots: every lot in it is stood up, not drawn flat.

### Zooming, and the map staying drawable

A 512×512 map is around three hundred thousand ground triangles, so the neighbourhood does not draw
all of them at every zoom — it draws the ones that are big enough on screen to be worth drawing,
and the choice is made from **how much of the map your view spans**, measured in tiles, so it is the
same rule at every resolution and needs nothing but the width:

- **A whole neighbourhood on screen** (about 724 tiles across): one quad per four by four tiles.
  A tile is a pixel or two wide, so the blocky version is indistinguishable and the frame is a
  sixteenth of the cost.
- **A street or two in view** (160 to 320 tiles): one quad per two by two tiles.
- **A lot in view** (48 to 160 tiles): one quad per tile.
- **Close in** (under 48 tiles): each tile is cut into four by four quads, and the ground is
  interpolated across them rather than being a flat plate.

The two thresholds are different questions, not one number used twice. The coarse levels ask "is a
tile small enough on screen that a block of them is just ground?"; subdivision asks the opposite,
"is a tile now *large* enough on screen that one flat quad of it is visible as a flat quad?" Between
48 and 160 tiles a tile is five pixels wide, and drawing it flat is exactly how a five-pixel tile
should be drawn.

**The level does not flicker when you zoom.** A rule that simply switched at a fixed width pops
twice on every zoom step, because a camera tweening through the boundary is at the exact width where
the answer changes, and one frame later it is not. So a level is only left once the view has crossed
the boundary by fifteen per cent, and once it has, the same margin applies coming back — the same
zoom gives the same answer in either direction. The one exception is the very first frame, which
takes the rule at face value: a neighbourhood screen starts zoomed out on the whole map, and a
deadband there would refuse the first change and leave the finest level on screen for ever.

The terrain, the roads and the lot rectangles are all emitted on the map's own integer tile grid,
which is what makes the placement a struct and a test rather than something read back out of a draw
call: a vertex that is a whole map tile is three units from its neighbour, and the packed rectangles
are whole map tiles too.

### Known limitations

- **One generated world per mode, as before.** Home, Downtown, Vacation, Studio and Magic each get
  their own map, and travelling between them is a transition between two worlds rather than a walk
  from one edge of one map to the other.
- **A whole neighbourhood slot can share one coordinate space — and nothing calls it yet.** There is
  a planner that places all five of a neighbourhood's views in one slot-wide space, side by side on
  a fixed pitch with a one-tile gap, and answers the question you would ask of it: which view owns a
  tile, where a lot sits in both spaces, and whether crossing from one view to another is a walk or a
  carried trip. It settles the residential view at the origin, so today's per-mode coordinates are
  unchanged in the shared space and anything that has not heard of it keeps working. Its answer is
  that every cross-mode hop is a **transition**, not a walk — a shared coordinate space is not a
  road, and the one tile of gap between two windows is there to make that true by construction. But
  **travel between modes does not go through it yet**: going from your house to Downtown still loads
  Downtown's own generated world and cuts, which is the same behaviour, arrived at by the existing
  path. So this is a decided and tested plan, not a shipped feature.
- **The walk carries the Sim; the street is still a thin shell.** *Walk Between Lots* builds a real
  lot around the stretch of street your route crosses, moves your Sim into it, and hands it to the
  destination lot at the other end with its own state — so identity and inventory survive the
  crossing. What is still missing is everything that needs that street lot to be a full simulation
  world: your Sim is *placed* along the pavement rather than routing itself, so it does not stop at
  a kerb or turn a corner on its own; the lot you left keeps living underneath the walk; and nothing
  of the walk is drawn from inside another lot's screen. Each of those is a gap at one call site
  rather than a decision somebody made, which is the difference between a limitation and a feature.
- **Off-screen simulation is a model, not the engine.** When it is switched on it writes real motive
  decay into lots that are in memory, using The Sims 1's own decay table; a lot nobody has ever
  entered gets modelled numbers rather than simulated ones. Objects, plants and conversations are
  not modelled at all — they need a room table and a person in it, and a marshal carries neither.
  A room score and a household income are modelled, but **both are off unless you turn them on**,
  there is no setting for them yet, and both are honest about what they are: the room score is the
  engine's own room arithmetic run over your lot's footprint and one guessed term for its furniture,
  and the income is a *rate* a day derived from the jobs your Sims' data records — bounded, drawn
  from the neighbourhood's own seed, and never a balance. Nothing off-screen reads or moves a
  budget; money stays where it belongs, in the family.
- **A near neighbour has real contents, but it is not a live one.** It is built from its own
  `HouseNN.iff` when the lot loads, as described above, so the house, the furniture and the driveway
  are all really there. What is not there is a running simulation: nothing on it is simulated, its
  clock does not run, it is not kept up to date, and the Sim walking through it is the only thing
  that moves on it. Reading a lot's contents also costs a throwaway lot view and simulation per
  house, so it is budgeted, and a lot that loses out of that budget is the bare plot again until
  you next come to that street.
- **A discrete hop is recorded as arrived**, so there is no half-finished journey to resume yet; the
  table that would hold one exists and is saved.
- **The ground's shading gets flatter the further out you are.** The terrain is lit per vertex from
  its own elevation field, and the coarser levels of detail give every corner of a block one
  averaged normal and one averaged height, so a hillside reads as a mosaic of flat-shaded plates
  with a step where two blocks meet. Zooming in past the closest threshold fixes it: at that range
  each tile is subdivided and the ground is interpolated across it rather than being one flat plate.
  The plates are what the level of detail trades away to keep a whole map drawable, and they are
  still there at the zoom a neighbourhood opens at.
- **A hand edit is noticed when the file's stamp moves.** The switches are re-read as soon as
  `simitone.ini` is written, so ticking a key in an editor takes effect without restarting the game.
  The check is the file's last-write time and its length, though, so an edit that changes neither —
  a same-length rewrite landing inside one filesystem clock tick — waits for the next save from the
  Options page. A missing, unreadable or hand-mangled file still reads as off, and costs one cheap
  look rather than a re-read.
- **Nothing Part B does can stop a neighbourhood from opening.** A world that cannot be built, a lot
  file that will not read, a capability that has to degrade — each one falls back and writes the
  reason to the log, and you get the 2D map rather than an error. A neighbour that cannot be read
  is the same story one level down: that lot alone is built bare, and the street carries on.

---

*Implementation:* `docs/NEIGHBOURHOOD_SCOPE_A_PLAN.md` (§7–§8, milestones M1–M5) and
`docs/NEIGHBOURHOOD_WORLD_HANDOFF.md` (Part B, milestones B1–B8) with
`Neighbourhood/*` under `Client/Simitone/Simitone.Client/`. The connected world is a Simitone-native
mesh builder fed by the generated 512×512 map; it does not use The Sims Online's city renderer.
Part B's capabilities are `NeighbourhoodWorldFlags` and `UI/Panels/Options/NeighbourhoodWorldRow.cs`,
and their end-to-end coverage is `Simitone.Tests/Neighbourhood/PartBIntegrationTests.cs` — including
the seams above: that the facade cache is told exactly which lots the surround built as real lots,
that a walked Sim is adopted by the lot it arrives in rather than respawned, that a cancelled walk
puts the world back where it started, and that the two off-screen models stay off until asked for.

The four claims this document's later sections make are pinned there too, and all four are asked of
the same real generated map rather than of a copy of a number:

- **Real neighbour contents** — `NeighbourHollowLoader`'s refusal, cache and budget rules, and that
  a plan reaches for a loader at all exactly when some neighbour has contents nowhere else. What is
  not asserted is the pass that actually reads a `HouseNN.iff`: that needs a graphics device, a
  content manager and game content, and it is named in the test rather than faked.
- **The street alignment** — that a sub-world at a given tile delta puts its pavement on the world
  tiles the surround's own band puts it on, and that the band is laid at the lot view's own widths.
  The alignment itself is a fact about two pure functions agreeing, which is why it can be a test
  at all.
- **The full region and its budget** — that every lot of the drawn region is a real lot on any
  layout the generator produces, and that a budget which does bind keeps the nearest, names the
  rest, and leaves every one of them drawn as a facade rather than dropped.
- **The cached map and the levels of detail** — the raster file's round trip byte for byte, the five
  ways a stale stamp and the three ways a damaged file each cost one regeneration and nothing else,
  the rule choosing a level from the view's width, and the deadband that stops it flickering.

The scale
and the near/far decision above are pinned by `NeighbourhoodScaleAndNeighbourTests.cs` and
`NeighbourhoodMapScaleTests.cs`, which build a real generated map and a real street network from
canned lot records rather than asserting either number against a copy of it, and which assert the
*ratio* between a run of map tiles and the lot view's tile so it keeps holding whatever a tile
costs. The near/far rule itself — that a planned neighbour is a real lot and never also a facade —
is pinned per type by `NeighbourhoodLotRenderDecisionTests.cs`. The slot-wide planner described
under *Known limitations* is pinned by `NeighbourhoodWorldUnificationTests.cs`; the off-screen
room and income models by `OffscreenSimulatorTests.cs`.
