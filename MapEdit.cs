using Godot;
using System.Collections.Generic;

namespace Moonbreak.Maptool
{
    // One reversible terrain diff: the single funnel every edit mode produces and the unit of undo.
    // Holds old + new tile Id per touched cell so it can be applied either direction. A null tileId
    // means "empty" (forward-null = erase; reverse-null = the cell was empty before).
    //
    // RefCounted so it marshals through Variant into EditorUndoRedoManager's do/undo method args.
    // The diff knows NOTHING about undo — the editor-plugin layer wraps it. (MAPTOOL_DESIGN.md)
    [Tool]
    public partial class MapEdit : RefCounted
    {
        private readonly List<(Vector3I cell, string oldId, string newId)> _entries = new();
        // Overlay-layer diffs (surfaces). Same old/new shape, keyed by layer too.
        private readonly List<(string layer, Vector3I cell, string oldId, string newId)> _layerEntries = new();

        // Quarter turns every placed cell gets. One per edit: a mode places one tile, one way.
        // Set by the editor layer (R key / clicked face) — modes never see rotation.
        public int NewRotation { get; set; }
        // Paint: retile in place — each cell keeps the turn it already had and NewRotation is ignored.
        public bool bKeepRotations { get; set; }

        // Rotation each cell had before the edit, captured on the first forward apply (the map is
        // exactly the pre-edit state then) so undo puts turned tiles back the way they were.
        private Dictionary<Vector3I, int> _oldRotations;
        // Per-cell turn for entries that carry their own (a moved selection keeps each tile's turn).
        // Wins over NewRotation and bKeepRotations.
        private Dictionary<Vector3I, int> _newRotations;

        public int Count => _entries.Count + _layerEntries.Count;

        public IEnumerable<(Vector3I cell, string oldId, string newId)> Entries => _entries;

        public IEnumerable<(string layer, Vector3I cell)> TouchedLayerCells
        {
            get
            {
                foreach (var (layer, cell, _, _) in _layerEntries)
                {
                    yield return (layer, cell);
                }
            }
        }

        // Cells this diff touches — lets the renderer update only those, not the whole map.
        public IEnumerable<Vector3I> TouchedCells
        {
            get
            {
                foreach (var (cell, _, _) in _entries)
                {
                    yield return cell;
                }
            }
        }

        public void Add(Vector3I cell, string oldId, string newId)
        {
            _entries.Add((cell, oldId, newId));
        }

        public void Add(Vector3I cell, string oldId, string newId, int newTurns)
        {
            _entries.Add((cell, oldId, newId));
            (_newRotations ??= new Dictionary<Vector3I, int>())[cell] = newTurns;
        }

        public void AddLayer(string layer, Vector3I cell, string oldId, string newId)
        {
            _layerEntries.Add((layer, cell, oldId, newId));
        }

        // One batched write per direction: MapData re-flushes its packed arrays once, not per cell,
        // so a big selection move stays instant.
        public void ApplyForward(MapData map)
        {
            if (_oldRotations == null)
            {
                _oldRotations = new Dictionary<Vector3I, int>();
                foreach (var (cell, _, _) in _entries)
                {
                    _oldRotations[cell] = map.GetRotation(cell);
                }
            }
            var writes = new List<(Vector3I, string, int)>(_entries.Count);
            foreach (var (cell, _, newId) in _entries)
            {
                int turns = NewRotation;
                if (bKeepRotations)
                {
                    _oldRotations.TryGetValue(cell, out turns);
                }
                if (_newRotations != null && _newRotations.TryGetValue(cell, out int ownTurns))
                {
                    turns = ownTurns;
                }
                writes.Add((cell, newId, turns));
            }
            map.WriteCells(writes);

            var layerWrites = new List<(string, Vector3I, string)>(_layerEntries.Count);
            foreach (var (layer, cell, _, newId) in _layerEntries)
            {
                layerWrites.Add((layer, cell, newId));
            }
            map.WriteLayerTiles(layerWrites);
        }

        public void ApplyReverse(MapData map)
        {
            // Reverse order, so a cell touched twice in one diff ends on its first old value.
            var writes = new List<(Vector3I, string, int)>(_entries.Count);
            for (int i = _entries.Count - 1; i >= 0; i--)
            {
                var (cell, oldId, _) = _entries[i];
                int turns = 0;
                _oldRotations?.TryGetValue(cell, out turns);
                writes.Add((cell, oldId, turns));
            }
            map.WriteCells(writes);

            var layerWrites = new List<(string, Vector3I, string)>(_layerEntries.Count);
            for (int i = _layerEntries.Count - 1; i >= 0; i--)
            {
                var (layer, cell, oldId, _) = _layerEntries[i];
                layerWrites.Add((layer, cell, oldId));
            }
            map.WriteLayerTiles(layerWrites);
        }
    }
}
