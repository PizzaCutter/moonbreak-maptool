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
        // Minecraft-style right-click break while in Place mode. Single-cell, not a drag.
        private readonly EraseMode _quickEraseMode = new();

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
        // R-key yaw in quarter turns, applied to every placed tile without auto-facing.
        private int _rotation;
        // Facing locked in at mouse-down, so a drag commits the turn its preview showed.
        private int _dragTurns;
        private int _activeLayer;
        private Camera3D _lastCamera;
        private Vector2 _lastMousePos;
        private MeshInstance3D _plane;

        // Right mouse is also Godot's freelook. Track the hold so only a still click breaks a
        // cell — any real mouse travel means the user was flying the camera.
        private const float RightClickSlop = 4f;
        private bool _bRightHeld;
        private Vector2 _rightPressPos;
        private float _rightTravel;

        private const string PlaneMeta = "_maptool_plane";
        private const string GhostMeta = "_maptool_ghost";

        // [Export] so these survive C# reload — Godot restores exported properties after reload.
        [Export] private string _savedTileId = "";
        [Export] private string _savedModeName = "Place";

        private StandardMaterial3D _ghostPlaceMat;
        private StandardMaterial3D _ghostEraseMat;
        private BoxMesh _ghostFallbackMesh;
        private BoxMesh _arrowShaftMesh;
        private PrismMesh _arrowHeadMesh;
        private StandardMaterial3D _arrowMat;

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
                if (_bRightHeld)
                {
                    // Relative, not Position — freelook captures the cursor, so Position stalls.
                    _rightTravel += mm.Relative.Length();
                }
                UpdateGhost(viewportCamera, mm.Position);
                return (int)EditorPlugin.AfterGuiInput.Pass;
            }

            if (@event is InputEventMouseButton rb && rb.ButtonIndex == MouseButton.Right)
            {
                return HandleRightClick(viewportCamera, rb);
            }

            // R / Shift+R: turn the tile a quarter clockwise / counter-clockwise (seen from above).
            // Consumed so it doesn't reach the editor's scale tool.
            if (@event is InputEventKey rk && rk.Pressed && !rk.Echo && rk.Keycode == Key.R
                && !rk.CtrlPressed && !rk.AltPressed && !rk.MetaPressed && _modeId != EditModeId.Erase)
            {
                _rotation = Mathf.PosMod(_rotation + (rk.ShiftPressed ? 1 : -1), 4);
                if (_bDragging)
                {
                    _dragTurns = _rotation;
                }
                UpdateGhost(_lastCamera ?? viewportCamera, _lastMousePos);
                return (int)EditorPlugin.AfterGuiInput.Stop;
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

        // Place mode only: a still right-click erases the hovered cell. Press is always passed on
        // so freelook still starts; the erase fires on release if the mouse barely moved.
        private int HandleRightClick(Camera3D camera, InputEventMouseButton mb)
        {
            if (_modeId != EditModeId.Place || _bDragging)
            {
                _bRightHeld = false;
                return (int)EditorPlugin.AfterGuiInput.Pass;
            }

            if (mb.Pressed)
            {
                _bRightHeld = true;
                _rightPressPos = mb.Position;
                _rightTravel = 0f;
                return (int)EditorPlugin.AfterGuiInput.Pass;
            }

            if (!_bRightHeld)
            {
                return (int)EditorPlugin.AfterGuiInput.Pass;
            }
            _bRightHeld = false;
            if (_rightTravel > RightClickSlop)
            {
                return (int)EditorPlugin.AfterGuiInput.Pass;
            }

            PickResult pick = PickFromMouse(camera, _rightPressPos);
            MapEdit edit;
            TileDefinition def = CurrentTileDef();
            if (IsLayerTile(def))
            {
                // A surface is selected: right-click lifts that layer off the floor you point at
                // and never touches the ground block itself.
                edit = BuildLayerClear(def.Layer, pick);
            }
            else
            {
                _quickEraseMode.OnPick(_renderer.Map, pick);
                edit = _quickEraseMode.Commit();
                _quickEraseMode.Cancel();
            }
            if (edit == null || edit.Count == 0)
            {
                return (int)EditorPlugin.AfterGuiInput.Pass;
            }

            CommitEdit(_quickEraseMode.Name, edit);
            UpdateGhost(camera, _rightPressPos);
            // Never Stop the release: freelook already saw the press, swallowing this leaves it stuck.
            return (int)EditorPlugin.AfterGuiInput.Pass;
        }

        private bool BeginInput(Camera3D camera, Vector2 mousePos)
        {
            PickResult pick = PickFromMouse(camera, mousePos);
            if (!pick.Hit) return false;

            IEditMode mode = ActiveMode;
            mode.OnPick(_renderer.Map, pick);
            _dragTurns = TurnsFor(pick);

            if (mode.IsDragMode)
            {
                _bDragging = true;
                return true;
            }

            MapEdit edit = mode.Commit();
            mode.Cancel();
            if (edit == null || edit.Count == 0) return true;

            edit = ToLayerEditIfNeeded(edit);
            if (edit == null) { return true; }
            edit.NewRotation = _dragTurns;
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
            edit = ToLayerEditIfNeeded(edit);
            if (edit == null) { return; }
            edit.NewRotation = _dragTurns;
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

            TileDefinition def = CurrentTileDef();
            if (_modeId != EditModeId.Erase && IsLayerTile(def))
            {
                foreach (var (cell, tileId) in ActiveMode.GetPreview(_renderer.Map, pick))
                {
                    if (tileId != null && CanHoldLayer(cell))
                    {
                        SpawnLayerGhost(cell, def);
                    }
                }
                return;
            }

            int turns = _bDragging ? _dragTurns : TurnsFor(pick);
            foreach (var (cell, tileId) in ActiveMode.GetPreview(_renderer.Map, pick))
                SpawnGhost(cell, tileId, turns);
        }

        // --- Overlay layers (surfaces) ---

        private static bool IsLayerTile(TileDefinition def) => def != null && !string.IsNullOrEmpty(def.Layer);

        // A layer cell is the open standing cell right above a terrain floor.
        private bool CanHoldLayer(Vector3I cell)
            => !_renderer.IsTerrainCell(cell) && _renderer.IsTerrainCell(cell + Vector3I.Down);

        // Modes speak terrain: they emit "put tile X in standing cell C". With a layer tile selected
        // that becomes "paint layer X at C" — so every placing mode paints surfaces unchanged.
        private MapEdit ToLayerEditIfNeeded(MapEdit edit)
        {
            TileDefinition def = CurrentTileDef();
            if (_modeId == EditModeId.Erase || !IsLayerTile(def))
            {
                return edit;
            }
            var layerEdit = new MapEdit();
            foreach (var (cell, _, newId) in edit.Entries)
            {
                if (newId == null || !CanHoldLayer(cell))
                {
                    continue;
                }
                string oldId = _renderer.Map.GetLayerTile(def.Layer, cell);
                if (oldId != def.Id)
                {
                    layerEdit.AddLayer(def.Layer, cell, oldId, def.Id);
                }
            }
            return layerEdit.Count > 0 ? layerEdit : null;
        }

        private MapEdit BuildLayerClear(string layer, PickResult pick)
        {
            if (!pick.Hit || pick.FromPlane)
            {
                return null;
            }
            Vector3I cell = pick.Cell + Vector3I.Up;
            string oldId = _renderer.Map.GetLayerTile(layer, cell);
            if (oldId == null)
            {
                return null;
            }
            var edit = new MapEdit();
            edit.AddLayer(layer, cell, oldId, null);
            return edit;
        }

        private void SpawnLayerGhost(Vector3I cell, TileDefinition def)
        {
            float cs = _renderer.CellSize;
            var ghost = new MeshInstance3D { Mesh = _renderer.GetPlateMesh(def.Id) };
            ghost.SetMeta(GhostMeta, true);
            ghost.Position = new Vector3((cell.X + 0.5f) * cs, cell.Y * cs + 0.01f, (cell.Z + 0.5f) * cs);
            _renderer.AddChild(ghost);
            ghost.Owner = null;
        }

        // Yaw for a placement made from this pick. A tile with an AttachDirection turns to meet the
        // wall face that was clicked; everything else (and floor/void clicks) uses the R-key turn.
        private int TurnsFor(PickResult pick)
        {
            TileDefinition def = CurrentTileDef();
            if (def == null || def.AttachDirection == Vector3I.Zero || !pick.Hit || pick.FromPlane
                || pick.Normal.Y != 0)
            {
                return _rotation;
            }
            Vector3 intoWall = -(Vector3)pick.Normal;
            for (int turns = 0; turns < 4; turns++)
            {
                Vector3 facing = MapRenderer.TurnBasis(turns) * (Vector3)def.AttachDirection;
                if (facing.DistanceTo(intoWall) < 0.01f)
                {
                    return turns;
                }
            }
            return _rotation;  // AttachDirection isn't horizontal — no quarter turn can match
        }

        private TileDefinition CurrentTileDef()
        {
            if (string.IsNullOrEmpty(_savedTileId))
            {
                return null;
            }
            foreach (var def in TileLibrary.GetAll())
            {
                if (def.Id == _savedTileId)
                {
                    return def;
                }
            }
            return null;
        }

        private void SpawnGhost(Vector3I cell, string tileId, int turns)
        {
            bool bErase = tileId == null;
            float cs = _renderer.CellSize;

            Mesh mesh;
            bool bBottomPivot;
            TileDefinition tileDef = null;
            if (!bErase)
            {
                Mesh tileMesh = null;
                foreach (var def in TileLibrary.GetAll())
                {
                    if (def.Id == tileId) { tileDef = def; tileMesh = def.Mesh; break; }
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
            ghost.Basis = MapRenderer.TurnBasis(turns);

            _renderer.AddChild(ghost);
            ghost.Owner = null;

            // Objects and wall-attached tiles often preview as a plain box, which hides the turn.
            // Point an arrow along what matters: into the wall for AttachDirection tiles, else the
            // tile's front (-Z). Child of the ghost, so the ghost's yaw turns it.
            if (tileDef != null && (tileDef.Scene != null || tileDef.AttachDirection != Vector3I.Zero))
            {
                Vector3 dir = tileDef.AttachDirection != Vector3I.Zero
                    ? ((Vector3)tileDef.AttachDirection).Normalized()
                    : Vector3.Forward;
                float midY = bBottomPivot ? 0.5f * cs : 0f;
                AddArrow(ghost, dir, new Vector3(0f, midY, 0f), cs);
            }
        }

        // Flat arrow from the cell centre to its edge along localDir, drawn on top of the ghost.
        private void AddArrow(Node3D parent, Vector3 localDir, Vector3 localCenter, float cs)
        {
            _arrowMat ??= new StandardMaterial3D
            {
                AlbedoColor   = new Color("#FFCC44"),
                ShadingMode   = BaseMaterial3D.ShadingModeEnum.Unshaded,
                NoDepthTest   = true,  // visible through the translucent ghost and the wall
                RenderPriority = 1,
            };
            _arrowShaftMesh ??= new BoxMesh { Size = new Vector3(0.08f, 0.04f, 0.3f), Material = _arrowMat };
            _arrowHeadMesh ??= new PrismMesh { Size = new Vector3(0.3f, 0.2f, 0.04f), Material = _arrowMat };

            // Root's -Z looks along localDir; shaft and head are built pointing -Z.
            var root = new Node3D
            {
                Position = localCenter,
                Basis = Basis.LookingAt(localDir, Mathf.Abs(localDir.Y) > 0.99f ? Vector3.Forward : Vector3.Up),
                Scale = Vector3.One * cs,
            };
            parent.AddChild(root);
            root.Owner = null;

            var shaft = new MeshInstance3D { Mesh = _arrowShaftMesh, Position = new Vector3(0f, 0f, -0.1f) };
            root.AddChild(shaft);
            shaft.Owner = null;

            // PrismMesh apex is +Y; tip it onto -Z and lay it flat.
            var head = new MeshInstance3D
            {
                Mesh = _arrowHeadMesh,
                Position = new Vector3(0f, 0f, -0.35f),
                Basis = new Basis(Vector3.Right, -Mathf.Pi * 0.5f),
            };
            root.AddChild(head);
            head.Owner = null;
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
