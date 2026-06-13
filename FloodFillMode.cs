using System.Collections.Generic;
using Godot;

namespace Moonbreak.Maptool
{
    // Click to flood-fill all connected cells of the same tile type on the same Y layer.
    // 4-directional BFS (N/S/E/W), bounded to MaxCells to prevent runaway on open maps.
    public class FloodFillMode : IEditMode
    {
        public string Name => "FloodFill";

        public string CurrentTileId;

        private const int MaxCells = 1000;

        private MapEdit _pending;

        public void OnPick(MapData map, PickResult pick)
        {
            _pending = null;
            if (!pick.Hit || string.IsNullOrEmpty(CurrentTileId)) return;

            var start = TargetCell(pick);
            string startTile = map.GetTileId(start);

            var edit = new MapEdit();
            foreach (var cell in BfsFlood(map, start, startTile))
            {
                if (map.GetTileId(cell) != CurrentTileId)
                    edit.Add(cell, map.GetTileId(cell), CurrentTileId);
            }
            if (edit.Count > 0)
                _pending = edit;
        }

        public MapEdit Commit() => _pending;

        public void Cancel() => _pending = null;

        public IEnumerable<(Vector3I Cell, string TileId)> GetPreview(MapData map, PickResult pick)
        {
            if (string.IsNullOrEmpty(CurrentTileId) || !pick.Hit) yield break;

            var start = TargetCell(pick);
            string startTile = map.GetTileId(start);

            foreach (var cell in BfsFlood(map, start, startTile))
                yield return (cell, CurrentTileId);
        }

        private static IEnumerable<Vector3I> BfsFlood(MapData map, Vector3I start, string matchTile)
        {
            var visited = new HashSet<Vector3I> { start };
            var queue = new Queue<Vector3I>();
            queue.Enqueue(start);
            int count = 0;

            while (queue.Count > 0 && count < MaxCells)
            {
                var cell = queue.Dequeue();
                count++;
                yield return cell;

                foreach (var neighbor in XZNeighbors(cell))
                {
                    if (visited.Contains(neighbor)) continue;
                    if (map.GetTileId(neighbor) != matchTile) continue;
                    visited.Add(neighbor);
                    queue.Enqueue(neighbor);
                }
            }
        }

        private static IEnumerable<Vector3I> XZNeighbors(Vector3I cell)
        {
            yield return cell + new Vector3I( 1, 0,  0);
            yield return cell + new Vector3I(-1, 0,  0);
            yield return cell + new Vector3I( 0, 0,  1);
            yield return cell + new Vector3I( 0, 0, -1);
        }

        private static Vector3I TargetCell(PickResult pick)
            => pick.FromPlane ? pick.Cell : pick.Cell + pick.Normal;
    }
}
