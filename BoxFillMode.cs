using System.Collections.Generic;
using Godot;

namespace Moonbreak.Maptool
{
    // Click-drag to fill a 3-D bounding box of cells. Mouse-down anchors corner A; mouse-up
    // commits everything from A to B (the full cuboid, inclusive on both ends).
    // IsErase = true → erases the solid cells hit instead of placing. Used by the Erase and Place
    // tools so that single-click and drag both go through the same box-fill path.
    public class BoxFillMode : IEditMode
    {
        public string Name { get; set; } = "BoxFill";
        public bool IsDragMode => true;

        public string CurrentTileId;
        public bool IsErase;

        private Vector3I _anchor;
        private bool _bHasAnchor;
        private int _yOffset;
        private MapEdit _pending;

        public void OnPick(MapData map, PickResult pick)
        {
            _pending = null;
            if (!pick.Hit) return;
            if (IsErase && pick.FromPlane) return;
            if (!IsErase && string.IsNullOrEmpty(CurrentTileId)) return;
            _anchor = TargetCell(pick);
            _bHasAnchor = true;
            _yOffset = 0;
        }

        public void OnDragEnd(MapData map, PickResult pick)
        {
            _pending = null;
            if (!_bHasAnchor || !pick.Hit) return;
            if (IsErase && pick.FromPlane) return;
            if (!IsErase && string.IsNullOrEmpty(CurrentTileId)) return;

            var end = TargetCell(pick);
            end.Y += _yOffset;
            _pending = BuildBoxEdit(map, _anchor, end, IsErase ? null : CurrentTileId);
            _bHasAnchor = false;
            _yOffset = 0;
        }

        public MapEdit Commit() => _pending;

        public void Cancel()
        {
            _pending = null;
            _bHasAnchor = false;
            _yOffset = 0;
        }

        // Space = raise end corner, Ctrl = lower end corner.
        public bool OnKey(Key key)
        {
            if (!_bHasAnchor) return false;
            if (key == Key.Space) { _yOffset++; return true; }
            if (key == Key.Ctrl)  { _yOffset--; return true; }
            return false;
        }

        public IEnumerable<(Vector3I Cell, string TileId)> GetPreview(MapData map, PickResult pick)
        {
            if (!pick.Hit) yield break;
            if (IsErase && pick.FromPlane) yield break;
            if (!IsErase && string.IsNullOrEmpty(CurrentTileId)) yield break;

            string tileId = IsErase ? null : CurrentTileId;
            var anchor = _bHasAnchor ? _anchor : TargetCell(pick);
            var hover  = TargetCell(pick);
            hover.Y   += _yOffset;

            foreach (var cell in BoxCells(anchor, hover))
                yield return (cell, tileId);
        }

        private static MapEdit BuildBoxEdit(MapData map, Vector3I a, Vector3I b, string tileId)
        {
            var edit = new MapEdit();
            foreach (var cell in BoxCells(a, b))
            {
                string oldId = map.GetTileId(cell);
                if (oldId != tileId)
                    edit.Add(cell, oldId, tileId);
            }
            return edit.Count > 0 ? edit : null;
        }

        private static IEnumerable<Vector3I> BoxCells(Vector3I a, Vector3I b)
        {
            int x0 = Mathf.Min(a.X, b.X), x1 = Mathf.Max(a.X, b.X);
            int y0 = Mathf.Min(a.Y, b.Y), y1 = Mathf.Max(a.Y, b.Y);
            int z0 = Mathf.Min(a.Z, b.Z), z1 = Mathf.Max(a.Z, b.Z);
            for (int x = x0; x <= x1; x++)
            for (int y = y0; y <= y1; y++)
            for (int z = z0; z <= z1; z++)
                yield return new Vector3I(x, y, z);
        }

        private Vector3I TargetCell(PickResult pick)
        {
            if (IsErase) return pick.Cell;
            return pick.FromPlane ? pick.Cell : pick.Cell + pick.Normal;
        }
    }
}
