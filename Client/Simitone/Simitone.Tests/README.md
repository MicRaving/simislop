# Simitone.Tests

The first test project in the repo. It exists to lock in the **pure, deterministic** logic that
the hardening pass introduced or fixed, so that behaviour cannot silently regress. It is not a
coverage exercise.

## Ground rules

* `Simitone.Client` is a MonoGame library. Nothing here may construct anything that touches a
  `GraphicsDevice`, a window, or an audio device - the test host is headless and that would
  crash the run. Every test in this project exercises static data, static pure functions, or
  file IO only.
* Production code under `Client\Simitone\Simitone.Client\` is **read-only** to this project.
  Nothing private was widened, nothing was made `internal` for testability. Where logic could
  only be reached by changing production code, it is reported in `UI/UntestableBoundaries.cs.txt`
  rather than tested.
* `Nullable` and `ImplicitUsings` are disabled, matching the rest of the repo, so every file
  has explicit `using` directives.

## Test files

| File | Covers |
| --- | --- |
| `Utils/SimitoneLogTests.cs` | `SimitoneLog`: never throws for any input (null / empty / whitespace / embedded newlines / unicode / 100 KB messages / real `Exception`s, on all three severities and both overloads), `LogPath` resolution and its relative-path fallback, line format, exception formatting, the 2 MiB rollover, and that 200 concurrent writes each produce one intact line. |
| `Utils/BudgetServiceTests.cs` | `BudgetService` in its genuine "no active family" state (which is also the headless state): the `int.MaxValue` unlimited sentinel, `GetDisplayBudget` returning 0 rather than the sentinel, and `CanAfford` always true. |
| `UI/SpeedRemapTests.cs` | `UIDesktopUCP` / `UIClockPanel` `RemapSpeed` + `ReverseRemap`: the mapping is a bijection onto the four panel button indices, the reverse table really inverts it, pause is index 4, the three speeds ascend, both panels agree, and `FloorNames` has one entry per floor. |
| `UI/DirectionUtilsTests.cs` | `DirectionUtils.PosMod` / `Normalize` / `NormalizeDegrees` / `Difference` - the wrap-around guards the CAS carousel and the lot-rotation drag helper cast straight into list indices. |
| `UI/BuyCatalogDataTests.cs` | `UIBuyBrowsePanel`'s static catalogue tables: `RemapString` is a permutation of the eight string tables, icon tables are long enough for every `MaskBit` the panel actually looks up (the crash class is `CatIcons[category][cat.MaskBit]`), subcategory mask bits are distinct, and the wall-style arrays are parallel. |
| `Utils/AutoFollowTests.cs` | `AutoFollow`, the per-sim auto-follow decision and the engine slots it feeds: the slot constants and the two `VMGenericTS1CallMode` values it claims to drive (bound by `const`, so a rename in the engine breaks this file rather than drifting), slot <-> direction round trips, that `None`/`Both` resolve to no slot at all (slot 0 holds bladder), that `Available` is a total function over all 64 flag combinations and reports the most fundamental objection first, that the preferred direction flips with the lot, and the byte-level slot write: the entry shape the engine reads, replace-don't-accumulate, the other direction's slot and the travel motive slots left alone, and the null-inventory refusal. |
| `UI/UntestableBoundaries.cs.txt` | Documentation only - not compiled. Records the two pieces of logic that cannot be reached headlessly, and the exact extraction each one needs. |

## Deliberately not tested

| Logic | Why |
| --- | --- |
| `UIMainPanel.GetLotType(bool music)` | Instance method fused to `Game.vm` and `Content.Get()`. Recommended pure form and where it should live: see `UI/UntestableBoundaries.cs.txt`. |
| `UINeighborhoodSelectionPanel.PopulateScreen` location parsing | The parse loop sits in the middle of a method that loads textures. Same file, same treatment. |
| `UIJobSubpanel.ToTime(int)` | `private`. Making it reachable would mean changing production code, which is out of scope. The obvious latent bug (12 -> "12pm", 0 -> "0am", 13 -> "1pm") is noted rather than locked in. |
| `PersonGeneratorHelper.PreparePersonDataFromObject` | Builds a `World(GameFacade.GraphicsDevice)`. Hard no. |
| `UIIconCache`, `UICatalogItem.ClearIconCache`, `HITVM` audio calls | All require a graphics/audio context. |
| `UIAutoFollowPanel` layout and button states | Builds `UIDiagonalStripe` / `UIBigButton`, which load textures through `GameFacade.GraphicsDevice`. The decision behind the button states is `AutoFollow.Available` and is tested; only the drawing is not. |
| `UILotControl.RunAutoFollow` / `AnswerAutoFollowThread` | Reads a live `VMAvatar` and sends a `VMNetDialogResponseCmd`. The part of it that is data - which slot, which neighbour ID, refusing a re-queue - is `AutoFollow.TryQueueRequest` / `TryFindRequest` and is tested; the `vm.SendCommand` hop is not. |

## Running

The project is not yet in `Simitone.sln`; the orchestrator owns the solution file and builds.
Once wired in:

```
dotnet test Client\Simitone\Simitone.Tests\Simitone.Tests.csproj
```
