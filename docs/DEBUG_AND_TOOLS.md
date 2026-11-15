# Debug keys and developer tools

## `simitone.ini` switches

`simitone.ini` lives beside FreeSO's `config.ini` (`Documents\Simitone\`). One `Key=true|false` per line;
changes made by hand are read on the next launch.

| Key | Default | Effect |
|---|---|---|
| `DebugKeys` | `false` | Enables the developer hotkeys in the lot view: **F8** (lot thumbnail + architecture value dialog), **F9** (toggle the picture-in-picture window), **F11** (`VerifyAllCollision`). F12 (3D / hybrid view toggle) is a view shortcut and is not gated. |
| `PictureInPicture` | `true` | The picture-in-picture window opens by itself when a sim other than the one being played raises an event headline. Also the "Picture-in-Picture" row on the Options > Gameplay page. Opens for 5 s, at most once per 20 s overall and once per 90 s per sim; ignored for 3 s after a lot loads. |

## Regenerating the EOD layouts

The EOD panel layouts (`Client\Simitone\Simitone.Client\Content\UI\uiscripts\*.uis`) and the caption
block in `_f900_simitone.cst` are generated from `EODLayouts`. After changing it:

```powershell
Simitone.Windows.exe -regen-eod-layouts Client\Simitone\Simitone.Client\Content
```

This does not start the game. It rewrites the `.uis` files and prints the `.cst` lines to paste into
`_f900_simitone.cst`. `EODLayoutBuilderTests` fails until the committed files match.
