# Map Tool — Progress / Resume Notes

Pick-up notes for the map tool build. Full architecture in `MAPTOOL_DESIGN.md`.

## Status: Slice 2 VERIFIED ✅ (palette + painting works in editor)

Paint / erase confirmed working in the live editor. Two fixes landed during verify: tile mesh uses a **bottom-center pivot** so `CellToLocal` shifts X/Z by +0.5 but NOT Y (Blockbench export sits on its base); and the dock migrated to the **Godot 4.6 `EditorDock` API** (`MaptoolDock : EditorDock`, `AddDock`/`RemoveDock`, dock owns `Title`+`DefaultSlot`) — replaces the now-obsolete `AddControlToDock`. `MaptoolDock.cs` is `#if TOOLS` (EditorDock is editor-only).

**Gotchas hit during verify (not bugs):**
- Delete + re-add the `MapRenderer` node → you get a *fresh empty `MapData`* (0 cells) → renders nothing. The old map's cells don't follow a new node.
- Paint silently no-ops if no tile is selected in the dock (PlaceMode needs `CurrentTileId`).

**Decision resolved (was flagged):** plugin entry rewritten in **C#**, not GDScript. Whole addon is one C# assembly; a GDScript bootstrap would split the language boundary exactly where picking + undo live. `plugin.gd` deleted; `plugin.cfg` → `script="MaptoolPlugin.cs"`.

### New files (Slice 2)
- **`FuzzySearch.cs`** — private copy in `Moonbreak.Maptool` (NOT shared w/ console, per design).
- **`TileLibrary.cs`** — folder-scan of `res://Tiles/Definitions/*.tres` → `TileDefinition` list, cached. `Refresh()` to rescan. Dir lives in the GAME (defs reference game meshes) — keeps addon submodule-clean.
- **`CellPicker.cs`** — `PickResult` + Amanatides–Woo DDA voxel march through `MapData` (first solid cell + entry-face normal) with active-layer **plane fallback** for void placement. Pure math, local space, no colliders.
- **`MapEdit.cs`** — `RefCounted` reversible diff (cell, oldId, newId). `ApplyForward/Reverse(MapData)`. The undo unit; knows nothing about undo itself.
- **`IEditMode.cs`** + **`PlaceMode.cs`** (hit+face, or plane cell) + **`EraseMode.cs`** (clears hit cell only). Single-pick, commit-immediately.
- **`MaptoolDock.cs`** — `VBoxContainer` dock built in code: Place/Erase toggle, build-layer ±, fuzzy search box, tile `ItemList`, Refresh. Raises C# events; touches no core state.
- **`MaptoolPlugin.cs`** — `[Tool] EditorPlugin`, the ONLY editor-API holder. `_Handles/_Edit/_MakeVisible` track the selected `MapRenderer`; `_Forward3DGuiInput` left-click → `CellPicker` → active `IEditMode` → wrap `MapEdit` in `EditorUndoRedoManager` (`ApplyEdit` do/undo on the renderer). Manages the translucent active-layer plane (owner=null, meta-tagged, survives `Rebuild`).

### MapRenderer changes
- `ApplyEdit(MapEdit, bool forward)` — the undo funnel target (on the renderer so undo history anchors to the scene node).
- `BuildTileIndex` now seeds from `TileLibrary.GetAll()`; explicit `Tiles` array still overrides (for tests).

### Verify-in-editor checklist (DO FIRST next session)
1. Build inside Godot (not just CLI) + restart editor so the C# plugin loads.
2. Create a `TileDefinition` `.tres` in `res://Tiles/Definitions/` (set `Id`, assign a `Mesh`). No defs yet → palette empty + placed cells render magenta (expected, still proves picking/undo).
3. Select a `MapRenderer` (with a `Map`) in a 3D scene → "Map Tool" dock appears (RightUl). Pick a tile, **Place** mode, click in viewport → cell appears against the aimed face. Click empty space → cell drops on the build-layer plane (cyan). **Erase** removes the hit cell. **Ctrl+Z / Ctrl+Y** undo/redo. Saved `.tscn` stays tiny.

### Known gaps / deferred to next
- Layer change via viewport scroll-wheel NOT wired (would hijack camera zoom) — dock ± buttons only. Design mentions scroll; revisit w/ a modifier if wanted.
- No drag-paint (each click = 1 undo action). Box/Flood modes + ghost preview (`GetPreview`) still deferred.
- "+ New Tile" authoring flow in the dock NOT built — tiles hand-created as `.tres` for now.

---

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

## Next: Slice 3 — GridManager integration (the "pull")

Goal: game reads terrain from `MapData` instead of `GridMap`. One-directional (game→addon); addon stays game-agnostic. See design doc *GridManager integration*.

1. Replace `FindGridMap()` + `gridMap.GetUsedCells()` (in `Scripts/Singletons/GridManager.cs`) with reading the scene's `MapData`.
2. Walkable derivation unchanged: cell is walkable if solid and nothing at `cell + Up`.
3. **Match the half-cell offset:** `GridManager.CellToWorld` must mirror `MapRenderer.CellToLocal` (`+0.5` per axis). See gotcha #5.
4. Coordinate conversion → regular grid, cell size 1 (or read `MapRenderer.CellSize`).

Blocked on Slice 2 in-editor verification first (see checklist above).

### Also worth doing soon
- "+ New Tile" authoring in the dock (write `.tres` to `res://Tiles/Definitions/`) — removes the hand-create step.
- Box/Flood modes + ghost preview (`IEditMode.GetPreview` from the design's interface — not yet on our lean `IEditMode`).
