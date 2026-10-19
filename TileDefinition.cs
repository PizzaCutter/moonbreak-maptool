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
        // Minecraft grass/stone variants: each cell adds a quarter-turn yaw hashed from its position,
        // so a field of one tile stops visibly repeating. Never stored — recomputed on every build,
        // so the R-key turn still adds on top (tap R to reroll an unlucky cell). Terrain only; for
        // tiles whose top reads fine in all four orientations.
        [Export] public bool bRandomYaw = false;
        // Minecraft logs/pillars: the tile's local Y axis follows the face you click. Top or bottom →
        // upright (R still spins it), a side face → lying along that face's axis. Lets a trunk
        // branch sideways by clicking its side.
        [Export] public bool bPillar = false;
        // Set → overlay tile painted onto a named MapData layer instead of terrain (e.g. "surface").
        // It sits in the standing cell above a floor. LayerValue is what the game reads it as
        // (e.g. a SurfaceState name); PreviewColor tints its editor plate and ghost.
        [Export] public string Layer = "";
        [Export] public string LayerValue = "";
        [Export] public Color PreviewColor = new("#FFFFFF");
    }
}
