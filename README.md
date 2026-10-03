# Moonbreak Map Tool

Godot 4 editor plugin for painting 3-D tile maps. Select a tile from the palette, pick an edit mode, and click or drag in the 3-D viewport.

## Edit modes

| Button | Behaviour |
|--------|-----------|
| **Place** | Click-drag to fill a solid cuboid of cells. Single click places one cell. |
| **Erase** | Click-drag to erase a cuboid of cells. Single click erases one cell. |
| **Line** | Click-drag to place tiles in a straight line from anchor to endpoint. Diagonals stay one cell thick. |
| **Room** | Click-drag to build the outer walls of a rectangular room (XZ perimeter, full Y height). Interior cells are left empty. |
| **Circle** | Click to set center, drag to set radius. Places a disc on the XZ plane. Space / Ctrl extrude into a cylinder. Toggle **Hollow** to place only the ring wall. |
| **Flood** | Flood-fills all connected cells of the same tile, starting from the clicked cell. |

## Shortcuts

These shortcuts are active **while dragging** in Place, Erase, Room, Line, or Circle mode.

| Key | Action |
|-----|--------|
| `Space` | Raise the end corner by one cell (increase Y height) |
| `Ctrl` | Lower the end corner by one cell (decrease Y height) |

Tap repeatedly or hold to step multiple cells. The ghost preview updates live so you can see the height before releasing.

## Rotation

| Key | Action |
|-----|--------|
| `R` | Turn the tile a quarter clockwise (seen from above) |
| `Shift+R` | Turn the tile a quarter counter-clockwise |

The ghost shows the turn before you click. Works in every placing mode; undo restores the old turn.
A tile whose definition sets **AttachDirection** (e.g. the ladder) turns itself to meet the wall face you click, Minecraft-style — no R needed. Clicking a floor or the void falls back to the R turn.

In **Place** mode, a right-click (press and release without moving the mouse) erases the cell under the cursor, Minecraft-style. Holding right mouse and dragging still flies the camera.

## Surfaces (overlay layers)

A tile whose definition sets **Layer** (the `Surface: …` tiles) paints onto that layer instead of terrain. Every placing mode works: Place onto a floor's top face, Line, Circle, Flood a whole room. Only standing cells right above a floor take paint. A right-click with a surface selected lifts the surface off the floor you point at and never erases the ground. Undo covers it like any edit. The tinted plates are editor-only; in game the layer's meaning (e.g. a SurfaceState) is applied by the game.

## Workflow tips

- **Quick room sketch** — switch to Room, drag out the footprint on the build plane, then tap Space to extrude walls upward before releasing the mouse.
- **Build layer** — use the `−` / `+` buttons in the dock to shift the active horizontal plane. Place and Erase snap to this plane when clicking into empty space.
- **Undo / redo** — all edits are pushed to Godot's undo history (`Ctrl+Z` / `Ctrl+Shift+Z`).
