using System.Collections.Generic;
using Godot;

namespace Moonbreak.Maptool
{
    // Drag a rectangle on the XZ plane to select every cell in those columns, at any height: terrain,
    // objects, spawns and every overlay layer. The selection then takes operations (move, delete)
    // that the plugin drives from keys. Never edits on click, so Commit is always null.
    //
    // Columns, not a cuboid: the job is "shift this part of the map", and a part of the map is the
    // ground, what stands on it and what's painted on it — picking a height range would leave
    // pieces behind.
    public class SelectMode : IEditMode
    {
        public string Name => "Select";
        public bool IsDragMode => true;

        private Vector3I _anchor;
        private bool _bHasAnchor;

        // Selected rectangle, inclusive on both ends. Y is unused.
        public bool bHasSelection { get; private set; }
        public Vector2I Min { get; private set; }
        public Vector2I Max { get; private set; }

        public void OnPick(MapData map, PickResult pick)
        {
            if (!pick.Hit)
            {
                return;
            }
            _anchor = pick.Cell;
            _bHasAnchor = true;
        }

        public void OnDragEnd(MapData map, PickResult pick)
        {
            if (!_bHasAnchor || !pick.Hit)
            {
                return;
            }
            SetRect(_anchor, pick.Cell);
            _bHasAnchor = false;
        }

        public MapEdit Commit() => null;

        // Drops a half-made drag only — the selection survives mode switches and commits.
        public void Cancel() => _bHasAnchor = false;

        public void ClearSelection()
        {
            bHasSelection = false;
            _bHasAnchor = false;
        }

        public IEnumerable<(Vector3I Cell, string TileId)> GetPreview(MapData map, PickResult pick)
        {
            yield break;  // the plugin draws the rectangle itself
        }

        // Rectangle being dragged right now (anchor → hover), for the live outline.
        public bool TryGetDragRect(PickResult hover, out Vector2I min, out Vector2I max)
        {
            min = max = default;
            if (!_bHasAnchor || !hover.Hit)
            {
                return false;
            }
            min = new Vector2I(Mathf.Min(_anchor.X, hover.Cell.X), Mathf.Min(_anchor.Z, hover.Cell.Z));
            max = new Vector2I(Mathf.Max(_anchor.X, hover.Cell.X), Mathf.Max(_anchor.Z, hover.Cell.Z));
            return true;
        }

        public bool Contains(Vector3I cell)
            => bHasSelection && cell.X >= Min.X && cell.X <= Max.X && cell.Z >= Min.Y && cell.Z <= Max.Y;

        // --- Operations ---

        // Shift everything selected by offset. Whatever already sits at the destination outside the
        // selection is overwritten (undo brings it back). The selection follows the move.
        public MapEdit BuildMove(MapData map, Vector3I offset)
        {
            if (!bHasSelection || offset == Vector3I.Zero)
            {
                return null;
            }
            var edit = new MapEdit();

            // Terrain + objects: every source cell empties, then its tile + turn lands one offset over.
            // A destination that is itself a source gets the incoming tile, not the clear.
            var moved = new Dictionary<Vector3I, (string tileId, int turns)>();
            foreach (var (cell, tileId) in map.Enumerate())
            {
                if (Contains(cell))
                {
                    moved[cell + offset] = (tileId, map.GetRotation(cell));
                }
            }
            var touched = new HashSet<Vector3I>();
            foreach (var (to, _) in moved)
            {
                touched.Add(to);
                touched.Add(to - offset);
            }
            foreach (var cell in touched)
            {
                string oldId = map.GetTileId(cell);
                if (moved.TryGetValue(cell, out var incoming))
                {
                    edit.Add(cell, oldId, incoming.tileId, incoming.turns);
                }
                else if (oldId != null)
                {
                    edit.Add(cell, oldId, null);
                }
            }

            // Overlay layers: same shuffle, per layer. Layers carry no rotation.
            foreach (string layer in map.LayerNames)
            {
                var movedLayer = new Dictionary<Vector3I, string>();
                foreach (var (cell, tileId) in map.EnumerateLayer(layer))
                {
                    if (Contains(cell))
                    {
                        movedLayer[cell + offset] = tileId;
                    }
                }
                var touchedLayer = new HashSet<Vector3I>();
                foreach (var (to, _) in movedLayer)
                {
                    touchedLayer.Add(to);
                    touchedLayer.Add(to - offset);
                }
                foreach (var cell in touchedLayer)
                {
                    string oldId = map.GetLayerTile(layer, cell);
                    movedLayer.TryGetValue(cell, out string newId);
                    if (oldId != newId)
                    {
                        edit.AddLayer(layer, cell, oldId, newId);
                    }
                }
            }

            if (edit.Count == 0)
            {
                return null;
            }
            Min += new Vector2I(offset.X, offset.Z);
            Max += new Vector2I(offset.X, offset.Z);
            return edit;
        }

        // Erase everything selected, layers included.
        public MapEdit BuildDelete(MapData map)
        {
            if (!bHasSelection)
            {
                return null;
            }
            var edit = new MapEdit();
            foreach (var (cell, tileId) in map.Enumerate())
            {
                if (Contains(cell))
                {
                    edit.Add(cell, tileId, null);
                }
            }
            foreach (string layer in map.LayerNames)
            {
                foreach (var (cell, tileId) in map.EnumerateLayer(layer))
                {
                    if (Contains(cell))
                    {
                        edit.AddLayer(layer, cell, tileId, null);
                    }
                }
            }
            return edit.Count > 0 ? edit : null;
        }

        // Lowest and highest filled Y inside the selection, for drawing its box. False if it's empty.
        public bool TryGetHeightRange(MapData map, out int minY, out int maxY)
        {
            minY = int.MaxValue;
            maxY = int.MinValue;
            foreach (var (cell, _) in map.Enumerate())
            {
                if (Contains(cell))
                {
                    minY = Mathf.Min(minY, cell.Y);
                    maxY = Mathf.Max(maxY, cell.Y);
                }
            }
            return minY <= maxY;
        }

        private void SetRect(Vector3I a, Vector3I b)
        {
            Min = new Vector2I(Mathf.Min(a.X, b.X), Mathf.Min(a.Z, b.Z));
            Max = new Vector2I(Mathf.Max(a.X, b.X), Mathf.Max(a.Z, b.Z));
            bHasSelection = true;
        }
    }
}
