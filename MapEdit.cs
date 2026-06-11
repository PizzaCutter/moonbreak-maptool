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

        public int Count => _entries.Count;

        public void Add(Vector3I cell, string oldId, string newId)
        {
            _entries.Add((cell, oldId, newId));
        }

        public void ApplyForward(MapData map)
        {
            foreach (var (cell, _, newId) in _entries)
            {
                Write(map, cell, newId);
            }
        }

        public void ApplyReverse(MapData map)
        {
            foreach (var (cell, oldId, _) in _entries)
            {
                Write(map, cell, oldId);
            }
        }

        private static void Write(MapData map, Vector3I cell, string tileId)
        {
            if (tileId == null)
            {
                map.ClearCell(cell);
            }
            else
            {
                map.SetCell(cell, tileId);
            }
        }
    }
}
