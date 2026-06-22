# Moonbreak Map Tool

Godot 4 editor plugin for painting 3-D tile maps. Select a tile from the palette, pick an edit mode, and click or drag in the 3-D viewport.

## Edit modes

| Button | Behaviour |
|--------|-----------|
| **Place** | Click-drag to fill a solid cuboid of cells. Single click places one cell. |
| **Erase** | Click-drag to erase a cuboid of cells. Single click erases one cell. |
| **Room** | Click-drag to build the outer walls of a rectangular room (XZ perimeter, full Y height). Interior cells are left empty. |
| **Flood** | Flood-fills all connected cells of the same tile, starting from the clicked cell. |

## Shortcuts

These shortcuts are active **while dragging** in Place, Erase, or Room mode.

| Key | Action |
|-----|--------|
| `Space` | Raise the end corner by one cell (increase Y height) |
| `Ctrl` | Lower the end corner by one cell (decrease Y height) |

Tap repeatedly or hold to step multiple cells. The ghost preview updates live so you can see the height before releasing.

## Workflow tips

- **Quick room sketch** — switch to Room, drag out the footprint on the build plane, then tap Space to extrude walls upward before releasing the mouse.
- **Build layer** — use the `−` / `+` buttons in the dock to shift the active horizontal plane. Place and Erase snap to this plane when clicking into empty space.
- **Undo / redo** — all edits are pushed to Godot's undo history (`Ctrl+Z` / `Ctrl+Shift+Z`).
