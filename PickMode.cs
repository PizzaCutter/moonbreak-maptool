using System.Collections.Generic;
using Godot;

namespace Moonbreak.Maptool
{
    // Eyedropper: click a cell to make its tile the current tile. Never edits the map, so Commit is
    // always null — the plugin reads the hovered cell itself and pushes the Id through the dock.
    public class PickMode : IEditMode
    {
        public string Name => "Pick";

        public void OnPick(MapData map, PickResult pick) { }

        public MapEdit Commit() => null;

        public void Cancel() { }

        // Ghost the hovered cell as itself, so you see what a click would pick up.
        public IEnumerable<(Vector3I Cell, string TileId)> GetPreview(MapData map, PickResult pick)
        {
            if (!pick.Hit || pick.FromPlane)
            {
                yield break;
            }
            string tileId = map.GetTileId(pick.Cell);
            if (tileId != null)
            {
                yield return (pick.Cell, tileId);
            }
        }
    }
}
