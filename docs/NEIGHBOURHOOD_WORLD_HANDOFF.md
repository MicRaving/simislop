# Connected-neighbourhood handoff — Part B: a live, simulated world

**How to read this.** `docs/NEIGHBOURHOOD_SCOPE_A_PLAN.md` is **Part A**: a procedurally
connected neighbourhood that renders but does not simulate, with milestones M0–M5. This
document is **Part B**: turning that into a live world where sims move between lots and the
neighbourhood persists. Part A must be complete and fully functional first; Part B is
implemented only after M5, and Part A must keep working even if Part B is never finished.

**Audience.** An engineer picking this up cold. §2 tells you what already exists and is worth
reusing; §3–§5 are the design; §6 is the milestone task list; §11 is the list of landmines.

---

## 0. Confirmed decisions and guarantees

Locked (see also Part A §0):

| # | Decision | Value |
| --- | --- | --- |
| 1 | 3D connected neighbourhood default | **Off** (opt-in) until the look is proven |
| 2 | Generated map shape | **Accept `CityGeometry`'s isometric diamond** |
| 3 | Terrain blend masks | **Omitted** in the first version |
| 4 | Generated world granularity | **One world per mode**, matching the existing per-mode maps |
| 5 | Scope B timing | **Only after M5**; A works without B |
| 6 | B preparation | Allowed inside M0–M5 **only** as inert seams (Part A §15) |

**The two guarantees that must hold at every point:**
1. **A-only builds work.** Any Part B milestone can be absent or disabled and Part A's M0–M5
   acceptance criteria still pass.
2. **B stages degrade backwards.** Turning a B feature off returns to the previous stage, not
   to a broken state.

---

## 1. Scope B definition and non-goals

### Definition
The connected world of Part A becomes **simulated**:
- the player controls a sim that can leave a lot and reach another lot through the generated
  world;
- time, weather and family money are **shared** across lots;
- inactive lots persist as **hollow** lots (present, minimally simulated) and survive leaving
  and returning;
- the world **saves and reloads**, including mid-travel.

### Non-goals
- No online play, no other players, no server. This is a single-player world.
- No rewrite of TS1 lot or neighbourhood data. The world is a **session layer**; `HouseNN.iff`
  and `Neighborhood.iff` remain the source of truth for contents and families and are written
  only through the existing, tested save paths.
- No change to Part A's rendering contracts.
- No new content format for lots; `VMMarshal`/`VMHollowMarshal` are reused.

---

## 2. What already exists (reuse map)

This tree already contains most of a lot-world model, because it is a FreeSO fork. Read these
before designing anything:

| Facility | Where | What it gives us |
| --- | --- | --- |
| Multi-lot in-memory store | `TS1GameScreen.SavedLots` (`Dictionary<short, VMMarshal>`), `LotTravel.ShouldStashCurrentLot`/`TryRestore` | Per-lot marshalled state across a rail switch: the seed of the world's lot store |
| Lot switch signal | `VM.OnRequestLotSwitch` (`VMLotSwitchHandler`), `VM.SignalLotSwitch`, `TS1GameScreen.VMLotSwitch`/`SwitchLot` | The event a sim raises to travel; B hooks the same event |
| Cross-lot game state | `FSO.Content.TS1.TS1GameState` (`ActiveFamily`, `DowntownSimGUID`, `LotTransitInfo`), persisted in `Neighborhood.iff` | The existing "travelling family" state |
| Travel slots | `TravelSave`, `TS1GameScreen.TryRestoreTravelSlot` | Cross-session preservation of a lot being travelled to |
| Hollow lots | `VMHollowMarshal` (`FSOh` magic, versioned, gzipped), `VM.HollowLoad` | Present-but-lightweight neighbour lots |
| Layout-aware surroundings build | `VMLotTerrainRestoreTools.RestoreSurroundings(vm, hollowAdj)`, `RestoreTerrain`, `RestoreHeight`, `GetBaseLevel`, `PopulateBlankTerrain`, `EnsureCoreObjects`, `PositionLandmarkObjects` | How a lot gets its terrain/roads/objects to match the surrounding map; B adapts the selection + offsets |
| Surroundings terrain container | `VMTSOSurroundingTerrain` (`BlendN[3,3]`, `Roads[3,3]`, `Height[4,4]`) under `vm.TSOState.Terrain` | The shape the terrain helpers consume; B fills it from the generated map |
| Subworld rendering | `SubWorldComponent`, `Blueprint.SubWorlds`, `World.InitSubWorlds`, `SubWorldComponent.GlobalPosition` | Drawing real neighbour lots at world offsets |
| Terrain/roads into a lot | `VMTS1Activator` (heights `ARRY(0)`, grass `ARRY(6)`, `HouseNumToType`), `FSO.Server...LotContainer.GenerateTerrain` (reference) | How the generated map should shape lot terrain |
| Cross-lot surface | `IVMTSOGlobalLink` (40+ methods), `VMTS1GlobalLinkStub` | Budget/transactions/leave-lot/architecture; B implements the TS1-relevant subset |
| Shared family money | `FAMI.Budget` via `TS1NeighborhoodProvider.FamilyForHouse`/`GetFamilyForHouse`; `VMTS1GlobalLinkStub.GetBudgetForFamily` | Money is *already* a shared chunk; the world mostly coordinates, it does not re-plumb money |
| Family activation | `VMTS1LotState.ActivateFamily`/`VerifyFamily`, `TS1State.CurrentFamily` | Bringing a family onto a lot |
| Layout + generated map (Part A) | `NeighbourhoodLayout`, `NeighbourhoodMapGenerator`, `RoadGraph`, `NeighbourhoodQuery`, `ILotEntrance` | The spatial substrate B routes on |

**The single most important reuse fact:** Part A's `RoadGraph` is not just a rasterizer — it is
a queryable graph of lot entrances and street junctions. Part A renders from it; **Part B
routes sims on it.** That is what makes the prep seam in Part A §15 worth doing.

---

## 3. Target architecture

```
                       INeighbourhoodWorld  (Part A seam, grown in B)
                       ├─ INeighbourhoodClock        authoritative time
                       ├─ ActiveLot / ActiveFamily
                       ├─ LotStore  { Unloaded | Hollow(VMHollowMarshal) | Active(VMMarshal) }
                       ├─ Transit   { sims between lots }
                       ├─ FamilyAuthority (FAMI money/ownership)
                       ├─ NeighbourhoodQuery  (tiles, entrances, road graph, path)
                       ├─ Rng (streams from the world seed)
                       └─ Tick(delta)  (game thread only)
                                   │
        ┌──────────────────────────┼──────────────────────────────┬───────────────────────┐
        ▼                          ▼                              ▼                       ▼
  SimitoneSurroundingsRestore  World.TransitionTo          NeighbourhoodScene      OffscreenSimulator
  (hollow/blank neighbour      (the one lot-switch path)   (A renderer reads it)   (B6, inactive lots)
   subworlds)
```

One authority for time, one path for changing the active lot, one query object for space.

---

## 4. Preparation seams (already in Part A) and the safety invariants

Part A §15 lists the seams introduced inside M0–M5. For Part B's purposes they are:

| Seam | Part B uses it for |
| --- | --- |
| `INeighbourhoodWorld` + `Tick(delta)` no-op | Growing the world without touching the screen's lot lifecycle |
| `INeighbourhoodClock` | Making time one authority (B1) |
| `ILotContentSource` | A mutable family/occupancy source (B1/B5) |
| `NeighbourhoodQuery` + persisted road graph | Routing (B4) and neighbour selection (B2) |
| `ILotEntrance` | Arrival placement (B3/B4) |
| `INeighbourhoodSceneSource` | Rendering subworlds instead of facades (B2) |
| Sidecar `schemaVersion` | Upgrading layout-only → world state (B7) |
| Derived RNG streams | Off-screen determinism without disturbing generation (B6) |
| Immutable maps + game-thread apply | Keeping B's simulation on the game thread (B1/B6) |

**Invariant:** every seam has a trivial Part-A implementation and can be deleted without
touching Part A's behaviour. If a B task cannot be built on these seams, extend the seams
inertly rather than reaching into Part A's code.

---

## 5. World model

### 5.1 One world per slot, per mode
TS1's `NeighborhoodService.NeighbourhoodIdForHouse` maps lot numbers to modes (2 Downtown,
3 Vacation, 5 Studio, 7 Magic, else residential), and all of those lots live in the **same**
`UserData` folder (`Neighborhood.iff`, `Houses/HouseNN.iff`). Part A generates one world per
mode. Part B keeps that and treats **cross-mode travel as a transition**, like the existing
travel (a taxi/car between areas), not as walking. A later unification (one generated map per
slot spanning all modes) is explicitly out of scope; §13 records it as an open question.

### 5.2 Clock
`VMTS1Activator.LoadFromIff` seeds `VM.Context.Clock` from `VM.GlobalState[0,1,5,6,7,8]`.
`WorldClock` owns those values; a lot's VM is seeded from the world on load and pushes back on
save/tick. With B off, the round-trip must be byte-identical to today.

### 5.3 Lot states
- **Active**: the lot the player is on, backed by a live `VM` and marshalled into
  `LotStore[number]` on switch (today's `SavedLots`).
- **Hollow**: `VMHollowMarshal`, present as a subworld but not driving a player.
- **Unloaded**: only the layout + the `HouseNN.iff` file; built from disk on demand.

### 5.4 Transit
`Transit` records sims between lots: `{ simGUID, origin, destination, path?, progress?, mode }`.
It is authoritative for "where is this sim"; the provider's `TS1GameState.LotTransitInfo` is
mirrored for compatibility with existing primitives.

### 5.5 Family authority
`FAMI` chunks are shared already, so money is intrinsically cross-lot. The world becomes the
single writer (so a background tick and a lot tick cannot both mutate a `FAMI.Budget`
unobserved) and owns lot ownership/occupancy.

### 5.6 Persistence split
- **Contents** → existing per-lot `HouseNN.iff` save (`SaveService`/`SaveHouse`, atomic).
- **World/session state** → Simitone sidecar, schema 2 (clock, active lot, transit, lot
  states, seed, generator version), written with `AtomicFileCommit`.
- The world sidecar **never** duplicates lot contents.

---

## 6. Milestones

Each milestone is independently gate-able and independently abandonable. "Flag" is the
capability switch; off means the previous stage's behaviour.

### B1 — World state foundation
**Goal:** one authority for time, active lot and stored lots; no behaviour change when off.

Dependencies: M5. Tasks:
1. Grow `INeighbourhoodWorld` to the §3 shape; keep `ReadOnlyNeighbourhoodWorld` for A.
2. `SimitoneNeighbourhoodWorld` owning `NeighbourhoodLayout`, generated map, `LotStore`,
   `WorldClock`, `Transit`, seed, and the `Tick(delta)` implementation.
3. `WorldClock` ↔ `VM.Context.Clock`/`GlobalState` sync on `InitializeLot`, `Save`, `Tick`.
   Prove it round-trips identically with B off.
4. Move `TS1GameScreen.SavedLots` behind the world; keep `LotTravel` semantics and keying.
5. Call `World.Tick(delta)` from `TS1GameScreen.Update` (world off ⇒ empty).
6. Flag `NeighbourhoodWorld` (off).

Acceptance: with the flag off, M0–M5 behaviour is byte-identical; with it on but nothing else,
entering/leaving/reloading keeps the clock and stored lots. Tests: clock round-trip, stash/
restore, sidecar schema upgrade.

Risk: low. Mostly mechanical.

### B2 — Hollow neighbours and layout-aware surroundings
**Goal:** the neighbours you can see are real lots, not just Part A facades.

Dependencies: B1. Tasks:
1. `SimitoneSurroundingsRestore` (Simitone-side). Do **not** change
   `VMLotTerrainRestoreTools.RestoreSurroundings` in place: it assumes a TSO 3×3 grid keyed on
   `vm.TSOState.LotID` with `(size−2)` spacing. Write a layout-aware version that reuses its
   helpers (`RestoreTerrain(VM, TerrainBlend, byte roads, RestoreLotType)`, `RestoreHeight`,
   `GetBaseLevel`, `PopulateBlankTerrain`, `EnsureCoreObjects`).
2. Choose neighbours from `NeighbourhoodQuery` (road adjacency + a configurable radius), and
   fill each neighbour's `TerrainBlend`/`Roads`/`Height` from the generated map (the same data
   Part A feeds `CityGeometry`).
3. If `LotStore` holds a `VMHollowMarshal` for the neighbour, `tempVM.HollowLoad(hollow)`;
   otherwise build a blank lot exactly as `RestoreSurroundings` does for `hollow == null`.
4. Set `subworld.GlobalPosition` from the **layout** rectangle offsets (not the `(size−2)`
   grid); `Blueprint.SubWorlds.Add`; `World.InitSubWorlds()`.
5. Coordinate with Part A's `NeighbourhoodFacades`: near neighbours as subworlds, far ones as
   facades; one `Rendered` decision per lot; keep the `surroundNumber` flag meaningful.
6. Disposal: clear subworlds in `CleanupLastWorld`/`DisposeOnLot`; no leaks across switches.
7. Flag `NeighbourhoodWorldSurroundings`.

Acceptance: neighbours show real contents; a missing/broken neighbour IFF is skipped, not
fatal; Part A's facade-only surroundings still work with the flag off.

Risk: medium. The TSO helpers are grid-shaped; the mismatch is the main work.

### B3 — Discrete cross-lot travel through the world (first playable result)
**Goal:** a sim can travel from any lot to any other lot, and the world keeps time, money,
family and state.

Dependencies: B1, B2. Tasks:
1. One switch path: `World.TransitionTo(houseId, reason, arrival)` used by both
   `NeighborhoodService.PlayHouse` (picker) and `VMLotSwitch` (in-VM request).
2. `arrival` from `ILotEntrance`: place the arriving/controlled sim at the destination's
   entrance (mailbox side) instead of the lot centre.
3. Record the hop in `Transit`; mirror `TS1GameState.LotTransitInfo`.
4. Keep `TravelSave`/`TryRestoreTravelSlot` precedence (a valid travel slot still beats the
   shipped template) and the exactly-once stash-before-load ordering.
5. On switch: atomic house save, world clock update, transit update.
6. Flag `NeighbourhoodWorldTravel` (off ⇒ today's `SwitchLot` path).

Acceptance: home → community → home preserves money, time and family; arrival is at the
entrance; travel slots still work; a failure falls back to the old path. Tests: round trip,
arrival tile, slot precedence, stash-once.

Risk: medium.

### B4 — Continuous walkable streets (may be abandoned)
**Goal:** the sim actually walks the generated streets between lots, camera following.

Dependencies: B3. Tasks:
1. Introduce a **transit lot**: a `World`+`VM` whose blueprint is the generated street region
   around the hop, holding the travelling avatar. Rationale: exactly one VM owns the avatar at
   any moment; two lot VMs sharing one avatar is the failure mode to avoid. (TSO's city VM is
   the same idea.) Considered and rejected for a first cut: a world-space avatar drawn by
   `Neighbourhood3DView` with no VM — routing and motives need a VM.
2. Route on `NeighbourhoodQuery`'s graph (A* between entrances); drive the avatar along the
   path (reuse `VMRouting` if it fits, otherwise a small path follower for the transit lot).
3. Threshold crossing: on reaching the destination entrance, `World.TransitionTo(destination)`
   and move the avatar into the destination VM; stash/unload the origin.
4. Camera/controls: a world camera that follows the avatar on the generated terrain, with the
   UI mode switch; keep the pure-helper style of `NeighCameraMachine` for testability.
5. Guard: exactly one active lot at a time (active / transit / hollow), asserted.
6. ~~Flag `NeighbourhoodWorldWalk`~~ (cut 2026-11; formerly off ⇒ B3 discrete travel only — the shippable result).

Acceptance (if it lands): walk out of a lot, along generated roads, into another lot's
entrance, and enter; camera and input coherent; no double simulation. If abandoned, B3 plus a
cut/fade is the documented outcome.

Risk: high. This is the milestone most likely to be cut; design and gate it accordingly.

### B5 — World services / offline global link
**Goal:** build/buy, lot ownership, jobs and leaving a lot behave world-consistently.

Dependencies: B1, B3. Tasks:
1. `SimitoneGlobalLink : IVMTSOGlobalLink` backing the world where the world is on; keep
   `VMTS1GlobalLinkStub` otherwise. Implement the TS1-relevant subset and leave TSO-online-only
   members no-op (as the stub already does).
2. Budget/transactions: route through the world's family authority so an off-screen tick and a
   lot tick cannot both write `FAMI.Budget` unseen. Do **not** regress the existing
   "multiple families" TODO in `VMTS1GlobalLinkStub.GetBudgetForFamily`.
3. `LeaveLot`: single-player must **transition**, not `CloseNet(LeaveLot)` as the stub does.
4. `QueueArchitecture`/`Tick`: keep the local verify/forward semantics.
5. Ownership: buy/sell/evict via `NeighborhoodService` + world occupancy; jobs used by Studio/
   Downtown routed to the world.
6. Flag `NeighbourhoodWorldServices`.

Acceptance: transactions during build/buy are consistent across a switch; acquiring/releasing
a lot charges the correct family; leave-lot transitions.

Risk: medium. The interface is wide; the implemented surface is small.

### B6 — Off-screen simulation and global time
**Goal:** the world moves on while the player is elsewhere.

Dependencies: B1, B3. Tasks:
1. `OffscreenSimulator` ticking inactive **resident** lots: clock always; motive decay from
   TS1 motive tuning; money via jobs/payouts; optionally object/plant decay.
2. Deterministic from a dedicated RNG stream (Part A §15 seam).
3. Fidelity: Paused / Coarse / Full; default Coarse.
4. Applied to a lot's stored state, never to the active lot's live tick.
5. Flag `NeighbourhoodWorldOffscreen`.

Acceptance: leaving and returning after N in-game hours shows time passed and plausible,
bounded motive changes; same seed ⇒ same result; Paused ⇒ nothing changes.

Risk: medium. Design-heavy; keep the model coarse and honest.

### B7 — World persistence, migration, recovery
**Goal:** quit and resume, including mid-travel, without loss or corruption.

Dependencies: B1 (can start early; must land before B is shippable). Tasks:
1. Sidecar schema 2: clock, active lot, transit, lot states, seed, generator version. Atomic
   (`AtomicFileCommit`).
2. Migration 1 → 2 (layout-only → world) and downgrade safety (an A-only build ignores the
   extra fields rather than failing).
3. Recovery: corrupt sidecar ⇒ rebuild from `Neighborhood.iff` + lots; lose only session state.
4. Mid-transit resume: resume the hop, or deterministically arrive (origin or destination).
5. Never store lot contents in the sidecar; those still go through `SaveService`/`SaveHouse`.

Acceptance: quit mid-transit and resume; version rejection; corrupt recovery; atomic write.
Risk: medium.

### B8 — World features (optional, late)
Centralised day/night and weather across the world; community visitors/townies; NPC routing;
world-level stats. Scope only after B1–B7. No gate; purely additive.

---

## 7. Sequencing and flags

```
Part A (M0–M5) ──► B1 ──► B2 ──► B3 ──┬─► B4 (may be abandoned)
                  │       │      │     ├─► B6
                  │       │      ├─► B5
                  │       └───────────────┘
                  └─► B7 (must land before B ships) ──► B8
```

| Stage | Flag (capability) | Off ⇒ |
| --- | --- | --- |
| Part A | `NeighbourhoodView` | 2D bitmap map |
| B1 | `NeighbourhoodWorld` | Part A behaviour |
| B2 | `NeighbourhoodWorldSurroundings` | Part A facades only |
| B3 | `NeighbourhoodWorldTravel` | today's `SwitchLot` |
| B4 | ~~`NeighbourhoodWorldWalk`~~ (cut 2026-11) | B3 discrete travel |
| B5 | `NeighbourhoodWorldServices` | `VMTS1GlobalLinkStub` |
| B6 | `NeighbourhoodWorldOffscreen` | world time frozen off-lot |
| B7 | `NeighbourhoodWorldPersist` | A's layout sidecar |

Every flag defaults off on first landing and is turned on only when its acceptance test passes.

---

## 8. Threading, determinism, performance

- **Threading.** Part A's generation is pure and runs on a worker task, uploading on
  `GameThread.NextUpdate`. Part B's simulation must run on the game thread only (`VM` is not
  thread-safe). B joins the existing game-thread apply boundary; it never ticks a VM from a
  worker.
- **Determinism.** Part A's seed is split into `generation`, `simulation`, `foliage` streams so
  tuning generation cannot perturb simulation. B6 is the only consumer of `simulation`.
- **Performance.** Hollow lots and facades are the budget mechanism: full VMs only for the
  active lot and the transit lot; neighbours are hollow or blank. Off-screen fidelity is the
  second dial. Long saves must stay atomic and off the render path where possible.

---

## 9. Testing and QA

Reuse the repo's style: pure logic and guards in `Simitone.Tests`, no GPU.

- **B1:** clock round-trip identical with the world off; stash/restore unchanged; sidecar
  schema upgrade; `Tick` is a no-op when off.
- **B2:** neighbour selection is deterministic; a missing neighbour IFF is skipped; subworlds
  are disposed on switch; facade-only mode unaffected.
- **B3:** home→community→home preserves money/time/family; arrival tile = entrance; travel
  slot precedence; exactly-one stash; failure falls back.
- **B4 (if built):** a path exists between adjacent entrances; transition is idempotent;
  exactly one active lot; camera state machine pure-tested.
- **B5:** transaction consistency across a switch; ownership charging; leave-lot transitions;
  no-op methods unchanged.
- **B6:** determinism by seed; time passes; motives bounded and plausible; Paused does nothing.
- **B7:** atomic write; version rejection; corrupt recovery; mid-transit resume.
- **Cross-cutting regression:** the M0–M5 suite must pass with every B flag off, and with each
  B flag individually on where it does not depend on a later one.

---

## 10. Save model and migration

| Data | Owner | Format |
| --- | --- | --- |
| Lot contents | existing TS1 path | `HouseNN.iff` (atomic `.tmp`→`.bak`→commit) |
| Families/neighbours/inventories | existing provider | `Neighborhood.iff` |
| Layout + generated-map seed | Part A | `Neighbourhoods/generated/<slot>.json`, schema 1 |
| World/session state | Part B | same sidecar, schema 2 (clock, active lot, transit, lot states, seed) |
| Cross-session travel | existing | `TravelSave` slots |

Migration: schema 1 is a strict subset of schema 2; an A-only build ignores schema-2 fields. A
schema this build does not know is refused and regenerated from `Neighborhood.iff`, never
half-read.

---

## 11. Known gotchas / landmines

1. **`VMLotTerrainRestoreTools.RestoreSurroundings` is TSO-shaped.** It assumes `vm.TSOState.Terrain`
   (3×3 `BlendN`/`Roads`, 4×4 `Height`), `vm.TSOState.LotID` as a packed grid coordinate, and
   `(size−2)` subworld spacing. Our layout is not a uniform grid. Write a layout-aware sibling
   and reuse the helpers; do not edit the TSO path.
2. **`VMTS1LotState` has no `Terrain`.** `vm.TSOState.Terrain` only exists on the TSO platform
   state. B fills a `VMTSOSurroundingTerrain` explicitly or passes blend/road/height directly
   to the helpers.
3. **`VMTS1GlobalLinkStub.LeaveLot` closes the net** (`CloseNet(LeaveLot)`) for the local
   player. Single-player must transition instead.
4. **`SavedLots` keying.** Keyed by house number; `LotTravel` compares current vs target to
   stash exactly once. Preserve both.
5. **`SwitchLot` ordering.** `VMLotSwitch` only sets `SwitchLot`; the actual switch happens in
   `Update`. Keep the stash-before-load order.
6. **Two homes for transit state.** `TS1GameState.LotTransitInfo`/`DowntownSimGUID` are
   persisted in `Neighborhood.iff`; the world's `Transit` is new. Pick the world as
   authoritative and mirror to the provider for the existing primitives.
7. **Shared `FAMI` money** means money is already cross-lot; don't add a second money store.
   Make the world the single writer to avoid lost updates.
8. **VM is not thread-safe.** Simulation on the game thread only.
9. **`CityGeometry` is 512×512 with a diamond clip.** Part A generates 512×512; B's spatial
   queries must use the same coordinate space.
10. **Never write non-game files into `UserData`.** The sidecar lives in
    `<UserDir>Neighbourhoods/`; lot contents only via the existing atomic save.
11. **`AtomicFileCommit` for every new file.** A crash mid-write must leave the previous world
    readable.

---

## 12. Risks and mitigations

| Risk | Likelihood | Impact | Mitigation |
| --- | --- | --- | --- |
| B4 (walking) proves too large | High | B feels less "alive" | Gate B4; B3 discrete travel is the shippable result; document the cut |
| Terrain seams again, now on the sim side | Medium | Visual/sim glitches | Part A's `TerrainSeamSolver` feeds the same helpers; neighbours share the map |
| Neighbour lots simulated twice / avatar duplicated | Medium | Corrupt state | One active lot invariant; subworlds are hollow/blank; transit lot owns the avatar |
| Clock/money lost updates | Medium | Wrong budgets/time | Single world writer; game-thread tick; round-trip tests |
| TSO helpers misused (grid assumptions) | Medium | Crashes, wrong terrain | Layout-aware sibling; warnings in §11 |
| Persistence corruption | Low | Lost session | Atomic commit; version refusal; rebuild from `Neighborhood.iff` |
| Scope creep into online/TSO semantics | Medium | Time sink | §1 non-goals; TSO-only global-link methods stay no-op |

---

## 13. Open questions

1. **Unified world per slot vs per-mode worlds.** This handoff keeps Part A's per-mode worlds
   and treats cross-mode travel as a transition. A single map per slot spanning home/downtown/
   vacation/studio/magic would make B more continuous but contradicts decision 4 and is a much
   larger change. Recommend keeping per-mode + transition for B; revisit after B7.
2. **Transit lot blueprint.** Reuse the destination's blueprint while walking, or synthesise a
   dedicated street blueprint from the generated map? Synthesising is cleaner but needs a
   walkable blueprint generator; reusing is cheaper but leaks lot geometry.
3. **Off-screen fidelity default.** Coarse is assumed; confirm.
4. **Is B4 required for "B done"?** Recommend no: B1–B3 + B5–B7 with B4 optional is a complete,
   honest single-player connected world.

---

## 14. First reads for the implementer

1. `docs/NEIGHBOURHOOD_SCOPE_A_PLAN.md` — Part A, especially §15 (the seams).
2. `FreeSO/TSOClient/tso.simantics/Utils/VMLotTerrainRestoreTools.cs` — `RestoreSurroundings`
   et al., and why they are TSO-shaped.
3. `FreeSO/TSOClient/tso.simantics/VM.cs` — `OnRequestLotSwitch`, `HollowLoad`, `TS1State`/`TSOState`.
4. `Client/Simitone/Simitone.Client/UI/Screens/TS1GameScreen.cs` — `InitializeLot`, `SavedLots`,
   `VMLotSwitch`, `SwitchLot`.
5. `Client/Simitone/Simitone.Client/Utils/LotTravel.cs` — the stash/restore contract.
6. `FreeSO/TSOClient/tso.simantics/Engine/TSOGlobalLink/` — `IVMTSOGlobalLink`, `VMTS1GlobalLinkStub`.
7. `FreeSO/TSOClient/tso.simantics/Marshals/Hollow/VMHollowMarshal.cs` and
   `Model/TSOPlatform/VMTSOSurroundingTerrain.cs`.
8. `FreeSO/TSOClient/tso.world/Components/SubWorldComponent.cs` and `tso.world/World.cs`
   (`InitSubWorlds`, `MakeSubWorld`, `Surroundings`).
