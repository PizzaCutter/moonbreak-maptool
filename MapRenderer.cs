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
                AddToBatch(cell, tileId);
            }

            GD.Print($"MapRenderer: rebuilt {Map.CellCount} cells");
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

            string key = BatchKey(tileId);
            if (_cellLoc.TryGetValue(cell, out var loc))
            {
                if (loc.key == key)
                {
                    return;  // same mesh, same position → nothing to upload
                }
                RemoveCell(cell);  // moved to a different mesh → pull from the old batch first
            }

            AddToBatch(cell, tileId);
        }

        private void AddToBatch(Vector3I cell, string tileId)
        {
            string key = BatchKey(tileId);
            TileBatch batch = GetOrCreateBatch(key, ResolveMesh(tileId));
            int index = batch.Add(cell, new Transform3D(Basis.Identity, CellToLocal(cell)));
            _cellLoc[cell] = (key, index);
        }

        private void RemoveCell(Vector3I cell)
        {
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
        private Vector3 CellToLocal(Vector3I cell)
        {
            return (new Vector3(cell.X, cell.Y, cell.Z) + new Vector3(0.5f, 0f, 0.5f)) * CellSize;
        }
    }
}
