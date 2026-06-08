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
                var mi = new MeshInstance3D { Mesh = ResolveMesh(tileId) };
                AddChild(mi);
                // Deliberately leave Owner null → never serialized into the scene.
                mi.SetMeta(VisualMeta, true);
                mi.Position = CellToLocal(cell);
            }

            GD.Print($"MapRenderer: rebuilt {Map.CellCount} cells");
        }

        // --- Single-cell mutation hooks (used by edit modes later) ---

        public void SetCell(Vector3I cell, string tileId)
        {
            Map?.SetCell(cell, tileId);
            Rebuild();  // naive for now; per-cell incremental update is a later optimization
        }

        public void ClearCell(Vector3I cell)
        {
            Map?.ClearCell(cell);
            Rebuild();
        }

        // --- Internals ---

        private void Clear()
        {
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
        // floor cell sits ON the y=0 plane (bottom at 0). Centered meshes need this half-cell
        // shift; the renderer owns alignment so tile meshes don't need corner pivots.
        private Vector3 CellToLocal(Vector3I cell)
        {
            return (new Vector3(cell.X, cell.Y, cell.Z) + new Vector3(0.5f, 0.5f, 0.5f)) * CellSize;
        }
    }
}
