#if TOOLS
using Godot;
using System;
using System.Collections.Generic;

namespace Moonbreak.Maptool
{
    [Tool]
    public partial class MaptoolDock : EditorDock
    {
        public Action<string> TileSelected;     // tile Id, or null when cleared
        public Action<string> ModeChanged;      // a Name from Modes
        public Action<int> LayerChanged;
        public Action<bool> HollowChanged;
        public Action RefreshRequested;
        public Action<string> MaskChanged;      // tile Id, or null when the mask is off
        public Action<Vector3I> MoveSelectionRequested;  // offset in cells
        public Action DeleteSelectionRequested;
        public Action ClearSelectionRequested;

        // Every edit mode, in button order. Shift + Key (physical number row) is the shortcut the plugin listens for,
        // so the button row and the keys can never disagree.
        public static readonly (string Name, string Label, Key Key)[] Modes =
        {
            ("Place",     "Place",  Key.Key1),
            ("Erase",     "Erase",  Key.Key2),
            ("Paint",     "Paint",  Key.Key3),
            ("Pick",      "Pick",   Key.Key4),
            ("Line",      "Line",   Key.Key5),
            ("Room",      "Room",   Key.Key6),
            ("Circle",    "Circle", Key.Key7),
            ("FloodFill", "Flood",  Key.Key8),
            ("Select",    "Select", Key.Key9),
        };

        private readonly Dictionary<string, Button> _modeBtns = new();
        private CheckBox _hollowCheck;
        private LineEdit _search;
        private ItemList _tileList;
        private Label _selectedLabel;
        private Label _layerLabel;
        private Label _axisLabel;

        private readonly List<TileDefinition> _filtered = new();
        private int _layer;

        // Recently used tile Ids, current first (Minecraft hotbar). [0] is always the selected tile.
        private const int RecentMax = 8;
        private readonly List<string> _recent = new();
        public IReadOnlyList<string> Recent => _recent;
        private HFlowContainer _hotbar;
        private StyleBox _activeStyle;

        private string _maskTileId;
        private Label _maskLabel;

        private VBoxContainer _selectionBox;
        private Label _selectionLabel;
        private SpinBox _moveX, _moveY, _moveZ;

        // Thumbnails by tile Id. Mesh previews arrive async from the editor previewer.
        private readonly Dictionary<string, Texture2D> _icons = new();
        private readonly HashSet<string> _pendingIcons = new();
        private const int IconSize = 48;

        public MaptoolDock()
        {
            // Set before AddDock so the editor places + tabs it correctly on first add.
            Title = "Map Tool";
            DefaultSlot = DockSlot.RightUl;
        }

        public override void _Ready()
        {
            // EditorDock is a MarginContainer → one content child that lays out the rows.
            var root = new VBoxContainer();
            root.AddThemeConstantOverride("separation", 6);
            AddChild(root);

            // --- Mode toggle ---
            // Flow container: eight modes + Hollow wrap instead of forcing the dock wide.
            var modeRow = new HFlowContainer();
            root.AddChild(modeRow);
            var group = new ButtonGroup();
            StyleBox activeStyle = _activeStyle = MakeActiveModeStyle();
            foreach (var (name, label, key) in Modes)
            {
                var btn = new Button
                {
                    Text = label,
                    TooltipText = $"{label} (Shift+{OS.GetKeycodeString(key)})",
                    ToggleMode = true,
                    ButtonGroup = group,
                    ButtonPressed = name == "Place",
                };
                // The editor's own pressed look is barely darker than normal — paint the active mode
                // in the accent colour so it reads at a glance.
                btn.AddThemeStyleboxOverride("pressed", activeStyle);
                btn.AddThemeStyleboxOverride("hover_pressed", activeStyle);
                btn.AddThemeColorOverride("font_pressed_color", new Color("#111111"));
                btn.AddThemeColorOverride("font_hover_pressed_color", new Color("#111111"));
                btn.Pressed += () => ModeChanged?.Invoke(name);
                modeRow.AddChild(btn);
                _modeBtns[name] = btn;
            }

            _hollowCheck = new CheckBox { Text = "Hollow" };
            _hollowCheck.Toggled += bPressed => HollowChanged?.Invoke(bPressed);
            modeRow.AddChild(_hollowCheck);

            // --- Active layer ---
            var layerRow = new HBoxContainer();
            root.AddChild(layerRow);
            var down = new Button { Text = "-" };
            var up = new Button { Text = "+" };
            _layerLabel = new Label { Text = "Layer: 0" };
            down.Pressed += () => SetLayer(_layer - 1);
            up.Pressed += () => SetLayer(_layer + 1);
            layerRow.AddChild(new Label { Text = "Build layer" });
            layerRow.AddChild(down);
            layerRow.AddChild(_layerLabel);
            layerRow.AddChild(up);
            _axisLabel = new Label
            {
                TooltipText = "T / Shift+T cycles: Auto (logs follow the clicked face), Upright, along X, along Z",
                MouseFilter = MouseFilterEnum.Pass,
            };
            layerRow.AddChild(_axisLabel);
            SetAxisLabel("Auto");

            // --- Mask ---
            var maskRow = new HBoxContainer();
            root.AddChild(maskRow);
            _maskLabel = new Label
            {
                SizeFlagsHorizontal = SizeFlags.ExpandFill,
                TooltipText = "Paint and Erase only touch cells of this tile. Shift+click in Pick mode sets it.",
                MouseFilter = MouseFilterEnum.Pass,
            };
            var maskUse = new Button { Text = "Use selected", TooltipText = "Mask to the selected tile" };
            var maskClear = new Button { Text = "Clear" };
            maskUse.Pressed += () => SetMask(_recent.Count > 0 ? _recent[0] : null);
            maskClear.Pressed += () => SetMask(null);
            maskRow.AddChild(_maskLabel);
            maskRow.AddChild(maskUse);
            maskRow.AddChild(maskClear);
            UpdateMaskLabel();

            // --- Selection (Select mode) ---
            BuildSelectionSection(root);

            // --- Recent tiles ---
            _hotbar = new HFlowContainer();
            root.AddChild(_hotbar);

            // --- Search ---
            _search = new LineEdit { PlaceholderText = "search tiles…" };
            _search.TextChanged += _ => Repopulate();
            root.AddChild(_search);

            // --- Tile list ---
            // Thumbnail grid: icon on top, name under it, as many columns as the dock fits.
            _tileList = new ItemList
            {
                SizeFlagsVertical = SizeFlags.ExpandFill,
                CustomMinimumSize = new Vector2(0, 200),
                IconMode = ItemList.IconModeEnum.Top,
                MaxColumns = 0,
                SameColumnWidth = true,
                FixedColumnWidth = 84,
                FixedIconSize = new Vector2I(IconSize, IconSize),
                MaxTextLines = 2,
            };
            _tileList.ItemSelected += OnItemSelected;
            root.AddChild(_tileList);

            // --- Footer ---
            var refresh = new Button { Text = "Refresh tiles" };
            refresh.Pressed += () =>
            {
                TileLibrary.Refresh();
                _icons.Clear();  // meshes may have changed; previews are re-queued on demand
                RefreshRequested?.Invoke();
                Repopulate();
                RebuildHotbar();
            };
            root.AddChild(refresh);

            _selectedLabel = new Label { Text = "Selected: none" };
            root.AddChild(_selectedLabel);

            Repopulate();
        }

        // Bounds readout + "move by" numbers. Hidden until something is selected.
        private void BuildSelectionSection(VBoxContainer root)
        {
            _selectionBox = new VBoxContainer { Visible = false };
            root.AddChild(_selectionBox);

            _selectionLabel = new Label();
            _selectionBox.AddChild(_selectionLabel);

            var moveRow = new HBoxContainer();
            _selectionBox.AddChild(moveRow);
            moveRow.AddChild(new Label { Text = "Move by" });
            _moveX = AddAxisSpin(moveRow, "X");
            _moveY = AddAxisSpin(moveRow, "Y");
            _moveZ = AddAxisSpin(moveRow, "Z");

            var buttonRow = new HBoxContainer();
            _selectionBox.AddChild(buttonRow);
            var move = new Button { Text = "Move", TooltipText = "Shift the selection by X/Y/Z cells (one undo step)" };
            var delete = new Button { Text = "Delete", TooltipText = "Erase everything in the selection" };
            var clear = new Button { Text = "Deselect" };
            move.Pressed += () =>
            {
                var offset = new Vector3I((int)_moveX.Value, (int)_moveY.Value, (int)_moveZ.Value);
                if (offset != Vector3I.Zero)
                {
                    MoveSelectionRequested?.Invoke(offset);
                }
            };
            delete.Pressed += () => DeleteSelectionRequested?.Invoke();
            clear.Pressed += () => ClearSelectionRequested?.Invoke();
            buttonRow.AddChild(move);
            buttonRow.AddChild(delete);
            buttonRow.AddChild(clear);
        }

        private static SpinBox AddAxisSpin(HBoxContainer row, string axis)
        {
            var spin = new SpinBox
            {
                MinValue = -256,
                MaxValue = 256,
                Step = 1,
                Prefix = axis,
                SelectAllOnFocus = true,
                SizeFlagsHorizontal = SizeFlags.ExpandFill,
            };
            row.AddChild(spin);
            return spin;
        }

        // Plugin pushes the current selection here after every change. bHas = false hides the section.
        public void SetSelectionInfo(bool bHas, Vector2I min, Vector2I max)
        {
            if (_selectionBox == null)
            {
                return;
            }
            _selectionBox.Visible = bHas;
            if (bHas)
            {
                Vector2I size = max - min + Vector2I.One;
                _selectionLabel.Text = $"Selection: X {min.X}..{max.X}, Z {min.Y}..{max.Y}  ({size.X}×{size.Y})";
            }
        }

        // Copy of the editor's pressed button box (keeps its margins/corners) filled with the accent.
        private static StyleBox MakeActiveModeStyle()
        {
            Theme theme = EditorInterface.Singleton.GetEditorTheme();
            Color accent = theme.HasColor("accent_color", "Editor")
                ? theme.GetColor("accent_color", "Editor")
                : new Color("#70BAFA");
            var style = theme.GetStylebox("pressed", "Button").Duplicate() as StyleBoxFlat
                ?? new StyleBoxFlat { ContentMarginLeft = 8, ContentMarginRight = 8, ContentMarginTop = 4, ContentMarginBottom = 4 };
            style.BgColor = accent;
            return style;
        }

        public void SetAxisLabel(string axisLock)
        {
            _axisLabel.Text = $"  Axis: {axisLock} (T)";
            if (axisLock == "Auto")
            {
                _axisLabel.RemoveThemeColorOverride("font_color");
            }
            else
            {
                // Same loud orange as the mask — a forgotten lock makes placement look broken.
                _axisLabel.AddThemeColorOverride("font_color", new Color("#FFB347"));
            }
        }

        public void SetLayer(int layer)
        {
            _layer = layer;
            _layerLabel.Text = $"Layer: {_layer}";
            LayerChanged?.Invoke(_layer);
        }

        private void Repopulate()
        {
            _tileList.Clear();
            _filtered.Clear();

            string query = _search.Text;
            var scored = new List<(TileDefinition def, int score)>();
            foreach (var def in TileLibrary.GetAll())
            {
                if (def == null) { continue; }
                string label = string.IsNullOrEmpty(def.DisplayName) ? def.Id : def.DisplayName;
                int score = FuzzySearch.Score(query, label + " " + string.Join(" ", def.Tags));
                if (score >= 0) { scored.Add((def, score)); }
            }
            scored.Sort((a, b) => b.score.CompareTo(a.score));

            foreach (var (def, _) in scored)
            {
                // Row first, icon after: a cached preview calls OnPreviewReady synchronously inside
                // IconFor, and that sets the icon by _filtered index — the row must already exist.
                int index = _tileList.AddItem(LabelOf(def));
                _filtered.Add(def);
                Texture2D icon = IconFor(def);
                if (icon != null)
                {
                    _tileList.SetItemIcon(index, icon);
                }
            }
        }

        private static string LabelOf(TileDefinition def)
            => string.IsNullOrEmpty(def.DisplayName) ? def.Id : def.DisplayName;

        private static TileDefinition FindDef(string tileId)
        {
            foreach (var def in TileLibrary.GetAll())
            {
                if (def != null && def.Id == tileId)
                {
                    return def;
                }
            }
            return null;
        }

        // --- Thumbnails ---

        // Cached icon, or null while a preview renders (OnPreviewReady fills it in later).
        // Surfaces are a swatch of PreviewColor; tiles with a Mesh preview the mesh (an object's
        // Mesh is its ghost, usually more readable than a scene shot); mesh-less objects the scene.
        private Texture2D IconFor(TileDefinition def)
        {
            if (_icons.TryGetValue(def.Id, out var icon))
            {
                return icon;
            }
            if (!string.IsNullOrEmpty(def.Layer))
            {
                var image = Image.CreateEmpty(IconSize, IconSize, false, Image.Format.Rgba8);
                image.Fill(def.PreviewColor);
                return _icons[def.Id] = ImageTexture.CreateFromImage(image);
            }
            if (!_pendingIcons.Add(def.Id))
            {
                return null;
            }
            EditorResourcePreview previewer = EditorInterface.Singleton.GetResourcePreviewer();
            if (def.Mesh != null)
            {
                previewer.QueueEditedResourcePreview(def.Mesh, this, MethodName.OnPreviewReady, def.Id);
            }
            else if (def.Scene != null && !string.IsNullOrEmpty(def.Scene.ResourcePath))
            {
                previewer.QueueResourcePreview(def.Scene.ResourcePath, this, MethodName.OnPreviewReady, def.Id);
            }
            return null;
        }

        private void OnPreviewReady(string path, Texture2D preview, Texture2D thumbnail, Variant userdata)
        {
            string tileId = userdata.AsString();
            _pendingIcons.Remove(tileId);
            if (preview == null || !IsInstanceValid(_tileList))
            {
                return;
            }
            _icons[tileId] = preview;
            for (int i = 0; i < _filtered.Count; i++)
            {
                if (_filtered[i].Id == tileId)
                {
                    _tileList.SetItemIcon(i, preview);
                }
            }
            RebuildHotbar();
        }

        // --- Recent tiles ---

        private void PushRecent(string tileId)
        {
            _recent.Remove(tileId);
            _recent.Insert(0, tileId);
            if (_recent.Count > RecentMax)
            {
                _recent.RemoveRange(RecentMax, _recent.Count - RecentMax);
            }
            RebuildHotbar();
        }

        private void RebuildHotbar()
        {
            if (_hotbar == null)
            {
                return;
            }
            foreach (var child in _hotbar.GetChildren())
            {
                child.QueueFree();
            }
            for (int i = 0; i < _recent.Count; i++)
            {
                string tileId = _recent[i];
                TileDefinition def = FindDef(tileId);
                if (def == null)
                {
                    continue;
                }
                Texture2D icon = IconFor(def);
                var btn = new Button
                {
                    Icon = icon,
                    ExpandIcon = true,
                    CustomMinimumSize = new Vector2(40, 40),
                    // No thumbnail yet → first letters, so the slot is never blank.
                    Text = icon == null ? LabelOf(def)[..Math.Min(3, LabelOf(def).Length)] : "",
                    TooltipText = i == 1 ? $"{LabelOf(def)} (Shift+X)" : LabelOf(def),
                };
                if (i == 0)
                {
                    btn.AddThemeStyleboxOverride("normal", _activeStyle);
                    btn.AddThemeStyleboxOverride("hover", _activeStyle);
                }
                btn.Pressed += () => SelectTile(tileId);
                _hotbar.AddChild(btn);
            }
        }

        // Photoshop X: flip between the current tile and the one before it.
        public void SwapToPreviousTile()
        {
            if (_recent.Count > 1)
            {
                SelectTile(_recent[1]);
            }
        }

        // --- Mask ---

        public void SetMask(string tileId)
        {
            _maskTileId = string.IsNullOrEmpty(tileId) ? null : tileId;
            UpdateMaskLabel();
            MaskChanged?.Invoke(_maskTileId);
        }

        private void UpdateMaskLabel()
        {
            if (_maskTileId == null)
            {
                _maskLabel.Text = "Mask: off";
                _maskLabel.RemoveThemeColorOverride("font_color");
                return;
            }
            TileDefinition def = FindDef(_maskTileId);
            _maskLabel.Text = $"Mask: only {(def != null ? LabelOf(def) : _maskTileId)}";
            // Loud on purpose — a forgotten mask makes Paint look broken.
            _maskLabel.AddThemeColorOverride("font_color", new Color("#FFB347"));
        }

        public void RestoreState(string modeName, string tileId, IEnumerable<string> recent, string maskTileId)
        {
            SetModeNoSignal(modeName);

            _recent.Clear();
            foreach (string id in recent)
            {
                _recent.Add(id);
            }
            RebuildHotbar();

            _maskTileId = string.IsNullOrEmpty(maskTileId) ? null : maskTileId;
            UpdateMaskLabel();

            if (!string.IsNullOrEmpty(tileId))
            {
                for (int i = 0; i < _filtered.Count; i++)
                {
                    if (_filtered[i].Id != tileId) { continue; }
                    _tileList.Select(i);
                    _selectedLabel.Text = $"Selected: {LabelOf(_filtered[i])}";
                    break;
                }
            }
        }

        // Shortcut / auto-return path: reflect a mode the plugin switched to without re-firing ModeChanged.
        public void SetModeNoSignal(string modeName)
        {
            if (!_modeBtns.ContainsKey(modeName))
            {
                modeName = "Place";
            }
            // SetPressedNoSignal bypasses the ButtonGroup, so it never unpresses the old button —
            // set every button explicitly.
            foreach (var (name, btn) in _modeBtns)
            {
                btn.SetPressedNoSignal(name == modeName);
            }
        }

        // Pick-tool path: select tileId in the list as if it were clicked. A search that hides the
        // tile is cleared so the selection is visible. Returns false if no such tile is loaded.
        public bool SelectTile(string tileId)
        {
            int index = _filtered.FindIndex(def => def.Id == tileId);
            if (index < 0 && !string.IsNullOrEmpty(_search.Text))
            {
                _search.Text = "";
                Repopulate();
                index = _filtered.FindIndex(def => def.Id == tileId);
            }
            if (index < 0)
            {
                return false;
            }
            _tileList.Select(index);
            _tileList.EnsureCurrentIsVisible();
            OnItemSelected(index);  // Select() doesn't emit item_selected
            return true;
        }

        private void OnItemSelected(long index)
        {
            if (index < 0 || index >= _filtered.Count)
            {
                return;
            }
            var def = _filtered[(int)index];
            _selectedLabel.Text = $"Selected: {LabelOf(def)}";
            PushRecent(def.Id);
            TileSelected?.Invoke(def.Id);
        }
    }
}
#endif
