![image](https://user-images.githubusercontent.com/6294155/29047328-2bb60b54-7bc3-11e7-8d88-cee5309495ed.png)

Note: This is a forked version of Simitone with some updates by alexjong that are not in the current version at the time of writing. Additionally, I fixed several bugs and included missing features. Most things from the original Sims 1 games should work now.


Alternative C# Windows Frontend for The Sims 1, based off of FreeSO. http://freeso.org 
(***REQUIRES*** a legitimate copy of The Sims: Complete Collection)

![image](https://user-images.githubusercontent.com/6294155/68995217-112b2680-0883-11ea-9f92-1acc839a7ec0.png)


*Only for Desktop Windows.* Other platforms cannot be officially supported.

# Purpose

On modern operating systems, The Sims has a few nagging issues that make it less than playable. Simitone can be seen as a tool to improve the playability of your existing installation of The Sims. Here are some features:

- Playable in 3D - using a combination of models generated from the 2D sprites and created by the community.
  - [Download the mesh pack here!](http://forum.freeso.org/forums/3d-remeshing.40/)
- Custom user interface that works at modern resolutions. Working on a more desktop oriented interface.
- Improved graphical performance, support for high resolutions and refresh rates.
- Custom lighting - directional lights with smooth falloffs and shadows using generated 3D meshes.
- Quality of life fixes from later installations of the series, such as the eyedropper tool (does
  not work on wallpaper or fences).
- *Volcanic*, a program which allows you to examine, modify and create new game objects. (from FreeSO)

# Building

Requires the .NET SDK pinned in [`global.json`](global.json) (`10.0.100`, rolling
forward to the latest 10.0 feature band) and a legitimate The Sims: Complete
Collection install — pass `-path:"C:\Path\To\The Sims"` or place a `The Sims`
folder next to the executable.

```powershell
dotnet build Client\Simitone\Simitone.sln -c Release
dotnet test  Client\Simitone\Simitone.Tests\Simitone.Tests.csproj -c Release
```

**Close the game before rebuilding.** A running Simitone holds handles on the
output folder, and MSBuild's error for a locked file is `MSB3021`/`MSB3025`
pointing at MSBuild rather than at the game. The build's own filesystem steps
warn and carry on rather than failing, and the trim tells you by name which files
it could not remove, so a stale `bin\` is visible instead of silent — but a
release layout needs a clean folder to build into.

The community 3D remesh pack is an optional addon that replaces auto-generated meshes with
hand-authored ones. If you have the pack checked out at `FSO.Remeshes-main/` in the repo
root, the Release build flattens it into `Content/MeshReplace/` automatically (with the
pack's `metadata.json` aliases, `game` filters and priority rules). Point at a different
checkout with `-p:RemeshPackDir=<path-to-remeshes>`, or skip it entirely with
`-p:RemeshPackDir=`. Without the pack the game still runs, using generated meshes.

The Release build also trims non-Windows natives (and, with `-p:BuildIde=true`, the TSO server
stack) that a singleplayer build never loads (`-p:TrimSingleplayerOutput=false` to keep them,
which is required for the `-ide` Volcanic host). Both that trim and the remesh packaging are
Release-only, so a Debug build keeps everything in `bin\Debug` and can be stepped into. The trim is verified after it runs: it names any allowlist path that stopped
matching, any file that survived a delete, and any file in the output that still belongs to
the server stack.

### The default build is the game; `-ide` is a flag

The build above compiles the game and **`FSO.Content.TSO`** (FreeSO's bundle of The Sims Online's
own data: 1,405 files, 284 MB of `Content\Objects`, `Globals`, `Avatar`, `Patch` ... - it is
content only, with no code dependencies, and the game needs it: cooked food and object-on-object
placement read it). It does **not** compile FreeSO's TSO online client (`FSO.Client`), its server
projects or the Volcanic IDE host (`FSO.IDE`): they are about 19 projects and a third of the
build's warnings, and none of them survives into a singleplayer output. They are behind a switch:

```powershell
dotnet build Client\Simitone\Simitone.sln -c Release -p:BuildIde=true
```

Add `-p:TrimSingleplayerOutput=false` to that as well if you want the server assemblies left
in the output, which a Debug build already does. The split is verified rather than assumed:
the build reads the resolved runtime graph out of `Simitone.deps.json` and **fails** if any of
the IDE-host / TSO-server assemblies is reachable from a default build, so a game that needed
one is a build error rather than a first-launch `TypeLoadException`.

### The content contract

`Simitone.Windows.csproj` carries an explicit list (`SimitoneRequiredContent`, checked by
`_SimitoneContentContract` after every build) of the files and folders the game opens **by
name**: the TSO content folders above, the neighbourhood's road atlas marker, the string table,
the weather clips, the eyedropper cursor, the CAS and EOD art, the licence text. If one is missing
from the output the build **fails**; a new startup dependency goes on that list, and from then on
the build will not go green without a source for it.

### There is no MonoGame content build

FreeSO's `MonoGame.Content.Builder.Task` step and its `.mgcb` compile are commented out in
`FSO.Content.csproj`, deliberately, and must stay that way. Simitone loads raw
`.iff` / `.fsom` / `.piff` straight off the player's disk and produces no `.xnb` of its own,
so enabling MGCB would add a content pass to every build for no output and reintroduce a
build step whose 2016-era binaries (`msdfgen.exe`, `zlib1.dll`) are not even in this tree.
(The only `.xnb` files in the output are three pre-built fonts FreeSO ships under
`Content/Fonts`.) A comment in that project file says the same thing, so this does not get
"helpfully" un-commented.

## Generating meshes for objects without a remesh

Most objects have no hand-authored remesh and fall back to the auto-generated mesh. Those
can be reconstructed offline from the game's own sprites with a calibrated space-carving
algorithm (see `docs/MESH_GENERATION_RESEARCH.md`):

```powershell
.\generate-meshes.ps1 -SimsPath "C:\Program Files\Sims"          # writes .\GeneratedMeshes
.\generate-meshes.ps1 -InstallOutput                             # also merge into the built game
```

The script builds `Simitone.GenerateMeshes`, walks the install's object archives, skips
every drawing group the community pack already covers (so hand-authored work is never
overwritten), and validates each output by reading it back. It is a manual authoring tool,
not part of the build. See `.\generate-meshes.ps1 -?` for options.

# Why is it called Simitone?

Simitone -> Semitone -> musical term -> C# -> a note

Further questions can be directed at my PR manager, uh, ... burglar cop.

# Attributions

Eyedropper tool icon: <a target="_blank" href="https://icons8.com/icon/78728/color-dropper">eyedropper</a> icon by <a target="_blank" href="https://icons8.com">Icons8</a>

Simitone is built on [FreeSO](https://freeso.org), which is
[MPL-2.0](https://www.mozilla.org/en-US/MPL/2.0/). Because Simitone's game code
is a derivative of FreeSO's `tso.client` / `tso.simantics` / `tso.world`
sources and the build output ships the resulting `FSO.*` assemblies, Simitone as
a whole is distributed under MPL-2.0: see [`LICENSE`](LICENSE). Attribution for
every third-party component, and Simitone's unofficial/no-EA-assets position,
is recorded in [`NOTICE`](NOTICE). Both files are copied into the build output,
so a release zip carries its own licence.

Simitone is an unofficial fan project and is not affiliated with or endorsed by
Maxis or Electronic Arts. It requires you to supply your own legitimate copy of
The Sims: Complete Collection; no EA game data is included here or in any build,
and none of the licence above grants any right in it.

