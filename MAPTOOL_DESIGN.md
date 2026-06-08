# Map Tool — Design

Custom in-editor map builder. Replaces Godot `GridMap`. Goal: kill the GridMap pain — hidden height workflow, repacking a master scene to add tiles, and the 166k-token `World.tscn` cell blob.

Designed as a self-contained Godot addon, extractable later as its own git submodule/repo.

## Core principles

- **Storage ≠ rendering ≠ behavior.** Three separate concerns, never conflated. That conflation is what made GridMap-as-scene-blob painful.
- **One-way dependency.** Game depends on addon. Addon NEVER imports game code (`GridManager`, `Character`). This is the rule that makes submodule extraction clean.
- **No shared code with the console.** `FuzzySearch` gets *copied* into the addon's own namespace, not referenced. Console and map tool stay fully separate.
- **Evolve when pain shows.** Start with the simplest representation; the architecture isolates the swap points.

## Layers (two-layer map model)

| Layer | What | Stored as | Why |
|---|---|---|---|
| **Terrain** | static ground/wall cells, thousands | `MapData` resource (compact) | bulk, no behavior, can't be nodes |
| **Objects** | trees, barrels, doors — sparse, destructible | real `PackedScene` scene nodes | each needs `Health`, collision, obstacle registration |

A "map" is therefore **two artifacts**: `MapData.tres` (terrain) + the scene's object nodes. Objects reuse the existing `ExplodingBarrel` pattern — real `StaticBody3D` + `Health` child, self-registers as a grid obstacle on `GraphBuilt`, unregisters on death. Trees stack on terrain at `cell + Up`, block LOS via existing Bresenham, become walkable on death.

## Data model

### `TileDefinition : Resource` (one `.tres` per tile)

```
[Export] string   Id           // immutable, set once at creation, NEVER renamed
[Export] string   DisplayName  // freely editable label
[Export] Mesh     Mesh         // carries its own material
[Export] string[] Tags         // fuzzy-search corpus
[Export] bool     Walkable     // core to tactics
```

- **Discovery:** plugin scans `res://.../Definitions/*.tres` on load. Drop a file in → it appears. No master registry to maintain. (This is the GridMap pain that's being fixed.)
- **Tile creation owned by the tool.** Palette UI "+ New Tile": pick mesh, name, tags → tool writes the `.tres`. Never hand-author a resource.
- **`Id` immutable / `DisplayName` mutable** is load-bearing. "Renaming" = editing `DisplayName` only → zero map impact. Tool forbids editing `Id` after creation.

### `MapData : Resource` (palette + index, Minecraft-style)

- Small **palette**: `string[]` of tile `Id`s this map uses.
- Per-cell storage: `ushort` index into the palette (packed array). A 10k-cell map = one `PackedByteArray`, not 11k path strings.
- **Add** tile to map → append `Id` to palette, O(1).
- **Rename** → free (palette holds immutable `Id`, only `DisplayName` changes).
- **Remove from map** → clear cells; unused palette entry left as-is (compaction deferred).
- **Delete `.tres`** while referenced → tool warns ("N maps use this"); dangling `Id` renders as a **magenta missing-tile placeholder** on load. Never crashes.

## Rendering — `MapRenderer`

- **Now:** one `MeshInstance3D` per cell. Simplest, per-cell highlight trivial. Fine at tactics-arena scale.
- **Later (on perf pain):** one `MultiMeshInstance3D` per tile-type. One draw call per type.
- **Swap is cheap by construction** as long as the rule holds: *nothing outside `MapRenderer` ever holds a reference to a per-cell visual node.* Everyone speaks coordinates — `SetCell(cell, tile)`, `Highlight(cell)`, `Rebuild()`. Then option-1→2 is "rewrite one class's internals," no ripple. Highlight overlay reuses the `GridDebugOverlay` pattern.
- **Non-negotiable:** spawned visual nodes are **`owner = null`** (non-serialized). They exist only at runtime / `@tool` preview, NEVER written into the `.tscn`. Scene holds one `MapRenderer` node; `MapData.tres` holds the cells. Renderer regenerates visuals from `MapData` on load and every edit.

## Cell picking — pure math, no colliders

One mouse ray crosses many stacked cells. Resolution (all reads `MapData`, zero collision shapes — keeps renderer a black box, keeps MultiMesh swap cheap):

- **Primary:** DDA voxel ray-march through `MapData` → first solid cell + the **face normal** hit (~30 lines, standard voxel pick).
- **Void fallback:** no DDA hit → ray ∩ **active-layer plane** → cell on current Y. Lets you paint floors / build into empty space.

## Height authoring

The direct fix for the GridMap pain. Active layer is dragged out of hiding (`_editor_floor_`) into an obvious UI control.

- **Active layer:** persistent visible translucent grid plane at current Y, big "Layer: N" readout, scroll-wheel / +/- to raise-lower. Spatial anchor + the surface you build against in the void.
- **Place (Minecraft):** new cell at `hit + faceNormal`. Build against the face you're aiming at.
- **Erase:** removes the DDA-hit cell itself.
- **Raise/Lower-column brush:** bumps the top of each hovered column ±1. The primary elevation-sculpting tool for terraced tactics maps.
- **Walls stay generic:** tool just stacks solid cubes, has NO "wall" concept. Climbable ledge vs unclimbable wall is `GridManager`'s call (existing dy±1 logic). Keeps tool game-agnostic.

Prototype scope: mostly-flat maps with occasional elevation. Build the machinery, keep multi-story editing UI minimal until pain shows.

## Edit modes — `IEditMode` (mirrors `IAction`)

One class per mode, discovered like `IAction` children. Lets new dev-speed modes be added cheaply.

```
interface IEditMode {
    string Name;
    void OnPick(Vector3I cell, Vector3I face);  // a click landed
    EditPreview GetPreview();                    // cells-to-change, for ghost render
    MapEdit Commit();                            // the actual MapData diff
    void Cancel();                               // reset (e.g. clear point A)
}
```

- **PlaceMode / EraseMode** — single pick, commit immediately.
- **BoxFillMode / FloodFillMode** (WorldEdit-style) — first pick stores point A + previews; second pick commits the cuboid / contiguous region. Variants: solid-fill, walls-only, outline, flood.
- **Uniform preview:** every mode reports "cells I would change"; tool ghost-renders them (GridDebugOverlay-style). WorldEdit box shows before the second click.
- **Uniform commit:** every mode produces one `MapEdit` (diff: cells + old tiles + new tiles). Single funnel = the undo unit.
- **Modes stay pure** — produce a `MapEdit`, know nothing about undo.

## Undo/redo — `EditorUndoRedoManager`

- Native Ctrl+Z / Ctrl+Y, free redo, native scene-dirty + save.
- Terrain `MapEdit` → one action: `do` = apply new tiles, `undo` = apply old tiles. Object placements → node add/remove. One unified history across terrain + objects + rest of editing.
- **Dependency isolated to the plugin layer only.** `IEditMode.Commit()` returns a pure `MapEdit`; only the editor-plugin driver wraps it in `create_action` / `add_do_method` / `add_undo_method`. Portable core (`MapData`, `MapRenderer`, `TileDefinition`, `IEditMode`) stays editor-independent — a future in-game editor reuses the core and swaps only the undo driver.

## GridManager integration — pull

- Replace `FindGridMap()` + `gridMap.GetUsedCells()` with reading `MapData` (game reaches into addon; addon emits nothing game-specific).
- Walkable derivation unchanged: a cell is walkable if solid and nothing at `cell + Up`.
- Replace `MapToLocal/LocalToMap/CellSize` coordinate conversion with addon equivalents (regular grid, cell size 1).
- One-directional: game→addon. Addon never references `GridManager`.

## Addon boundary (submodule contents)

**Addon owns:** `TileDefinition`, `MapData`, `MapRenderer`, `IEditMode` + modes, editor plugin + palette UI, private copy of `FuzzySearch`.
**Game owns:** `GridManager` (reads `MapData`), object prefabs (trees/barrels).

## Deferred

- MultiMesh rendering (until perf pain).
- Palette compaction.
- Multi-story editing UI.
- In-game / user-content editor (reuses core, swaps undo driver + input).