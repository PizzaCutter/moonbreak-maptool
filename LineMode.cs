using System.Collections.Generic;
using Godot;

namespace Moonbreak.Maptool
{
    // Click-drag to place tiles in a straight line from anchor to endpoint.
    // Steps are computed by lerping across the longest axis, so diagonals stay one cell thick.
    // Space / Ctrl raise or lower the endpoint Y while dragging.
    public class LineMode : IEditMode
    {
        public string Name => "Line";
        public bool IsDragMode => true;

        public string CurrentTileId;

        private Vector3I _anchor;
        private bool _bHasAnchor;
        private int _yOffset;
        private MapEdit _pending;

        public void OnPick(MapData map, PickResult pick)
        {
            _pending = null;
            if (!pick.Hit || string.IsNullOrEmpty(CurrentTileId)) return;
            _anchor = TargetCell(pick);
            _bHasAnchor = true;
            _yOffset = 0;
        }

        public void OnDragEnd(MapData map, PickResult pick)
        {
            _pending = null;
            if (!_bHasAnchor || !pick.Hit || string.IsNullOrEmpty(CurrentTileId)) return;

            var end = TargetCell(pick);
            end.Y += _yOffset;
            _pending = BuildLineEdit(map, _anchor, end, CurrentTileId);
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

        // Space = raise endpoint, Ctrl = lower endpoint.
        public bool OnKey(Key key)
        {
            if (!_bHasAnchor) return false;
            if (key == Key.Space) { _yOffset++; return true; }
            if (key == Key.Ctrl)  { _yOffset--; return true; }
            return false;
        }

        public IEnumerable<(Vector3I Cell, string TileId)> GetPreview(MapData map, PickResult pick)
        {
            if (string.IsNullOrEmpty(CurrentTileId) || !pick.Hit) yield break;

            var anchor = _bHasAnchor ? _anchor : TargetCell(pick);
            var hover  = TargetCell(pick);
            hover.Y   += _yOffset;

            foreach (var cell in LineCells(anchor, hover))
                yield return (cell, CurrentTileId);
        }

        private static MapEdit BuildLineEdit(MapData map, Vector3I a, Vector3I b, string tileId)
        {
            var edit = new MapEdit();
            foreach (var cell in LineCells(a, b))
            {
                string oldId = map.GetTileId(cell);
                if (oldId != tileId)
                    edit.Add(cell, oldId, tileId);
            }
            return edit.Count > 0 ? edit : null;
        }

        private static IEnumerable<Vector3I> LineCells(Vector3I a, Vector3I b)
        {
            int steps = Mathf.Max(Mathf.Max(Mathf.Abs(b.X - a.X), Mathf.Abs(b.Y - a.Y)), Mathf.Abs(b.Z - a.Z));
            var seen = new HashSet<Vector3I>();
            var prev = a;

            for (int i = 0; i <= steps; i++)
            {
                float t = steps == 0 ? 0f : (float)i / steps;
                var cell = new Vector3I(
                    Mathf.RoundToInt(Mathf.Lerp(a.X, b.X, t)),
                    Mathf.RoundToInt(Mathf.Lerp(a.Y, b.Y, t)),
                    Mathf.RoundToInt(Mathf.Lerp(a.Z, b.Z, t))
                );

                // Diagonal steps leave face-gaps. Insert corner cells so every adjacent
                // pair in the line shares a face (edge-connected). Order: X → Y → Z.
                if (i > 0)
                {
                    var d = cell - prev;
                    if (d.X != 0 && (d.Y != 0 || d.Z != 0))
                    {
                        var c1 = new Vector3I(cell.X, prev.Y, prev.Z);
                        if (seen.Add(c1)) yield return c1;
                        if (d.Y != 0 && d.Z != 0)
                        {
                            var c2 = new Vector3I(cell.X, cell.Y, prev.Z);
                            if (seen.Add(c2)) yield return c2;
                        }
                    }
                    else if (d.Y != 0 && d.Z != 0)
                    {
                        var c1 = new Vector3I(prev.X, cell.Y, prev.Z);
                        if (seen.Add(c1)) yield return c1;
                    }
                }

                if (seen.Add(cell)) yield return cell;
                prev = cell;
            }
        }

        private static Vector3I TargetCell(PickResult pick)
            => pick.FromPlane ? pick.Cell : pick.Cell + pick.Normal;
    }
}
