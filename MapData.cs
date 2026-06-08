using Godot;
using System.Collections.Generic;

namespace Moonbreak.Maptool
{
    // Source of truth for terrain. Minecraft-style: a small palette of tile Ids this map uses,
    // and per-cell storage as an index into that palette. Keeps the .tres tiny and rename-safe
    // (the palette holds immutable Ids, not file paths).
    //
    // Serialized form is two flat arrays so it stores compactly in the .tres:
    //   _palette   = ["ground_grass", "ground_stone", ...]
    //   _packedCells = [x, y, z, paletteIndex,  x, y, z, paletteIndex, ...]
    // A runtime Dictionary is built lazily for fast lookup and is written back on every mutation.
    [Tool]
    [GlobalClass]
    public partial class MapData : Resource
    {
        [Export] public Godot.Collections.Array<string> Palette { get; set; } = new();
        [Export] public int[] PackedCells { get; set; } = System.Array.Empty<int>();

        // cell -> palette index. Runtime cache, rebuilt lazily from PackedCells.
        private Dictionary<Vector3I, int> _lookup;

        private void EnsureLoaded()
        {
            if (_lookup != null)
            {
                return;
            }

            _lookup = new Dictionary<Vector3I, int>();
            for (int i = 0; i + 3 < PackedCells.Length; i += 4)
            {
                var cell = new Vector3I(PackedCells[i], PackedCells[i + 1], PackedCells[i + 2]);
                _lookup[cell] = PackedCells[i + 3];
            }
        }

        // Flatten the runtime dictionary back into PackedCells for serialization.
        private void Flush()
        {
            var packed = new int[_lookup.Count * 4];
            int w = 0;
            foreach (var (cell, index) in _lookup)
            {
                packed[w++] = cell.X;
                packed[w++] = cell.Y;
                packed[w++] = cell.Z;
                packed[w++] = index;
            }
            PackedCells = packed;
        }

        // Returns the palette index for tileId, appending it if new.
        private int PaletteIndexOf(string tileId)
        {
            int existing = Palette.IndexOf(tileId);
            if (existing >= 0)
            {
                return existing;
            }
            Palette.Add(tileId);
            return Palette.Count - 1;
        }

        private void SetCellInternal(Vector3I cell, string tileId)
        {
            _lookup[cell] = PaletteIndexOf(tileId);
        }

        // --- Public API ---

        public void SetCell(Vector3I cell, string tileId)
        {
            EnsureLoaded();
            SetCellInternal(cell, tileId);
            Flush();
        }

        // Batch write — one Flush for many cells (box fill, flood fill).
        public void SetCells(IEnumerable<(Vector3I cell, string tileId)> cells)
        {
            EnsureLoaded();
            foreach (var (cell, tileId) in cells)
            {
                SetCellInternal(cell, tileId);
            }
            Flush();
        }

        public void ClearCell(Vector3I cell)
        {
            EnsureLoaded();
            if (_lookup.Remove(cell))
            {
                Flush();
            }
        }

        public bool HasCell(Vector3I cell)
        {
            EnsureLoaded();
            return _lookup.ContainsKey(cell);
        }

        // Tile Id at cell, or null if empty.
        public string GetTileId(Vector3I cell)
        {
            EnsureLoaded();
            if (_lookup.TryGetValue(cell, out int index) && index >= 0 && index < Palette.Count)
            {
                return Palette[index];
            }
            return null;
        }

        public IEnumerable<(Vector3I cell, string tileId)> Enumerate()
        {
            EnsureLoaded();
            foreach (var (cell, index) in _lookup)
            {
                string tileId = (index >= 0 && index < Palette.Count) ? Palette[index] : null;
                yield return (cell, tileId);
            }
        }

        public int CellCount
        {
            get
            {
                EnsureLoaded();
                return _lookup.Count;
            }
        }
    }
}
