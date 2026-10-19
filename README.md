# Moonbreak Map Tool

Godot 4 editor plugin for painting 3-D tile maps. Select a tile from the palette, pick an edit mode, and click or drag in the 3-D viewport.

## Edit modes

| Key | Button | Behaviour |
|-----|--------|-----------|
| `Shift+1` | **Place** | Click-drag to fill a solid cuboid of cells. Single click places one cell. |
| `Shift+2` | **Erase** | Click-drag to erase a cuboid of cells. Single click erases one cell. |
| `Shift+3` | **Paint** | Click-drag to retile the cells that already exist in a cuboid — blockout first, materials after. Empty cells stay empty; each cell keeps its turn. With a surface selected it paints the floors you click. |
| `Shift+4` | **Pick** | Eyedropper: click a cell to select its tile, then the tool drops back to the mode you came from. Clicking a block's top takes the surface or vegetation on it, if any; clicking a side takes the block. |
| `Shift+5` | **Line** | Click-drag to place tiles in a straight line from anchor to endpoint. Diagonals stay one cell thick. |
| `Shift+6` | **Room** | Click-drag to build the outer walls of a rectangular room (XZ perimeter, full Y height). Interior cells are left empty. |
| `Shift+7` | **Circle** | Click to set center, drag to set radius. Places a disc on the XZ plane. Space / Ctrl extrude into a cylinder. Toggle **Hollow** to place only the ring wall. |
| `Shift+8` | **Flood** | Flood-fills all connected cells of the same tile, starting from the clicked cell. |
| `Shift+9` | **Select** | Click-drag a rectangle to select every cell in those columns, at any height — terrain, objects, spawns, surfaces and vegetation. Then move or delete it with the keys below. |

`Shift` + number row (by key position, so AZERTY works too) works while the 3-D viewport has focus and a MapRenderer is selected. The key table lives in `MaptoolDock.Modes`.

## Palette

- **Thumbnails** — the tile list is a grid of mesh previews (surfaces show their tint). Previews render in the background; *Refresh tiles* re-renders them.
- **Recent** — the row above the search holds the last 8 tiles used, current one lit. Click to reselect. `Shift+X` swaps to the previous tile.
- **Mask** — *Mask: only X* limits **Paint** and **Erase** to cells holding tile X: drag a big box and only the stone turns mossy. Set it with *Use selected* or `Shift+click` in **Pick**; *Clear* turns it off. The label turns orange while a mask is on. Placing modes ignore it.

## Shortcuts

These shortcuts are active **while dragging** in Place, Erase, Paint, Room, Line, or Circle mode.

| Key | Action |
|-----|--------|
| `Space` | Raise the end corner by one cell (increase Y height) |
| `Ctrl` | Lower the end corner by one cell (decrease Y height) |

Tap repeatedly or hold to step multiple cells. The ghost preview updates live so you can see the height before releasing.

## Selection

In **Select** mode, once a rectangle is selected (yellow box), the dock shows a **Selection** section: its bounds, three *Move by* fields (X / Y / Z in cells) and **Move**, **Delete**, **Deselect** buttons. Type the offset, press Move — one undo step. Y is up.

The same operations have keys in **Select** mode. Every key press is one undo step.

| Key | Action |
|-----|--------|
| Arrows | Move the selection one cell. Up pushes it away from the camera, Right to the camera's right — snapped to the nearest map axis, so it works from any angle. Hold to keep moving. |
| `PageUp` / `PageDown` | Move the selection up / down one level |
| `Delete` | Erase everything in the selection |
| `Escape` | Clear the selection |

Moving onto cells outside the selection overwrites them; undo brings them back. Each tile keeps its turn. Undo restores the map but not the yellow box — reselect after undoing a move.

## Rotation

| Key | Action |
|-----|--------|
| `R` | Turn the tile a quarter clockwise (seen from above) |
| `Shift+R` | Turn the tile a quarter counter-clockwise |

The ghost shows the turn before you click. Works in every placing mode; undo restores the old turn.
A tile whose definition sets **AttachDirection** (e.g. the ladder) turns itself to meet the wall face you click, Minecraft-style — no R needed. Clicking a floor or the void falls back to the R turn.
A tile whose definition sets **bPillar** (e.g. the oak log) works like a Minecraft log: click a top or bottom face and it stands upright, click a side face and it lies along that face's axis. To branch a tree, click the trunk's side.

In **Place** mode, a right-click (press and release without moving the mouse) erases the cell under the cursor, Minecraft-style. Holding right mouse and dragging still flies the camera.

## Surfaces (overlay layers)

A tile whose definition sets **Layer** (the `Surface: …` tiles) paints onto that layer instead of terrain. Every placing mode works: Place onto a floor's top face, Line, Circle, Flood a whole room. Only standing cells right above a floor take paint. A right-click with a surface selected lifts the surface off the floor you point at and never erases the ground. **Erase** follows the same rule: with a surface or vegetation tile selected, it clears that whole layer (any value on it) from the floors in the box and leaves the blocks alone. Undo covers it like any edit. The tinted plates are editor-only; in game the layer's meaning (e.g. a SurfaceState) is applied by the game.

## Workflow tips

- **Quick room sketch** — switch to Room, drag out the footprint on the build plane, then tap Space to extrude walls upward before releasing the mouse.
- **Build layer** — use the `−` / `+` buttons in the dock to shift the active horizontal plane. Place and Erase snap to this plane when clicking into empty space.
- **Undo / redo** — all edits are pushed to Godot's undo history (`Ctrl+Z` / `Ctrl+Shift+Z`).
