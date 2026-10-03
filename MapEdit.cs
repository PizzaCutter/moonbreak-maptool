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

        // Rotation each cell had before the edit, captured on the first forward apply (the map is
        // exactly the pre-edit state then) so undo puts turned tiles back the way they were.
        private Dictionary<Vector3I, int> _oldRotations;

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

        public void AddLayer(string layer, Vector3I cell, string oldId, string newId)
        {
            _layerEntries.Add((layer, cell, oldId, newId));
        }

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
            foreach (var (cell, _, newId) in _entries)
            {
                Write(map, cell, newId, NewRotation);
            }
            foreach (var (layer, cell, _, newId) in _layerEntries)
            {
                map.SetLayerTile(layer, cell, newId);
            }
        }

        public void ApplyReverse(MapData map)
        {
            foreach (var (cell, oldId, _) in _entries)
            {
                int turns = 0;
                _oldRotations?.TryGetValue(cell, out turns);
                Write(map, cell, oldId, turns);
            }
            foreach (var (layer, cell, oldId, _) in _layerEntries)
            {
                map.SetLayerTile(layer, cell, oldId);
            }
        }

        private static void Write(MapData map, Vector3I cell, string tileId, int turns)
        {
            if (tileId == null)
            {
                map.ClearCell(cell);
            }
            else
            {
                map.SetCell(cell, tileId, turns);
            }
        }
    }
}
