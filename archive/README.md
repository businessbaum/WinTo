# Archive

Modules taken out of the app but kept for later. Nothing here is built, tested or shipped.

| Module | Archived | Why |
| --- | --- | --- |
| `airplay` | 0.13.0 | Depends on an external receiver program; kept to work on later |
| `bluetooth-audio` | 0.13.0 | Connecting moved into the control center, the connection notice into the app as the "Headphone notice" feature |
| `game-mode` | 0.13.0 | Became the "Auto game mode" feature; power plans are in the control center. Never committed before archiving |
| `clipboard-tray` | 0.13.0 | The Shelf. Replaced by the "Clipboard history" feature, which fills itself instead of needing a window |

## Bringing one back

1. Move the folder back to `src/modules/<id>`; the folder name is the module id.
2. Add its `.csproj` to `MultiTool.slnx`.
3. Check `minHostVersion` in its `module.json` against the current contract, then build and run
   `tools\build.ps1 -SelfTest`.

`src/modules/Directory.Build.props` and `.targets` only apply under `src/modules`, which is why an
archived module doesn't build where it is.
