using Godot;

namespace Moonbreak.Maptool
{
    // One .tres per tile. Authored by the tool (never by hand).
    // Id is the immutable contract MapData's palette references — DisplayName is the mutable label.
    [Tool]
    [GlobalClass]
    public partial class TileDefinition : Resource
    {
        [Export] public string Id = "";          // immutable after creation, NEVER renamed
        [Export] public string DisplayName = "";  // freely editable label
        [Export] public Mesh Mesh;                // carries its own material; on an object tile, ghost preview only
        // Set → object tile (Minecraft block entity): each cell instantiates this scene instead of
        // joining a mesh batch. Not terrain — the game decides what the spawned node does.
        [Export] public PackedScene Scene;
        [Export] public string[] Tags = System.Array.Empty<string>();  // fuzzy-search corpus
        [Export] public bool bWalkable = true;    // core to tactics
        // Minecraft ladder/torch placement: the local direction that should point INTO the surface
        // you click. Placing against a wall face auto-turns the tile so this axis meets the wall.
        // Zero = no auto-facing; the R-key rotation applies instead.
        [Export] public Vector3I AttachDirection = Vector3I.Zero;
    }
}
