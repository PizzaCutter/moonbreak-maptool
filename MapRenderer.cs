using Godot;
using System.Collections.Generic;

namespace Moonbreak.Maptool
{
    // Turns MapData into visible geometry. Speaks ONLY in cell coordinates — nothing outside
    // ever holds a reference to a per-cell node. That black-box rule is what keeps the future
    // swap to MultiMesh a contained change.
    //
    // Spawned meshes are intentionally left with Owner = null so they are NOT serialized into
    // the .tscn. The scene holds one MapRenderer node; MapData.tres holds the cells. Visuals are
    // regenerated from MapData on load and on every edit.
    [Tool]
    [GlobalClass]
    public partial class MapRenderer : Node3D
    {
        private MapData _map;
        [Export] public MapData Map
        {
            get => _map;
            set
            {
                _map = value;
                // Auto-preview when (re)assigned in the editor. Guarded so it doesn't fire
                // mid-deserialization before the node is in the tree (_Ready handles load).
                if (IsInsideTree())
                {
                    Rebuild();
                }
            }
        }

        // Tile library: resolves a MapData palette Id to its mesh. (Folder-scan discovery comes
        // with the palette UI; an explicit array is enough to light up the core.)
        [Export] public Godot.Collections.Array<TileDefinition> Tiles { get; set; } = new();

        [Export] public float CellSize { get; set; } = 1f;

        [ExportToolButton("Rebuild")] public Callable RebuildButton => Callable.From(Rebuild);

        // Neighbor update (Minecraft-style): fired after every rebuild and every applied edit, so
        // anything shaped by its surroundings (a fence joining its neighbors) can re-check them.
        // Parameterless on purpose — listeners are few and cheap to refresh wholesale.
        [Signal] public delegate void MapChangedEventHandler();

        // Marks nodes this renderer spawned, so Clear() can sweep the live tree for them —
        // robust against editor script hot-reloads that orphan the previous batch.
        private const string VisualMeta = "_maptool_visual";

        private Dictionary<string, TileDefinition> _tileById;
        private Mesh _missingMesh;

        // Key under which all unresolved-Id cells share one (magenta) batch.
        private const string MissingKey = "__missing__";

        // One MultiMesh batch per tile-mesh — N cells collapse to ~palette-size draw calls/nodes.
        private readonly Dictionary<string, TileBatch> _batches = new();
        // cell -> which batch holds it and at what instance index. The index map is what swap-pop
        // removal keeps in sync, so single-cell edits stay O(1) with no full rebuild.
        private readonly Dictionary<Vector3I, (string key, int index)> _cellLoc = new();
        // Object tiles (TileDefinition.Scene set): one spawned scene instance per cell. Owner stays
        // null like the batches — MapData is the record, the node is regenerated. At runtime the
        // game may free or move these (destroyed crate, shoved barrel); the map never notices.
        private readonly Dictionary<Vector3I, (string tileId, int turns, Node3D node)> _objects = new();

        public override void _Ready()
        {
            Rebuild();
        }

        public void Rebuild()
        {
            Clear();
            if (Map == null)
            {
                return;
            }

            BuildTileIndex();

            foreach (var (cell, tileId) in Map.Enumerate())
            {
                AddCell(cell, tileId);
            }

            GD.Print($"MapRenderer: rebuilt {Map.CellCount} cells");
            EmitSignal(SignalName.MapChanged);
        }

        // --- Single-cell mutation hooks (used by edit modes later) ---

        public void SetCell(Vector3I cell, string tileId)
        {
            if (Map == null)
            {
                return;
            }
            Map.SetCell(cell, tileId);
            EnsureTileIndex();
            UpdateCell(cell);
        }

        public void ClearCell(Vector3I cell)
        {
            Map?.ClearCell(cell);
            RemoveCell(cell);
        }

        // Undo funnel: the editor-plugin layer routes every terrain diff through here via
        // EditorUndoRedoManager do/undo. forward=true applies new tiles, false restores old.
        // Kept on the renderer (not the plugin) so the call target is the scene node the undo
        // history is anchored to. Marshalable signature (MapEdit RefCounted + bool) for Variant args.
        public void ApplyEdit(MapEdit edit, bool forward)
        {
            if (Map == null || edit == null)
            {
                return;
            }
            if (forward)
            {
                edit.ApplyForward(Map);
            }
            else
            {
                edit.ApplyReverse(Map);
            }
            // Touch only the cells the diff changed — the whole point of Stage 1. The map is the
            // source of truth post-apply, so re-read each cell and add/update/remove its node.
            EnsureTileIndex();
            foreach (var cell in edit.TouchedCells)
            {
                UpdateCell(cell);
            }
            EmitSignal(SignalName.MapChanged);
        }

        // --- Terrain vs object queries (the game reads these; objects are not terrain) ---

        public bool IsObjectTile(string tileId)
        {
            EnsureTileIndex();
            return tileId != null && _tileById.TryGetValue(tileId, out var def) && def.Scene != null;
        }

        // Filled with a terrain tile (an unresolved Id counts as terrain — it renders as a block).
        public bool IsTerrainCell(Vector3I cell)
        {
            string tileId = Map?.GetTileId(cell);
            return tileId != null && !IsObjectTile(tileId);
        }

        public IEnumerable<(Vector3I cell, string tileId)> EnumerateTerrain()
        {
            if (Map == null)
            {
                yield break;
            }
            foreach (var (cell, tileId) in Map.Enumerate())
            {
                if (!IsObjectTile(tileId))
                {
                    yield return (cell, tileId);
                }
            }
        }

        // --- Internals ---

        // Add or re-batch the single cell. A tile change to a different mesh moves it between
        // batches; same mesh is a no-op (position is fixed). Empty cell → remove instead.
        private void UpdateCell(Vector3I cell)
        {
            string tileId = Map.GetTileId(cell);
            if (tileId == null)
            {
                RemoveCell(cell);
                return;
            }

            if (IsObjectTile(tileId))
            {
                if (_objects.TryGetValue(cell, out var obj) && obj.tileId == tileId
                    && obj.turns == Map.GetRotation(cell) && IsInstanceValid(obj.node))
                {
                    return;  // same object, same facing, already standing here
                }
                RemoveCell(cell);
                SpawnObject(cell, tileId);
                return;
            }

            // Re-seat unconditionally: the rotation may have changed even if the mesh didn't, and a
            // swap-pop remove + append is O(1) anyway.
            RemoveCell(cell);
            AddToBatch(cell, tileId);
        }

        private void AddCell(Vector3I cell, string tileId)
        {
            if (IsObjectTile(tileId))
            {
                SpawnObject(cell, tileId);
            }
            else
            {
                AddToBatch(cell, tileId);
            }
        }

        private void SpawnObject(Vector3I cell, string tileId)
        {
            Node3D node = _tileById[tileId].Scene.Instantiate<Node3D>();
            // Transform BEFORE AddChild: a scene that reads its position in _Ready (grid registration)
            // must already see its cell. Same bottom-center pivot as tile meshes.
            int turns = Map.GetRotation(cell);
            node.Transform = CellTransform(cell, turns);
            node.Name = $"{tileId}_{cell.X}_{cell.Y}_{cell.Z}";  // stable, addressable by NodePath
            node.SetMeta(VisualMeta, true);  // Owner stays null → never serialized into the scene
            AddChild(node);
            _objects[cell] = (tileId, turns, node);
        }

        private void AddToBatch(Vector3I cell, string tileId)
        {
            string key = BatchKey(tileId);
            TileBatch batch = GetOrCreateBatch(key, ResolveMesh(tileId));
            int index = batch.Add(cell, CellTransform(cell, Map.GetRotation(cell)));
            _cellLoc[cell] = (key, index);
        }

        private void RemoveCell(Vector3I cell)
        {
            if (_objects.Remove(cell, out var obj))
            {
                if (IsInstanceValid(obj.node))
                {
                    obj.node.Free();  // immediate, so undo/redo in one frame can't double it
                }
                return;
            }

            if (!_cellLoc.TryGetValue(cell, out var loc))
            {
                return;
            }
            if (_batches.TryGetValue(loc.key, out var batch) && IsInstanceValid(batch))
            {
                // Swap-pop may relocate another cell into this slot — fix that cell's index.
                Vector3I? moved = batch.RemoveAt(loc.index);
                if (moved.HasValue)
                {
                    _cellLoc[moved.Value] = (loc.key, loc.index);
                }
            }
            _cellLoc.Remove(cell);
        }

        private TileBatch GetOrCreateBatch(string key, Mesh mesh)
        {
            if (_batches.TryGetValue(key, out var batch) && IsInstanceValid(batch))
            {
                return batch;
            }
            batch = new TileBatch();
            AddChild(batch);
            batch.SetMeta(VisualMeta, true);  // Owner stays null → never serialized into the scene
            batch.Init(mesh);
            _batches[key] = batch;
            return batch;
        }

        // Cells with an unresolved Id all share one magenta batch; everything else groups by Id
        // (one Id == one mesh), which is exactly one MultiMesh per distinct mesh.
        private string BatchKey(string tileId)
        {
            if (tileId != null && _tileById.TryGetValue(tileId, out var def) && def.Mesh != null)
            {
                return tileId;
            }
            return MissingKey;
        }

        // Tile index is built by Rebuild, but incremental edits can run before a rebuild
        // (e.g. straight after a hot-reload). Build on demand so ResolveMesh never sees null.
        private void EnsureTileIndex()
        {
            if (_tileById == null)
            {
                BuildTileIndex();
            }
        }

        private void Clear()
        {
            _cellLoc.Clear();
            _batches.Clear();
            _objects.Clear();  // their nodes carry VisualMeta → freed by the sweep below
            // Sweep the live tree, not an in-memory list — survives editor script reloads.
            var stale = new List<Node>();
            foreach (var child in GetChildren())
            {
                if (child.HasMeta(VisualMeta))
                {
                    stale.Add(child);
                }
            }
            foreach (var node in stale)
            {
                node.Free();  // immediate, so a same-frame rebuild can't double
            }
        }

        private void BuildTileIndex()
        {
            _tileById = new Dictionary<string, TileDefinition>();
            // Folder-scan discovery is the default source (drop a .tres in → it resolves).
            foreach (var def in TileLibrary.GetAll())
            {
                if (def != null && !string.IsNullOrEmpty(def.Id))
                {
                    _tileById[def.Id] = def;
                }
            }
            // Explicit Tiles array overrides discovery — handy for tests / one-off scenes.
            foreach (var def in Tiles)
            {
                if (def != null && !string.IsNullOrEmpty(def.Id))
                {
                    _tileById[def.Id] = def;
                }
            }
        }

        private Mesh ResolveMesh(string tileId)
        {
            if (tileId != null && _tileById.TryGetValue(tileId, out var def) && def.Mesh != null)
            {
                return def.Mesh;
            }
            return GetMissingMesh();
        }

        // Magenta placeholder for an unresolved Id (deleted .tres, typo). Visible, never a crash.
        private Mesh GetMissingMesh()
        {
            if (_missingMesh != null)
            {
                return _missingMesh;
            }

            var mat = new StandardMaterial3D
            {
                AlbedoColor = new Color("#FF00FF"),
                ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
            };
            _missingMesh = new BoxMesh { Size = Vector3.One * CellSize, Material = mat };
            return _missingMesh;
        }

        // Cell (0,0,0) fills the volume [0,1]³ → cube edges land on integer gridlines, and a
        // floor cell sits ON the y=0 plane (bottom at 0). Tile meshes use a bottom-center pivot
        // (centered on X/Z, origin on the bottom face — the natural Blockbench export), so we
        // shift half a cell on X/Z to center them but NOT on Y, where the mesh is already grounded.
        // Quarter turns spin the tile around its own bottom-center pivot, so it stays in its cell.
        public static Basis TurnBasis(int turns) => new(Vector3.Up, Mathf.PosMod(turns, 4) * Mathf.Pi * 0.5f);

        private Transform3D CellTransform(Vector3I cell, int turns) => new(TurnBasis(turns), CellToLocal(cell));

        private Vector3 CellToLocal(Vector3I cell)
        {
            return (new Vector3(cell.X, cell.Y, cell.Z) + new Vector3(0.5f, 0f, 0.5f)) * CellSize;
        }
    }
}
