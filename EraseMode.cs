namespace Moonbreak.Maptool
{
    // Removes the solid cell the ray actually hit (not hit+face). Plane picks erase nothing —
    // there's no cell in the void to remove.
    public class EraseMode : IEditMode
    {
        public string Name => "Erase";

        private MapEdit _pending;

        public void OnPick(MapData map, PickResult pick)
        {
            _pending = null;
            if (pick.FromPlane || !map.HasCell(pick.Cell))
            {
                return;
            }

            _pending = new MapEdit();
            _pending.Add(pick.Cell, map.GetTileId(pick.Cell), null);
        }

        public MapEdit Commit() => _pending;

        public void Cancel() => _pending = null;
    }
}
