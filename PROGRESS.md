# Map Tool — Progress / Resume Notes

Pick-up notes for the map tool build. Full architecture in `MAPTOOL_DESIGN.md`.

## Status board

| Slice | What | State |
|---|---|---|
| 1 | Data + render core (`MapData`, `MapRenderer`, `TileDefinition`) | ✅ done |
| 2 | Palette + painting (dock, picking, place/erase, undo) | ✅ verified in editor |
| 3 | GridManager "pull" — game reads `MapData` not `GridMap` | ✅ done (GridMap node removed, runtime confirmed) |
| 3b | Input picking — mouse → terrain cell via DDA (entities still physics) | ✅ done (builds; quick playtest pending) |
| **4** | **Object layer — trees/barrels as PackedScene obstacles** | ✅ done |
| 5a | Ghost preview — hover cell highlights what the mode would paint | ✅ done |
| **5b** | **Box fill + flood fill edit modes** | ✅ done |

Build is green (`dotnet build Riminity.csproj`, 0/0). Detailed per-slice notes below; **the Slice 4 plan is at the very bottom.**

---

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
5. **Hot-reload corrupts the plugin + tool-resources (the "all pink cubes + paint dead + `_mode is invalid`" trio).** A background C# rebuild reloads the assembly and reconstructs `MaptoolPlugin` *without* re-running `_EnterTree`:
   - Any field only assigned in `_EnterTree` comes back null. Fixed for the mode by deriving `ActiveMode` from `_placeMode`/`_eraseMode` (field initializers → survive reload) + a `_bErase` bool, instead of caching `_mode`. Never null now.
   - `_dock` is null after reload and its event wiring is gone; the dock still on screen is the orphaned pre-reload one, delegates pointing at a dead plugin → tile-pick no longer sets `CurrentTileId` → paint silently dead.
   - `[Tool][GlobalClass]` resources (`MapData`/`TileDefinition`) get re-typed → in-memory instances become bare `Resource`, so `ResolveMesh`'s `is TileDefinition` fails for every cell → all magenta.
   - **No code makes an `EditorPlugin` survive a C# hot-reload cleanly (engine limitation).** Cure: **restart the editor** (or toggle the plugin off/on) after a C# build. Avoid leaning on background auto-build while the maptool is live — build deliberately, restart, then paint.
6. **Half-cell offset.** `CellToLocal` shifts by `+0.5` per axis so cubes align to gridlines and floor cells sit on `y=0`. **`GridManager.CellToWorld` must match this when integrated.**

## Slice 3 — GridManager integration (the "pull") — DONE ✅

`Scripts/Singletons/GridManager.cs` reads terrain from `MapRenderer.Map` (`MapData`) instead of `GridMap`. One-directional: `using Moonbreak.Maptool;` in the game; addon references nothing game-side. Old `GridMap` node removed from `World.tscn`; grid generates correctly at runtime.

What changed (all internal — public `CellToWorld`/`WorldToCell` signatures + world coords unchanged, so the ~40 call sites across Character/AI/Actions are untouched):
- `_gridMap` field → `_renderer` (MapRenderer). Node detection (`OnNodeAdded`/`OnNodeRemoved`/`FindMap`) keys off `MapRenderer` by type (`FindRenderer` recursive scan — node can be named anything).
- `BuildGraph(MapRenderer)` derives walkable cells from `map.Enumerate()` (solid + nothing at `cell+Up`). Same derivation, new source.
- `CellToWorld`: X/Z `+0.5` (matches `MapRenderer.CellToLocal` pivot), Y on top face (`cell.Y + 1`). Mathematically identical to the old GridMap output → no behaviour change for callers.
- `WorldToCell`: inverse via `_renderer.ToLocal` + floor, `-0.01` Y bias. `InvalidateCell` reads `map.HasCell`.

## Slice 3b — Input picking (terrain has no colliders) — DONE ✅

MapData terrain has no physics colliders by design, so the old `ObjectUnderMouse` raycast can't find the ground cell. Split the picking: **entities = physics, terrain = voxel march** (the "double setup", now intentional).
- `CellPicker.Pick` gained `allowPlaneFallback` (default true for editor; gameplay passes false → clean miss off-map).
- `GridManager.TryPickCell(camera, screenPos, out cell)` — façade: mouse ray → renderer-local → DDA through `MapData` → solid cell. No plane fallback.
- `Scripts/InteractionManager.cs` — 4 ground-pick sites swapped to `TryPickCell` (left-click move, `TryGetHoverCell`, skill preview, hover path preview). Character select / right-click inspect+interact / occupant targeting still use `ObjectUnderMouse` (entities are real colliders).
- **Known soft edge (accepted):** `TryPickCell` returns the solid cell the ray strikes; clicking the vertical *side* of a tall ledge returns that side cell (no-ops if unreachable). Snap-to-column-top deferred — revisit if it feels wrong on terraced maps.

### Quick playtest still pending (not blocking)
Run game → left-click moves on painted terrain, hover shows path+AP, skills target cells+entities, character select works. Spawns may need nudging onto the new terrain shape.

---

## Slice 4 — Object layer — DONE ✅

**New files:**
- `Scripts/Objects/GridObstacle.cs` — component node. Add as child of any `StaticBody3D` that blocks the grid. Self-registers on `GraphBuilt`, frees cell on `Health.OnDepleted`, exposes `MoveTo` for `ThrowAction`. Authoring: drop any StaticBody3D into the scene and add GridObstacle + Health children.
- `Prefabs/Prefab_Crate.tscn` — crate obstacle using `Object_Crate.gltf` mesh + Health + GridObstacle + InteractionComponent + AttackAction. No extra script needed.

**Changed files:**
- `Scripts/Objects/ExplodingBarrel.cs` — stripped direct grid registration; delegates to GridObstacle child for cell state and MoveTo. Only retains explosion-specific AOE logic.
- `Prefabs/Prefab_ExplodingBarrel.tscn` — added GridObstacle child node.
- `Scripts/Actions/ThrowAction.cs` — generalized throw target from `is ExplodingBarrel` to `GetNodeOrNull<GridObstacle>("GridObstacle")` — works for any obstacle.
- `Scenes/World.tscn` — placed one Crate at ~(20.5, 1.5, 7.5) for playtest.

**Authoring convention (Option A settled):** hand-place in scene. No editor object-mode needed — the GridObstacle component makes any StaticBody3D a grid obstacle by default.

**Verify steps:**
1. Build + restart editor so GridObstacle.cs gets a UID.
2. Run World.tscn → crate blocks pathfinding. Attack it to 0 HP → cell becomes walkable, units can path through.
3. Throw a barrel → `ThrowAction` still works via GridObstacle.

**Gotchas:**
- `GridObstacle._Ready()` runs before `ExplodingBarrel._Ready()` (children first). So GridObstacle subscribes to `OnDepleted` first → `Unregister()` + `InvalidateCell()` fire before `Explode()`. `Explode()` reads `_gridObstacle.Cell` (still valid post-unregister) and does AOE only — no duplicate grid-freeing.
- Crate UID in scene files is placeholder (`uid://cprefcrate001`). Godot rewrites it on first open — safe.

## NEXT SESSION → Slice 5 — Editor polish (deferred from Slice 2)

Terrain (bulk static cells) is done. Objects = sparse, destructible things that are real nodes, NOT MapData cells. See `MAPTOOL_DESIGN.md` "Layers" + "Objects".

**Goal:** trees / barrels as `PackedScene` nodes that self-register as grid obstacles, block LOS, and become walkable on death — reusing the existing `ExplodingBarrel` pattern (`Scripts/Objects/ExplodingBarrel.cs`).

**Pattern to follow (already proven by ExplodingBarrel):**
- Real `StaticBody3D` + `Health` child. Has a collider → entity picking (physics) already handles selection/interaction. No DDA work needed for objects.
- On `GridManager.GraphBuilt` (signal exists): compute its cell via `WorldToCell(GlobalPosition)` and call `GridManager.RegisterObstacle(cell, this)`.
- On death (`Health.OnDepleted`): `UnregisterObstacle(cell)` → cell becomes walkable again. Trees stack at `cell + Up`, block LOS via existing Bresenham (`HasLineOfSight` already checks `_obstacles`).

**Open questions to resolve first next session:**
1. **Authoring:** how do objects get placed? Option A — hand-place in the scene like barrels are now (simplest, ships the prototype). Option B — an "object mode" in the map tool dock that instantiates a chosen PackedScene at the picked cell (matches the design's tool ownership, more work). *Lean A for now, B when pain shows.*
2. **Where object PackedScenes live + how the tool lists them** (mirror `TileLibrary` folder-scan for object prefabs? or just a scene-author concern?). Tied to Q1.
3. Does the existing `ExplodingBarrel` already register correctly against the new `MapData`-sourced graph? Verify its `GraphBuilt` hook + `WorldToCell` still land on the right cell on the terraced map before generalizing the pattern to trees.

**Lower priority / deferred (pick up when relevant):**
- `TileDefinition.bWalkable` not yet consulted (water/lava non-walkable tiles) — current walkable rule is purely geometric.
- Editor authoring polish: "+ New Tile" dock flow (write `.tres` to `res://Tiles/Definitions/`); Box/Flood fill modes + ghost preview (`IEditMode.GetPreview`, not on our lean interface yet); scroll-wheel layer change.
- Side-hit → column-top snap in `TryPickCell` (see Slice 3b soft edge).
