using Godot;

namespace Moonbreak.Maptool
{
    // Minecraft-style placement: drop a cell against the face you're aiming at, or onto the active
    // plane when building into the void. The current tile Id is pushed in by the plugin (from the
    // palette selection) before each pick.
    public class PlaceMode : IEditMode
    {
        public string Name => "Place";

        public string CurrentTileId;

        private MapEdit _pending;

        public void OnPick(MapData map, PickResult pick)
        {
            _pending = null;
            if (string.IsNullOrEmpty(CurrentTileId))
            {
                return;  // no tile selected → nothing to place
            }

            // On a solid hit, build against the hit face. On the plane, the picked cell IS the target.
            Vector3I target = pick.FromPlane ? pick.Cell : pick.Cell + pick.Normal;

            string oldId = map.GetTileId(target);
            if (oldId == CurrentTileId)
            {
                return;  // already that tile → no-op, don't pollute undo history
            }

            _pending = new MapEdit();
            _pending.Add(target, oldId, CurrentTileId);
        }

        public MapEdit Commit() => _pending;

        public void Cancel() => _pending = null;
    }
}
