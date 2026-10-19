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
        private readonly BoxFillMode _paintMode = new() { Name = "Paint", IsPaint = true };
        private readonly PickMode _pickMode = new();
        private readonly LineMode _lineMode = new();
        private readonly RoomMode _roomMode = new();
        private readonly CircleMode _circleMode = new();
        private readonly FloodFillMode _floodFillMode = new();
        private readonly SelectMode _selectMode = new();
        // Minecraft-style right-click break while in Place mode. Single-cell, not a drag.
        private readonly EraseMode _quickEraseMode = new();

        private enum EditModeId { Place, Erase, Paint, Pick, Line, Room, Circle, FloodFill, Select }
        private static EditModeId ParseModeId(string name) => name switch
        {
            "Erase"     => EditModeId.Erase,
            "Paint"     => EditModeId.Paint,
            "Pick"      => EditModeId.Pick,
            "Line"      => EditModeId.Line,
            "Room"      => EditModeId.Room,
            "Circle"    => EditModeId.Circle,
            "FloodFill" => EditModeId.FloodFill,
            "Select"    => EditModeId.Select,
            _           => EditModeId.Place,
        };
        private EditModeId _modeId = EditModeId.Place;
        // Pick is a detour: after one successful pick the tool drops back to the mode you came from.
        private string _modeBeforePick = "Place";

        // Modes that put the current tile down (as terrain or as a layer). Erase removes, Pick reads,
        // Select only marks cells.
        private bool PlacesCurrentTile
            => _modeId != EditModeId.Erase && _modeId != EditModeId.Pick && _modeId != EditModeId.Select;
        private IEditMode ActiveMode => _modeId switch
        {
            EditModeId.Erase     => _eraseMode,
            EditModeId.Paint     => _paintMode,
            EditModeId.Pick      => _pickMode,
            EditModeId.Line      => _lineMode,
            EditModeId.Room      => _roomMode,
            EditModeId.Circle    => _circleMode,
            EditModeId.FloodFill => _floodFillMode,
            EditModeId.Select    => _selectMode,
            _                    => _placeMode,
        };

        private bool _bDragging;
        // R-key yaw in quarter turns, applied to every placed tile without auto-facing.
        private int _rotation;
        // T-key axis lock. Auto = the tile decides (pillars follow the clicked face, everything else
        // stands upright); any other value forces that axis on every placed tile.
        private enum AxisLock { Auto, Upright, AlongX, AlongZ }
        private AxisLock _axisLock = AxisLock.Auto;
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
        [Export] private Godot.Collections.Array<string> _savedRecentTiles = new();
        [Export] private string _savedMaskTileId = "";

        private StandardMaterial3D _ghostPlaceMat;
        private StandardMaterial3D _ghostTintMat;
        private StandardMaterial3D _ghostEraseMat;
        private StandardMaterial3D _ghostSelectMat;
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
                ApplyTileId(id);
                _savedRecentTiles = new Godot.Collections.Array<string>(_dock.Recent);
                ClearGhosts();
            };
            _dock.MaskChanged += id =>
            {
                _savedMaskTileId = id ?? "";
                ClearGhosts();
            };
            _dock.ModeChanged += SetMode;
            _dock.HollowChanged += bHollow => _circleMode.IsHollow = bHollow;
            _dock.LayerChanged += layer => { _activeLayer = layer; UpdatePlane(); };
            _dock.RefreshRequested += () => _renderer?.Rebuild();
            _dock.MoveSelectionRequested += offset => RunSelectionOp(() => MoveSelection(offset));
            _dock.DeleteSelectionRequested += () => RunSelectionOp(
                () => CommitIfAny("Delete selection", _selectMode.BuildDelete(_renderer.Map)));
            _dock.ClearSelectionRequested += () => RunSelectionOp(_selectMode.ClearSelection);
            _owner.AddDock(_dock);  // triggers MaptoolDock._Ready() → tile list populated

            // Restore mode and tile selection from [Export] values that survived the reload.
            _modeId = ParseModeId(_savedModeName);
            if (!string.IsNullOrEmpty(_savedTileId))
            {
                ApplyTileId(_savedTileId);
            }
            _dock.RestoreState(_savedModeName, _savedTileId, _savedRecentTiles, _savedMaskTileId);

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

        // Every mode that places the current tile gets the same Id.
        private void ApplyTileId(string id)
        {
            _savedTileId = id;
            _placeMode.CurrentTileId     = id;
            _paintMode.CurrentTileId     = id;
            _lineMode.CurrentTileId      = id;
            _roomMode.CurrentTileId      = id;
            _circleMode.CurrentTileId    = id;
            _floodFillMode.CurrentTileId = id;
        }

        // Single mode-switch funnel for dock buttons, number keys and Pick's auto-return. The dock
        // fires this on click; key and auto-return paths sync the dock's button without a signal.
        private void SetMode(string name)
        {
            if (name == "Pick" && _modeId != EditModeId.Pick)
            {
                _modeBeforePick = _savedModeName;
            }
            ActiveMode.Cancel();
            _savedModeName = name;
            _modeId = ParseModeId(name);
            _bDragging = false;
            ClearGhosts();
            SyncSelectionToDock();
        }

        private void SetModeFromTool(string name)
        {
            SetMode(name);
            _dock?.SetModeNoSignal(name);
        }

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

            // Shift + number row switches mode (table in MaptoolDock.Modes). Bare digits belong to the
            // editor. Matched by physical key so the top row works on any layout (AZERTY included),
            // and nothing fires while the right mouse is flying the camera.
            if (@event is InputEventKey mk && mk.Pressed && !mk.Echo && !_bRightHeld && mk.ShiftPressed
                && !mk.CtrlPressed && !mk.AltPressed && !mk.MetaPressed)
            {
                // Shift+X: swap to the previously used tile (Photoshop's X).
                if (mk.PhysicalKeycode == Key.X)
                {
                    _dock?.SwapToPreviousTile();
                    UpdateGhost(_lastCamera ?? viewportCamera, _lastMousePos);
                    return (int)EditorPlugin.AfterGuiInput.Stop;
                }
                foreach (var (name, _, key) in MaptoolDock.Modes)
                {
                    if (mk.PhysicalKeycode != key)
                    {
                        continue;
                    }
                    SetModeFromTool(name);
                    UpdateGhost(_lastCamera ?? viewportCamera, _lastMousePos);
                    return (int)EditorPlugin.AfterGuiInput.Stop;
                }
            }

            // Select mode: arrows / PageUp / PageDown / Delete / Escape act on the selection. Echo is
            // allowed so holding an arrow keeps nudging.
            if (@event is InputEventKey sk && sk.Pressed && _modeId == EditModeId.Select && !_bRightHeld
                && !sk.CtrlPressed && !sk.AltPressed && !sk.MetaPressed && _selectMode.bHasSelection)
            {
                if (HandleSelectionKey(sk.Keycode, _lastCamera ?? viewportCamera))
                {
                    UpdateGhost(_lastCamera ?? viewportCamera, _lastMousePos);
                    return (int)EditorPlugin.AfterGuiInput.Stop;
                }
            }

            // R / Shift+R: turn the tile a quarter clockwise / counter-clockwise (seen from above).
            // Consumed so it doesn't reach the editor's scale tool.
            if (@event is InputEventKey rk && rk.Pressed && !rk.Echo && rk.Keycode == Key.R
                && !rk.CtrlPressed && !rk.AltPressed && !rk.MetaPressed && _modeId != EditModeId.Erase
                && _modeId != EditModeId.Select)
            {
                _rotation = Mathf.PosMod(_rotation + (rk.ShiftPressed ? 1 : -1), 4);
                if (_bDragging)
                {
                    _dragTurns = ManualTurns();
                }
                UpdateGhost(_lastCamera ?? viewportCamera, _lastMousePos);
                return (int)EditorPlugin.AfterGuiInput.Stop;
            }

            // T / Shift+T: cycle the axis lock forward / back (Auto → Upright → X → Z).
            if (@event is InputEventKey tk && tk.Pressed && !tk.Echo && tk.Keycode == Key.T
                && !tk.CtrlPressed && !tk.AltPressed && !tk.MetaPressed && PlacesCurrentTile)
            {
                _axisLock = (AxisLock)Mathf.PosMod((int)_axisLock + (tk.ShiftPressed ? -1 : 1), 4);
                _dock?.SetAxisLabel(_axisLock.ToString());
                if (_bDragging)
                {
                    _dragTurns = ManualTurns();
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
                    bool consumed = BeginInput(viewportCamera, mb.Position, mb.ShiftPressed);
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

            CommitEdit(_quickEraseMode.Name + " tile", edit);
            UpdateGhost(camera, _rightPressPos);
            // Never Stop the release: freelook already saw the press, swallowing this leaves it stuck.
            return (int)EditorPlugin.AfterGuiInput.Pass;
        }

        private bool BeginInput(Camera3D camera, Vector2 mousePos, bool bShift)
        {
            PickResult pick = PickFromMouse(camera, mousePos);
            if (!pick.Hit) return false;

            if (_modeId == EditModeId.Pick)
            {
                PickTile(pick, bShift);
                return true;
            }

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

            edit = ToLayerEditIfNeeded(ApplyMask(edit));
            if (edit == null) { return true; }
            edit.NewRotation = _dragTurns;
            CommitEdit(mode.Name + " tile", edit);
            return true;
        }

        // Eyedropper: the clicked cell's tile becomes the current tile (Shift: the mask), then back
        // to the last mode. A top-face click takes the surface/vegetation lying there, a side click
        // the block. The mask compares blocks, so Shift always takes the block.
        private void PickTile(PickResult pick, bool bAsMask)
        {
            if (pick.FromPlane || _dock == null)
            {
                return;
            }
            string tileId = bAsMask ? null : LayerTileOnTop(pick, out _);
            tileId ??= _renderer.Map.GetTileId(pick.Cell);
            if (tileId == null)
            {
                return;
            }
            if (bAsMask)
            {
                _dock.SetMask(tileId);
            }
            else if (!_dock.SelectTile(tileId))
            {
                return;  // a tile Id with no loaded definition — keep the current tile
            }
            SetModeFromTool(_modeBeforePick == "Pick" ? "Place" : _modeBeforePick);
        }

        // The overlay tile in the standing cell above a top-face hit, or null. With several layers on
        // one floor the first in LayerNames wins (the order they were first painted on this map).
        private string LayerTileOnTop(PickResult pick, out Vector3I standing)
        {
            standing = pick.Cell + Vector3I.Up;
            if (!pick.Hit || pick.FromPlane || pick.Normal != Vector3I.Up)
            {
                return null;
            }
            foreach (string layer in _renderer.Map.LayerNames)
            {
                string tileId = _renderer.Map.GetLayerTile(layer, standing);
                if (tileId != null)
                {
                    return tileId;
                }
            }
            return null;
        }

        private void EndDrag(Camera3D camera, Vector2 mousePos)
        {
            _bDragging = false;
            IEditMode mode = ActiveMode;
            PickResult pick = PickFromMouse(camera, mousePos);

            mode.OnDragEnd(_renderer.Map, pick);
            MapEdit edit = mode.Commit();
            mode.Cancel();
            if (_modeId == EditModeId.Select)
            {
                SyncSelectionToDock();
            }

            if (edit == null || edit.Count == 0) return;
            edit = ToLayerEditIfNeeded(ApplyMask(edit));
            if (edit == null) { return; }
            edit.NewRotation = _dragTurns;
            CommitEdit(mode.Name + " tile", edit);
        }

        // --- Selection ---

        // Arrows move one cell along whichever map axis is closest to the camera's forward / right,
        // so "up" pushes the selection away from you from any angle. PageUp / PageDown move a level.
        private bool HandleSelectionKey(Key key, Camera3D camera)
        {
            switch (key)
            {
                case Key.Escape:
                    _selectMode.ClearSelection();
                    SyncSelectionToDock();
                    return true;
                case Key.Delete:
                    CommitIfAny("Delete selection", _selectMode.BuildDelete(_renderer.Map));
                    return true;
            }

            Vector3I offset = key switch
            {
                Key.Up       => CameraAxis(camera, -camera.GlobalBasis.Z),
                Key.Down     => -CameraAxis(camera, -camera.GlobalBasis.Z),
                Key.Right    => CameraAxis(camera, camera.GlobalBasis.X),
                Key.Left     => -CameraAxis(camera, camera.GlobalBasis.X),
                Key.Pageup   => Vector3I.Up,
                Key.Pagedown => Vector3I.Down,
                _            => Vector3I.Zero,
            };
            if (offset == Vector3I.Zero)
            {
                return false;
            }
            MoveSelection(offset);
            return true;
        }

        private void MoveSelection(Vector3I offset)
        {
            CommitIfAny("Move selection", _selectMode.BuildMove(_renderer.Map, offset));
            SyncSelectionToDock();
        }

        // Dock buttons: no-op without a map, then refresh the yellow box (the mouse may be on the dock).
        private void RunSelectionOp(System.Action op)
        {
            if (_renderer?.Map == null)
            {
                return;
            }
            op();
            SyncSelectionToDock();
            if (_lastCamera != null)
            {
                UpdateGhost(_lastCamera, _lastMousePos);
            }
        }

        // The dock section shows only in Select mode; the selection itself survives other modes.
        private void SyncSelectionToDock()
            => _dock?.SetSelectionInfo(_modeId == EditModeId.Select && _selectMode.bHasSelection,
                _selectMode.Min, _selectMode.Max);

        // worldDir flattened onto the map's XZ plane and snapped to the dominant axis.
        private Vector3I CameraAxis(Camera3D camera, Vector3 worldDir)
        {
            Vector3 local = _renderer.GlobalTransform.Basis.Inverse() * worldDir;
            if (Mathf.Abs(local.X) >= Mathf.Abs(local.Z))
            {
                return new Vector3I(Mathf.Sign(local.X), 0, 0);
            }
            return new Vector3I(0, 0, Mathf.Sign(local.Z));
        }

        private void CommitIfAny(string actionName, MapEdit edit)
        {
            if (edit != null && edit.Count > 0)
            {
                CommitEdit(actionName, edit);
            }
        }

        // Translucent box over the selected columns, from the lowest to the highest filled cell.
        private void SpawnSelectionGhost(Vector2I min, Vector2I max)
        {
            int minY = _activeLayer, maxY = _activeLayer;
            if (_selectMode.bHasSelection && min == _selectMode.Min && max == _selectMode.Max)
            {
                if (!_selectMode.TryGetHeightRange(_renderer.Map, out minY, out maxY))
                {
                    minY = maxY = _activeLayer;
                }
            }
            float cs = _renderer.CellSize;
            var size = new Vector3(max.X - min.X + 1, maxY - minY + 1, max.Y - min.Y + 1) * cs;
            var ghost = new MeshInstance3D
            {
                Mesh = new BoxMesh { Size = size + Vector3.One * 0.04f * cs },
                MaterialOverride = _ghostSelectMat ??= MakeGhostMat(new Color("#FFCC4440")),
                Position = new Vector3(min.X * cs, minY * cs, min.Y * cs) + size * 0.5f,
            };
            ghost.SetMeta(GhostMeta, true);
            _renderer.AddChild(ghost);
            ghost.Owner = null;
        }

        private PickResult PickFromMouse(Camera3D camera, Vector2 mousePos)
        {
            Transform3D inv = _renderer.GlobalTransform.AffineInverse();
            Vector3 localOrigin = inv * camera.ProjectRayOrigin(mousePos);
            Vector3 localDir    = (inv.Basis * camera.ProjectRayNormal(mousePos)).Normalized();
            return CellPicker.Pick(_renderer.Map, localOrigin, localDir, _renderer.CellSize, _activeLayer);
        }

        private void CommitEdit(string actionName, MapEdit edit)
        {
            EditorUndoRedoManager undo = _owner.GetUndoRedo();
            undo.CreateAction(actionName);
            undo.AddDoMethod(_renderer, MapRenderer.MethodName.ApplyEdit, edit, true);
            undo.AddUndoMethod(_renderer, MapRenderer.MethodName.ApplyEdit, edit, false);
            undo.CommitAction();
        }

        private void UpdateGhost(Camera3D camera, Vector2 mousePos)
        {
            ClearGhosts();
            if (_renderer?.Map == null) return;

            PickResult pick = PickFromMouse(camera, mousePos);

            if (_modeId == EditModeId.Select)
            {
                // Live rectangle while dragging, else the committed selection (drawn even when the
                // cursor is off the map).
                if (_bDragging && _selectMode.TryGetDragRect(pick, out Vector2I dragMin, out Vector2I dragMax))
                {
                    SpawnSelectionGhost(dragMin, dragMax);
                }
                else if (_selectMode.bHasSelection)
                {
                    SpawnSelectionGhost(_selectMode.Min, _selectMode.Max);
                }
                return;
            }
            if (!pick.Hit) return;

            if (_modeId == EditModeId.Pick)
            {
                // Preview what a plain click would take: the layer plate on top, else the block.
                string layerTile = LayerTileOnTop(pick, out Vector3I standing);
                TileDefinition layerDef = layerTile != null ? _renderer.GetTileDef(layerTile) : null;
                if (layerDef != null)
                {
                    SpawnLayerGhost(standing, layerDef, false);
                    return;
                }
            }

            TileDefinition def = CurrentTileDef();
            bool bEraseLayer = _modeId == EditModeId.Erase && IsLayerTile(def);
            if ((PlacesCurrentTile || bEraseLayer) && IsLayerTile(def))
            {
                foreach (var (cell, tileId) in ActiveMode.GetPreview(_renderer.Map, pick))
                {
                    Vector3I standing = LayerCellFor(cell);
                    if (!PassesMask(cell))
                    {
                        continue;
                    }
                    if (bEraseLayer)
                    {
                        // Red plate only where that layer actually has something to clear.
                        if (_renderer.Map.GetLayerTile(def.Layer, standing) != null)
                        {
                            SpawnLayerGhost(standing, def, true);
                        }
                    }
                    else if (tileId != null && CanHoldLayer(standing))
                    {
                        SpawnLayerGhost(standing, def, false);
                    }
                }
                return;
            }

            // Paint and Pick show a cell as it is turned now; placing modes show the turn to come.
            bool bInPlace = _modeId == EditModeId.Paint || _modeId == EditModeId.Pick;
            int turns = _bDragging ? _dragTurns : TurnsFor(pick);
            foreach (var (cell, tileId) in ActiveMode.GetPreview(_renderer.Map, pick))
            {
                if (!PassesMask(cell))
                {
                    continue;
                }
                SpawnGhost(cell, tileId, bInPlace ? _renderer.Map.GetRotation(cell) : turns);
            }
        }

        // --- Mask ---

        // WorldEdit gmask: with a mask tile set, Paint and Erase only touch cells holding that tile.
        // Placing modes ignore it — they build into empty cells, which never match.
        private bool MaskApplies => !string.IsNullOrEmpty(_savedMaskTileId)
            && (_modeId == EditModeId.Paint || _modeId == EditModeId.Erase);

        private bool PassesMask(Vector3I cell)
            => !MaskApplies || _renderer.Map.GetTileId(cell) == _savedMaskTileId;

        // Drops the entries the mask protects. Runs before ToLayerEditIfNeeded, so a surface painted
        // with a mask lands only on floors of the mask tile.
        private MapEdit ApplyMask(MapEdit edit)
        {
            if (!MaskApplies)
            {
                return edit;
            }
            var masked = new MapEdit();
            foreach (var (cell, oldId, newId) in edit.Entries)
            {
                if (oldId == _savedMaskTileId)
                {
                    masked.Add(cell, oldId, newId);
                }
            }
            return masked.Count > 0 ? masked : null;
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
            if (edit == null)
            {
                return null;
            }
            TileDefinition def = CurrentTileDef();
            if (!IsLayerTile(def))
            {
                // Paint retiles in place: no new turn, each cell keeps its own.
                edit.bKeepRotations = _modeId == EditModeId.Paint;
                return edit;
            }
            if (_modeId == EditModeId.Erase)
            {
                // Same rule as right-click: with a layer tile selected, Erase lifts that layer off the
                // floors in the box and never touches the blocks.
                var clear = new MapEdit();
                foreach (var (cell, _, _) in edit.Entries)
                {
                    Vector3I standing = LayerCellFor(cell);
                    string oldId = _renderer.Map.GetLayerTile(def.Layer, standing);
                    if (oldId != null)
                    {
                        clear.AddLayer(def.Layer, standing, oldId, null);
                    }
                }
                return clear.Count > 0 ? clear : null;
            }
            if (!PlacesCurrentTile)
            {
                return edit;
            }
            var layerEdit = new MapEdit();
            foreach (var (cell, _, newId) in edit.Entries)
            {
                Vector3I standing = LayerCellFor(cell);
                if (newId == null || !CanHoldLayer(standing))
                {
                    continue;
                }
                string oldId = _renderer.Map.GetLayerTile(def.Layer, standing);
                if (oldId != def.Id)
                {
                    layerEdit.AddLayer(def.Layer, standing, oldId, def.Id);
                }
            }
            return layerEdit.Count > 0 ? layerEdit : null;
        }

        // Placing modes target the open standing cell already; Paint and Erase target the floor block
        // itself, so its layer cell is one up.
        private Vector3I LayerCellFor(Vector3I cell)
            => _modeId == EditModeId.Paint || _modeId == EditModeId.Erase ? cell + Vector3I.Up : cell;

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

        private void SpawnLayerGhost(Vector3I cell, TileDefinition def, bool bErase)
        {
            float cs = _renderer.CellSize;
            var ghost = new MeshInstance3D { Mesh = _renderer.GetPlateMesh(def.Id) };
            if (bErase)
            {
                ghost.MaterialOverride = _ghostEraseMat ??= MakeGhostMat(new Color("#FF443388"));
            }
            ghost.SetMeta(GhostMeta, true);
            ghost.Position = new Vector3((cell.X + 0.5f) * cs, cell.Y * cs + 0.01f, (cell.Z + 0.5f) * cs);
            _renderer.AddChild(ghost);
            ghost.Owner = null;
        }

        // Orientation for a placement made from this pick. A pillar lies along a clicked side face's
        // axis; a tile with an AttachDirection turns to meet the wall face that was clicked;
        // everything else (and floor/void clicks) uses the R-key turn.
        private int TurnsFor(PickResult pick)
        {
            if (_axisLock != AxisLock.Auto)
            {
                return ManualTurns();
            }
            TileDefinition def = CurrentTileDef();
            if (def != null && def.bPillar && pick.Hit && !pick.FromPlane && pick.Normal.Y == 0)
            {
                return MapRenderer.TiltBit | (pick.Normal.X != 0 ? 1 : 0);
            }
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

        // The turn the keys alone give: R's yaw, tipped onto the locked axis. A lying tile's yaw is
        // its axis, so R does nothing while X or Z is locked.
        private int ManualTurns() => _axisLock switch
        {
            AxisLock.AlongX => MapRenderer.TiltBit | 1,
            AxisLock.AlongZ => MapRenderer.TiltBit,
            _               => _rotation,
        };

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
            if (!bErase && bBottomPivot)
            {
                // The real tile, own materials and all, so texture and turn read exactly as they'll
                // land. A translucent blue overlay pass marks it as not placed yet.
                ghost.MaterialOverlay = _ghostTintMat ??= MakeGhostMat(new Color("#3D8BFF73"));
            }
            else
            {
                ghost.MaterialOverride = bErase
                    ? (_ghostEraseMat ??= MakeGhostMat(new Color("#FF443388")))
                    : (_ghostPlaceMat ??= MakeGhostMat(new Color("#55DDBBAA")));
            }

            float yLocal = bBottomPivot ? cell.Y * cs : (cell.Y + 0.5f) * cs;
            ghost.Position = new Vector3((cell.X + 0.5f) * cs, yLocal, (cell.Z + 0.5f) * cs);
            ghost.Basis = MapRenderer.TurnBasis(turns);
            if (bBottomPivot)
            {
                ghost.Position += MapRenderer.PivotShift(ghost.Basis, cs);
            }
            if (!bErase && bBottomPivot && _renderer.Map.HasCell(cell))
            {
                // Paint/Pick ghost a filled cell: inflate a hair so it doesn't z-fight the block.
                ghost.Basis = ghost.Basis.Scaled(Vector3.One * 1.04f);
                ghost.Position -= new Vector3(0f, 0.02f * cs, 0f);
            }

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
