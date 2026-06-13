using System.Collections.Generic;
using Godot;

namespace Moonbreak.Maptool
{
    // One class per edit mode (mirrors the game's IAction discovery pattern). A mode interprets a
    // pick and produces a MapEdit diff — it knows nothing about undo, input, or rendering.
    //
    // PlaceMode / EraseMode are single-pick: OnPick builds the diff, Commit returns it immediately.
    // Multi-pick modes (BoxFill, FloodFill) stash point A on first OnPick, emit diff on second.
    public interface IEditMode
    {
        string Name { get; }

        // A click landed. map is read-only context for computing old tiles; do NOT mutate it here.
        void OnPick(MapData map, PickResult pick);

        // The diff to apply, or null if this pick changes nothing (e.g. erasing empty space).
        MapEdit Commit();

        // Reset pending state (e.g. clear a stored point A). Safe to call anytime.
        void Cancel();

        // Cells that WOULD be affected if the user clicked now. Called on MouseMotion, not on click.
        // Yields (cell, tileId) pairs; tileId == null means "would erase". PlaceMode/EraseMode
        // yield 0–1 entries; BoxFill/FloodFill yield many.
        IEnumerable<(Vector3I Cell, string TileId)> GetPreview(MapData map, PickResult pick);
    }
}
