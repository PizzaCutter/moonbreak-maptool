#if TOOLS
using Godot;

namespace Moonbreak.Maptool
{
    // Editor entry point. THE ONLY place allowed to touch editor-only APIs (viewport input,
    // EditorUndoRedoManager, docks) — the portable core (MapData, MapRenderer, TileDefinition,
    // CellPicker, IEditMode) stays editor-independent so a future in-game editor reuses it and
    // swaps only this driver. See MAPTOOL_DESIGN.md "Undo/redo" + "Edit modes".
    [Tool]
    public partial class MaptoolPlugin : EditorPlugin
    {
        private MaptoolDock _dock;
        private MapRenderer _renderer;   // the MapRenderer currently being edited, or null

        private readonly PlaceMode _placeMode = new();
        private readonly EraseMode _eraseMode = new();

        // Derived, not cached: a C# hot-reload reconstructs the plugin WITHOUT re-running _EnterTree,
        // so any field only assigned there comes back null. _placeMode/_eraseMode have field
        // initializers (survive reload) and _bErase defaults to false → ActiveMode is never null.
        private bool _bErase;
        private IEditMode ActiveMode => _bErase ? _eraseMode : _placeMode;

        private int _activeLayer;
        private MeshInstance3D _plane;

        private const string PlaneMeta  = "_maptool_plane";
        private const string GhostMeta  = "_maptool_ghost";

        private StandardMaterial3D _ghostPlaceMat;
        private StandardMaterial3D _ghostEraseMat;
        private BoxMesh _ghostFallbackMesh;

        public override void _EnterTree()
        {
            _dock = new MaptoolDock();
            _dock.TileSelected += id => { _placeMode.CurrentTileId = id; ClearGhosts(); };
            _dock.EraseModeChanged += erase => { _bErase = erase; ClearGhosts(); };
            _dock.LayerChanged += layer => { _activeLayer = layer; UpdatePlane(); };
            _dock.RefreshRequested += () => _renderer?.Rebuild();
            AddDock(_dock);  // dock carries its own Title + DefaultSlot (set in MaptoolDock ctor)
        }

        public override void _ExitTree()
        {
            HidePlane();
            if (_dock != null)
            {
                RemoveDock(_dock);
                _dock.QueueFree();  // AddDock docs: caller must free the dock on cleanup
                _dock = null;
            }
        }

        // Take over editing when a MapRenderer is selected.
        public override bool _Handles(GodotObject @object) => @object is MapRenderer;

        public override void _Edit(GodotObject @object)
        {
            _renderer = @object as MapRenderer;
            UpdatePlane();
        }

        public override void _MakeVisible(bool visible)
        {
            if (!visible)
            {
                ClearGhosts();
                _renderer = null;
                HidePlane();
            }
        }

        public override int _Forward3DGuiInput(Camera3D viewportCamera, InputEvent @event)
        {
            if (_renderer == null || _renderer.Map == null)
            {
                return (int)AfterGuiInput.Pass;
            }

            if (@event is InputEventMouseMotion mm)
            {
                UpdateGhost(viewportCamera, mm.Position);
                return (int)AfterGuiInput.Pass;  // never consume motion — camera still needs it
            }

            if (@event is InputEventMouseButton mb && mb.Pressed && mb.ButtonIndex == MouseButton.Left)
            {
                if (Paint(viewportCamera, mb.Position))
                {
                    UpdateGhost(viewportCamera, mb.Position);  // refresh so ghost tracks the new map state
                    return (int)AfterGuiInput.Stop;
                }
            }
            return (int)AfterGuiInput.Pass;
        }

        // Resolve the ray → run the active mode → wrap its diff in one undoable action.
        private bool Paint(Camera3D camera, Vector2 mousePos)
        {
            MapData map = _renderer.Map;

            // Ray into the renderer's local space (renderer may be translated/rotated in the scene).
            Transform3D inv = _renderer.GlobalTransform.AffineInverse();
            Vector3 localOrigin = inv * camera.ProjectRayOrigin(mousePos);
            Vector3 localDir = (inv.Basis * camera.ProjectRayNormal(mousePos)).Normalized();

            PickResult pick = CellPicker.Pick(map, localOrigin, localDir, _renderer.CellSize, _activeLayer);
            if (!pick.Hit)
            {
                return false;
            }
            
            IEditMode mode = ActiveMode;
            mode.OnPick(map, pick);
            MapEdit edit = mode.Commit();
            mode.Cancel();
            if (edit == null || edit.Count == 0)
            {
                return true;  // a valid click that changed nothing (e.g. erasing empty) — still consume
            }

            EditorUndoRedoManager undo = GetUndoRedo();
            undo.CreateAction(mode.Name + " tile");
            undo.AddDoMethod(_renderer, MapRenderer.MethodName.ApplyEdit, edit, true);
            undo.AddUndoMethod(_renderer, MapRenderer.MethodName.ApplyEdit, edit, false);
            undo.CommitAction();  // executes the do → applies diff + rebuilds, marks scene dirty
            return true;
        }

        // --- Ghost preview: translucent cells showing what the active mode would paint ---

        private void UpdateGhost(Camera3D camera, Vector2 mousePos)
        {
            ClearGhosts();
            if (_renderer?.Map == null) return;

            Transform3D inv = _renderer.GlobalTransform.AffineInverse();
            Vector3 localOrigin = inv * camera.ProjectRayOrigin(mousePos);
            Vector3 localDir    = (inv.Basis * camera.ProjectRayNormal(mousePos)).Normalized();

            PickResult pick = CellPicker.Pick(_renderer.Map, localOrigin, localDir,
                                              _renderer.CellSize, _activeLayer);
            if (!pick.Hit) return;

            foreach (var (cell, tileId) in ActiveMode.GetPreview(_renderer.Map, pick))
            {
                SpawnGhost(cell, tileId);
            }
        }

        private void SpawnGhost(Vector3I cell, string tileId)
        {
            bool bErase = tileId == null;
            float cs = _renderer.CellSize;

            // Resolve mesh and whether it uses a bottom-centre pivot (tile mesh) or centre pivot (BoxMesh).
            Mesh mesh;
            bool bBottomPivot;
            if (!bErase)
            {
                Mesh tileMesh = null;
                foreach (var def in TileLibrary.GetAll())
                {
                    if (def.Id == tileId) { tileMesh = def.Mesh; break; }
                }
                if (tileMesh != null)
                {
                    mesh = tileMesh;
                    bBottomPivot = true;
                }
                else
                {
                    mesh = _ghostFallbackMesh ??= new BoxMesh { Size = Vector3.One * (cs + 0.04f) };
                    bBottomPivot = false;
                }
            }
            else
            {
                // Slightly enlarged so the ghost outline sits above the existing tile without z-fighting.
                mesh = _ghostFallbackMesh ??= new BoxMesh { Size = Vector3.One * (cs + 0.04f) };
                bBottomPivot = false;
            }

            var ghost = new MeshInstance3D { Mesh = mesh };
            ghost.SetMeta(GhostMeta, true);
            ghost.MaterialOverride = bErase
                ? (_ghostEraseMat ??= MakeGhostMat(new Color("#FF443388")))
                : (_ghostPlaceMat ??= MakeGhostMat(new Color("#55DDBBAA")));

            // Bottom-pivot meshes (tiles): Y = cell.Y (bottom flush with cell floor).
            // Centre-pivot meshes (BoxMesh): Y = cell.Y + 0.5 (centred in cell volume).
            float yLocal = bBottomPivot ? cell.Y * cs : (cell.Y + 0.5f) * cs;
            ghost.Position = new Vector3((cell.X + 0.5f) * cs, yLocal, (cell.Z + 0.5f) * cs);

            _renderer.AddChild(ghost);
            ghost.Owner = null;
        }

        private void ClearGhosts()
        {
            if (_renderer == null || !IsInstanceValid(_renderer)) return;
            foreach (var child in _renderer.GetChildren())
            {
                if (child.HasMeta(GhostMeta))
                    child.Free();
            }
        }

        private static StandardMaterial3D MakeGhostMat(Color color) => new()
        {
            AlbedoColor  = color,
            Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
            ShadingMode  = BaseMaterial3D.ShadingModeEnum.Unshaded,
            CullMode     = BaseMaterial3D.CullModeEnum.Disabled,
        };

        // --- Active-layer build plane: translucent visual anchor for void placement ---

        private void UpdatePlane()
        {
            if (_renderer == null)
            {
                HidePlane();
                return;
            }

            if (_plane == null || !IsInstanceValid(_plane))
            {
                _plane = new MeshInstance3D
                {
                    Mesh = new PlaneMesh { Size = new Vector2(64, 64) },
                    MaterialOverride = new StandardMaterial3D
                    {
                        AlbedoColor = new Color("#33CCFF22"),  // faint cyan, low alpha
                        Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
                        ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
                        CullMode = BaseMaterial3D.CullModeEnum.Disabled,
                    },
                };
                _plane.SetMeta(PlaneMeta, true);
                _renderer.AddChild(_plane);
                _plane.Owner = null;  // never serialized into the .tscn
            }

            // PlaneMesh lies in XZ (normal +Y). Sit it at the bottom of the active layer.
            _plane.Position = new Vector3(0, _activeLayer * _renderer.CellSize, 0);
        }

        private void HidePlane()
        {
            if (_plane != null && IsInstanceValid(_plane))
            {
                _plane.Free();
            }
            _plane = null;
        }
    }
}
#endif
