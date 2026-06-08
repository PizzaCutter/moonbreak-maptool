# Map Tool — Progress / Resume Notes

Pick-up notes for the map tool build. Full architecture in `MAPTOOL_DESIGN.md`.

## Status: Slice 1 DONE ✅ (data + render core)

Verified working in editor **and** at runtime: a hand-authored `MapData` renders as grid-aligned cubes, the saved `.tscn` stays tiny (no per-cell nodes serialized).

### Files (all in `addons/moonbreak_maptool/`)
- **`TileDefinition.cs`** — `[Tool][GlobalClass]` Resource. `Id` (immutable), `DisplayName`, `Mesh`, `Tags`, `bWalkable`. One `.tres` per tile.
- **`MapData.cs`** — `[Tool][GlobalClass]` Resource. Source of truth for terrain.
  - Serialized as `Palette` (`string[]` of tile Ids) + `PackedCells` (flat `x,y,z,paletteIndex` quads).
  - Runtime `Dictionary<Vector3I,int>` built lazily, flushed to `PackedCells` on every mutation.
  - API: `SetCell`, `SetCells` (batch, one flush), `ClearCell`, `HasCell`, `GetTileId`, `Enumerate`, `CellCount`.
- **`MapRenderer.cs`** — `[Tool][GlobalClass]` Node3D. Turns `MapData` → cubes.
  - `Map`, `Tiles` (tile library array), `CellSize`, plus a `Rebuild` tool button.
  - Speaks only in cell coords. Spawned meshes are `owner=null` (never serialized) and tagged `_maptool_visual`.
- `plugin.cfg`, `plugin.gd` — addon bootstrap (currently an empty EditorPlugin shell).

### How to test
New 3D scene → add `MapRenderer` node → `Map` = New MapData → set `Palette=["test"]`, `PackedCells=0,0,0,0, 1,0,0,0, 0,0,1,0, 1,0,1,0` → click **Rebuild** → 4 grid-aligned cubes (magenta = no mesh resolved, expected). Add a `TileDefinition` to `Tiles` with matching `Id` + a `Mesh` to replace magenta.

## Gotchas already solved (don't re-hit these)

1. **`[Tool]` cascades.** Every C# type a `[Tool]` script touches at edit-time must itself be `[Tool]`. Missing it on `MapData`/`TileDefinition` → `InvalidCastException: Resource → MapData` on assign.
2. **New `[GlobalClass]` types need an in-editor Build + editor restart** to appear in inspector "New …" menus. CLI `dotnet build` does NOT update `global_script_class_cache.cfg`.
3. **Editor doesn't re-run `_Ready` on data edits.** Preview refreshes only via the `Rebuild` button or re-assigning `Map` (setter triggers it). Edit modes will call `Rebuild()` automatically later.
4. **Hot-reload orphans.** Editor script reloads reset in-memory fields but leave spawned children alive. `Clear()` sweeps the live tree for `_maptool_visual`-tagged nodes and uses immediate `Free()` (not `QueueFree`). Pre-fix ghosts clear with a one-time scene reload.
5. **Half-cell offset.** `CellToLocal` shifts by `+0.5` per axis so cubes align to gridlines and floor cells sit on `y=0`. **`GridManager.CellToWorld` must match this when integrated.**

## Next: Slice 2 — palette + painting

Goal: place tiles by clicking in the editor viewport, no more hand-typing `PackedCells`.

Suggested order (see design doc sections *Cell picking*, *Edit modes*, *Height authoring*):
1. **Tile discovery** — folder-scan `TileDefinition` `.tres` into the renderer's tile library (replaces the manual `Tiles` array).
2. **Palette UI** — editor dock, grid of tiles, fuzzy search. **Copy `FuzzySearch` into the addon namespace** (`Moonbreak.Maptool`) — do NOT reference the console's copy.
3. **Picking** — DDA voxel ray-march through `MapData` + active-layer plane fallback. Pure math, no colliders.
4. **`IEditMode`** — start with `PlaceMode` + `EraseMode`; each returns a `MapEdit` diff.
5. **Undo** — wrap `MapEdit` in `EditorUndoRedoManager`, isolated to the plugin layer.

### Decision to make first next session
`plugin.gd` is GDScript. Viewport input (`_forward_3d_gui_input`) and `EditorUndoRedoManager` access live on the **EditorPlugin**. Decide: keep `plugin.gd` as a thin GDScript bootstrap that hands off to a C# tool, or rewrite the plugin entry in C#. Picking + undo both need this resolved.
