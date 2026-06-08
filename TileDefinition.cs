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
        [Export] public Mesh Mesh;                // carries its own material
        [Export] public string[] Tags = System.Array.Empty<string>();  // fuzzy-search corpus
        [Export] public bool bWalkable = true;    // core to tactics
    }
}
