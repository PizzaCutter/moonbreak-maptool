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
        private IEditMode _mode;

        private int _activeLayer;
        private MeshInstance3D _plane;

        private const string PlaneMeta = "_maptool_plane";

        public override void _EnterTree()
        {
            _mode = _placeMode;

            _dock = new MaptoolDock();
            _dock.TileSelected += id => _placeMode.CurrentTileId = id;
            _dock.EraseModeChanged += erase => _mode = erase ? _eraseMode : _placeMode;
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

            if (@event is InputEventMouseButton mb && mb.Pressed && mb.ButtonIndex == MouseButton.Left)
            {
                if (Paint(viewportCamera, mb.Position))
                {
                    return (int)AfterGuiInput.Stop;  // consumed → don't let the click also move the gizmo
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
            
            if (_mode == null)
            {
                GD.Print("_mode is invalid");
                return false;
            }

            _mode.OnPick(map, pick);
            MapEdit edit = _mode.Commit();
            _mode.Cancel();
            if (edit == null || edit.Count == 0)
            {
                return true;  // a valid click that changed nothing (e.g. erasing empty) — still consume
            }

            EditorUndoRedoManager undo = GetUndoRedo();
            undo.CreateAction(_mode.Name + " tile");
            undo.AddDoMethod(_renderer, MapRenderer.MethodName.ApplyEdit, edit, true);
            undo.AddUndoMethod(_renderer, MapRenderer.MethodName.ApplyEdit, edit, false);
            undo.CommitAction();  // executes the do → applies diff + rebuilds, marks scene dirty
            return true;
        }

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
