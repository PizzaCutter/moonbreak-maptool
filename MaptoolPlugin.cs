#if TOOLS
using Godot;

namespace Moonbreak.Maptool
{
    [Tool]
    public partial class MaptoolPlugin : Node
    {
        private EditorPlugin _owner;
        private MaptoolDock _dock;
        private MapRenderer _renderer;

        private readonly BoxFillMode _placeMode = new() { Name = "Place" };
        private readonly BoxFillMode _eraseMode = new() { Name = "Erase", IsErase = true };
        private readonly LineMode _lineMode = new();
        private readonly RoomMode _roomMode = new();
        private readonly CircleMode _circleMode = new();
        private readonly FloodFillMode _floodFillMode = new();

        private enum EditModeId { Place, Erase, Line, Room, Circle, FloodFill }
        private static EditModeId ParseModeId(string name) => name switch
        {
            "Erase"     => EditModeId.Erase,
            "Line"      => EditModeId.Line,
            "Room"      => EditModeId.Room,
            "Circle"    => EditModeId.Circle,
            "FloodFill" => EditModeId.FloodFill,
            _           => EditModeId.Place,
        };
        private EditModeId _modeId = EditModeId.Place;
        private IEditMode ActiveMode => _modeId switch
        {
            EditModeId.Erase     => _eraseMode,
            EditModeId.Line      => _lineMode,
            EditModeId.Room      => _roomMode,
            EditModeId.Circle    => _circleMode,
            EditModeId.FloodFill => _floodFillMode,
            _                    => _placeMode,
        };

        private bool _bDragging;
        private int _activeLayer;
        private Camera3D _lastCamera;
        private Vector2 _lastMousePos;
        private MeshInstance3D _plane;

        private const string PlaneMeta = "_maptool_plane";
        private const string GhostMeta = "_maptool_ghost";

        // [Export] so these survive C# reload — Godot restores exported properties after reload.
        [Export] private string _savedTileId = "";
        [Export] private string _savedModeName = "Place";

        private StandardMaterial3D _ghostPlaceMat;
        private StandardMaterial3D _ghostEraseMat;
        private BoxMesh _ghostFallbackMesh;

        public override void _ExitTree()
        {
            HidePlane();
            _renderer = null;
            _dock = null;
        }

        public override void _Process(double delta)
        {
            // Retry renderer lookup each frame until found. SetupImpl may run before
            // GetEditedSceneRoot() is ready (scene briefly null during C# reload).
            if (_owner == null || _renderer != null) { return; }
            _renderer = FindInTree<MapRenderer>(EditorInterface.Singleton.GetEditedSceneRoot());
            if (_renderer != null)
            {
                TileLibrary.Refresh();
                UpdatePlane();
                _renderer.Rebuild();
            }
        }

        // GetParent() instead of a parameter — avoids cross-language cast ambiguity.
        public Node SetupImpl()
        {
            _owner = GetParent() as EditorPlugin;

            TileLibrary.Refresh();

            _dock = new MaptoolDock();
            _dock.TileSelected += id =>
            {
                _savedTileId = id;
                _placeMode.CurrentTileId   = id;
                _lineMode.CurrentTileId    = id;
                _roomMode.CurrentTileId    = id;
                _circleMode.CurrentTileId  = id;
                _floodFillMode.CurrentTileId = id;
                ClearGhosts();
            };
            _dock.ModeChanged += name =>
            {
                _savedModeName = name;
                _modeId = ParseModeId(name);
                ActiveMode.Cancel();
                _bDragging = false;
                ClearGhosts();
            };
            _dock.HollowChanged += bHollow => _circleMode.IsHollow = bHollow;
            _dock.LayerChanged += layer => { _activeLayer = layer; UpdatePlane(); };
            _dock.RefreshRequested += () => _renderer?.Rebuild();
            _owner.AddDock(_dock);  // triggers MaptoolDock._Ready() → tile list populated

            // Restore mode and tile selection from [Export] values that survived the reload.
            _modeId = ParseModeId(_savedModeName);
            if (!string.IsNullOrEmpty(_savedTileId))
            {
                _placeMode.CurrentTileId    = _savedTileId;
                _lineMode.CurrentTileId     = _savedTileId;
                _roomMode.CurrentTileId     = _savedTileId;
                _circleMode.CurrentTileId   = _savedTileId;
                _floodFillMode.CurrentTileId = _savedTileId;
            }
            _dock.RestoreState(_savedModeName, _savedTileId);

            foreach (var node in EditorInterface.Singleton.GetSelection().GetSelectedNodes())
            {
                if (node is MapRenderer mr) { _renderer = mr; break; }
            }
            _renderer ??= FindInTree<MapRenderer>(EditorInterface.Singleton.GetEditedSceneRoot());
            UpdatePlane();
            _renderer?.Rebuild();

            _mySessionId = _sessionId;
            return _dock;
        }

        // Unique value generated when the C# assembly loads. Always different across reloads.
        private static readonly int _sessionId = System.Environment.TickCount;
        // Reset to -1 by field initializer on every reload — never matches _sessionId until SetupImpl runs.
        private int _mySessionId = -1;

        // GDScript polls this. Returns false after any C# reload regardless of whether _dock was preserved.
        public bool IsInitialized() => _mySessionId == _sessionId;

        public bool HandlesImpl(GodotObject @object) => @object is MapRenderer;

        public void EditImpl(GodotObject @object)
        {
            _renderer = @object as MapRenderer;
            UpdatePlane();
        }

        public void MakeVisibleImpl(bool visible)
        {
            if (!visible)
            {
                ClearGhosts();
                _renderer = null;
                HidePlane();
            }
        }

        public int Forward3DGuiInputImpl(Camera3D viewportCamera, InputEvent @event)
        {
            if (_renderer == null || _renderer.Map == null)
                return (int)EditorPlugin.AfterGuiInput.Pass;

            if (@event is InputEventMouseMotion mm)
            {
                _lastCamera = viewportCamera;
                _lastMousePos = mm.Position;
                UpdateGhost(viewportCamera, mm.Position);
                return (int)EditorPlugin.AfterGuiInput.Pass;
            }

            if (@event is InputEventKey ik && ik.Pressed && _bDragging)
            {
                if (ActiveMode.OnKey(ik.Keycode))
                {
                    UpdateGhost(_lastCamera ?? viewportCamera, _lastMousePos);
                    return (int)EditorPlugin.AfterGuiInput.Stop;
                }
            }

            if (@event is InputEventMouseButton mb && mb.ButtonIndex == MouseButton.Left)
            {
                if (mb.Pressed)
                {
                    bool consumed = BeginInput(viewportCamera, mb.Position);
                    if (consumed) UpdateGhost(viewportCamera, mb.Position);
                    return consumed ? (int)EditorPlugin.AfterGuiInput.Stop : (int)EditorPlugin.AfterGuiInput.Pass;
                }
                if (!mb.Pressed && _bDragging)
                {
                    EndDrag(viewportCamera, mb.Position);
                    UpdateGhost(viewportCamera, mb.Position);
                    return (int)EditorPlugin.AfterGuiInput.Stop;
                }
            }
            return (int)EditorPlugin.AfterGuiInput.Pass;
        }

        private bool BeginInput(Camera3D camera, Vector2 mousePos)
        {
            PickResult pick = PickFromMouse(camera, mousePos);
            if (!pick.Hit) return false;

            IEditMode mode = ActiveMode;
            mode.OnPick(_renderer.Map, pick);

            if (mode.IsDragMode)
            {
                _bDragging = true;
                return true;
            }

            MapEdit edit = mode.Commit();
            mode.Cancel();
            if (edit == null || edit.Count == 0) return true;

            CommitEdit(mode.Name, edit);
            return true;
        }

        private void EndDrag(Camera3D camera, Vector2 mousePos)
        {
            _bDragging = false;
            IEditMode mode = ActiveMode;
            PickResult pick = PickFromMouse(camera, mousePos);

            mode.OnDragEnd(_renderer.Map, pick);
            MapEdit edit = mode.Commit();
            mode.Cancel();

            if (edit == null || edit.Count == 0) return;
            CommitEdit(mode.Name, edit);
        }

        private PickResult PickFromMouse(Camera3D camera, Vector2 mousePos)
        {
            Transform3D inv = _renderer.GlobalTransform.AffineInverse();
            Vector3 localOrigin = inv * camera.ProjectRayOrigin(mousePos);
            Vector3 localDir    = (inv.Basis * camera.ProjectRayNormal(mousePos)).Normalized();
            return CellPicker.Pick(_renderer.Map, localOrigin, localDir, _renderer.CellSize, _activeLayer);
        }

        private void CommitEdit(string modeName, MapEdit edit)
        {
            EditorUndoRedoManager undo = _owner.GetUndoRedo();
            undo.CreateAction(modeName + " tile");
            undo.AddDoMethod(_renderer, MapRenderer.MethodName.ApplyEdit, edit, true);
            undo.AddUndoMethod(_renderer, MapRenderer.MethodName.ApplyEdit, edit, false);
            undo.CommitAction();
        }

        private void UpdateGhost(Camera3D camera, Vector2 mousePos)
        {
            ClearGhosts();
            if (_renderer?.Map == null) return;

            PickResult pick = PickFromMouse(camera, mousePos);
            if (!pick.Hit) return;

            foreach (var (cell, tileId) in ActiveMode.GetPreview(_renderer.Map, pick))
                SpawnGhost(cell, tileId);
        }

        private void SpawnGhost(Vector3I cell, string tileId)
        {
            bool bErase = tileId == null;
            float cs = _renderer.CellSize;

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
                mesh = _ghostFallbackMesh ??= new BoxMesh { Size = Vector3.One * (cs + 0.04f) };
                bBottomPivot = false;
            }

            var ghost = new MeshInstance3D { Mesh = mesh };
            ghost.SetMeta(GhostMeta, true);
            ghost.MaterialOverride = bErase
                ? (_ghostEraseMat ??= MakeGhostMat(new Color("#FF443388")))
                : (_ghostPlaceMat ??= MakeGhostMat(new Color("#55DDBBAA")));

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

        private void UpdatePlane()
        {
            if (_renderer == null)
            {
                HidePlane();
                return;
            }

            if (_plane == null || !IsInstanceValid(_plane))
            {
                // Remove any orphaned plane left in the renderer from a previous C# reload
                // (after reload _plane is null here but the node may still exist in the scene).
                foreach (var child in _renderer.GetChildren())
                {
                    if (child.HasMeta(PlaneMeta)) { child.Free(); break; }
                }

                _plane = new MeshInstance3D
                {
                    Mesh = new PlaneMesh { Size = new Vector2(64, 64) },
                    MaterialOverride = new StandardMaterial3D
                    {
                        AlbedoColor = new Color("#33CCFF22"),
                        Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
                        ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
                        CullMode = BaseMaterial3D.CullModeEnum.Disabled,
                    },
                };
                _plane.SetMeta(PlaneMeta, true);
                _renderer.AddChild(_plane);
                _plane.Owner = null;
            }

            _plane.Position = new Vector3(0, _activeLayer * _renderer.CellSize, 0);
        }

        private void HidePlane()
        {
            if (_plane != null && IsInstanceValid(_plane))
                _plane.Free();
            _plane = null;
        }

        private static T FindInTree<T>(Node root) where T : Node
        {
            if (root == null) return null;
            if (root is T match) return match;
            foreach (var child in root.GetChildren())
            {
                var found = FindInTree<T>(child);
                if (found != null) return found;
            }
            return null;
        }
    }
}
#endif
