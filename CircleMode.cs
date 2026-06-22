using System.Collections.Generic;
using Godot;

namespace Moonbreak.Maptool
{
    // Drag to define a bounding box. The circle/ellipse is inscribed within that box:
    // center = midpoint, radii = half the XZ extents. Non-square boxes produce ellipses.
    // Space / Ctrl extrude vertically into a cylinder / elliptic prism.
    // IsHollow = false → solid fill. IsHollow = true → ring wall only, no caps.
    public class CircleMode : IEditMode
    {
        public string Name => "Circle";
        public bool IsDragMode => true;

        public string CurrentTileId;
        public bool IsHollow;

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
            _pending = BuildEllipseEdit(map, _anchor, end, CurrentTileId, IsHollow);
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

        // Space = raise top of extrusion, Ctrl = lower it.
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

            foreach (var cell in EllipseCells(anchor, hover, IsHollow))
                yield return (cell, CurrentTileId);
        }

        private static MapEdit BuildEllipseEdit(MapData map, Vector3I a, Vector3I b, string tileId, bool isHollow)
        {
            var edit = new MapEdit();
            foreach (var cell in EllipseCells(a, b, isHollow))
            {
                string oldId = map.GetTileId(cell);
                if (oldId != tileId)
                    edit.Add(cell, oldId, tileId);
            }
            return edit.Count > 0 ? edit : null;
        }

        private static IEnumerable<Vector3I> EllipseCells(Vector3I a, Vector3I b, bool isHollow)
        {
            int y0 = Mathf.Min(a.Y, b.Y), y1 = Mathf.Max(a.Y, b.Y);

            float cx = (a.X + b.X) / 2f;
            float cz = (a.Z + b.Z) / 2f;
            // Radius = longest half-extent, so an axis-aligned drag spans the full distance.
            float r  = Mathf.Max(Mathf.Abs(b.X - a.X), Mathf.Abs(b.Z - a.Z)) / 2f;
            int   ri = Mathf.CeilToInt(r);

            for (int x = Mathf.FloorToInt(cx) - ri; x <= Mathf.CeilToInt(cx) + ri; x++)
            for (int z = Mathf.FloorToInt(cz) - ri; z <= Mathf.CeilToInt(cz) + ri; z++)
            {
                if (!InCircle(x, z, cx, cz, r)) continue;

                if (isHollow)
                {
                    bool onRing = !InCircle(x + 1, z, cx, cz, r) ||
                                  !InCircle(x - 1, z, cx, cz, r) ||
                                  !InCircle(x, z + 1, cx, cz, r) ||
                                  !InCircle(x, z - 1, cx, cz, r);
                    if (!onRing) continue;
                }

                for (int y = y0; y <= y1; y++)
                    yield return new Vector3I(x, y, z);
            }
        }

        private static bool InCircle(int x, int z, float cx, float cz, float r)
        {
            if (r <= 0f) return Mathf.Abs(x - cx) < 0.5f && Mathf.Abs(z - cz) < 0.5f;
            float dx = x - cx, dz = z - cz;
            return dx * dx + dz * dz <= r * r;
        }

        private static Vector3I TargetCell(PickResult pick)
            => pick.FromPlane ? pick.Cell : pick.Cell + pick.Normal;
    }
}
