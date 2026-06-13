using System.Collections.Generic;
using Godot;

namespace Moonbreak.Maptool
{
    // Click-drag to fill a 3-D bounding box of cells. Mouse-down anchors corner A; mouse-up
    // commits everything from A to B (the full cuboid, inclusive on both ends).
    public class BoxFillMode : IEditMode
    {
        public string Name => "BoxFill";
        public bool IsDragMode => true;

        public string CurrentTileId;

        private Vector3I _anchor;
        private bool _bHasAnchor;
        private MapEdit _pending;

        public void OnPick(MapData map, PickResult pick)
        {
            _pending = null;
            if (!pick.Hit || string.IsNullOrEmpty(CurrentTileId)) return;
            _anchor = TargetCell(pick);
            _bHasAnchor = true;
        }

        public void OnDragEnd(MapData map, PickResult pick)
        {
            _pending = null;
            if (!_bHasAnchor || !pick.Hit || string.IsNullOrEmpty(CurrentTileId)) return;

            var end = TargetCell(pick);
            _pending = BuildBoxEdit(map, _anchor, end, CurrentTileId);
            _bHasAnchor = false;
        }

        public MapEdit Commit() => _pending;

        public void Cancel()
        {
            _pending = null;
            _bHasAnchor = false;
        }

        public IEnumerable<(Vector3I Cell, string TileId)> GetPreview(MapData map, PickResult pick)
        {
            if (string.IsNullOrEmpty(CurrentTileId) || !pick.Hit) yield break;

            var anchor = _bHasAnchor ? _anchor : TargetCell(pick);
            var hover  = TargetCell(pick);

            foreach (var cell in BoxCells(anchor, hover))
                yield return (cell, CurrentTileId);
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

        private static Vector3I TargetCell(PickResult pick)
            => pick.FromPlane ? pick.Cell : pick.Cell + pick.Normal;
    }
}
