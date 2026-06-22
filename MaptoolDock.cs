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
        public Action<string> ModeChanged;      // "Place" | "Erase" | "Room" | "FloodFill"
        public Action<int> LayerChanged;
        public Action RefreshRequested;

        private Button _placeBtn, _eraseBtn, _roomBtn, _floodFillBtn;
        private LineEdit _search;
        private ItemList _tileList;
        private Label _selectedLabel;
        private Label _layerLabel;

        private readonly List<TileDefinition> _filtered = new();
        private int _layer;

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
            var modeRow = new HBoxContainer();
            root.AddChild(modeRow);
            var group = new ButtonGroup();
            _placeBtn    = new Button { Text = "Place",   ToggleMode = true, ButtonGroup = group, ButtonPressed = true };
            _eraseBtn    = new Button { Text = "Erase",   ToggleMode = true, ButtonGroup = group };
            _roomBtn     = new Button { Text = "Room",    ToggleMode = true, ButtonGroup = group };
            _floodFillBtn = new Button { Text = "Flood",  ToggleMode = true, ButtonGroup = group };
            _placeBtn.Pressed     += () => ModeChanged?.Invoke("Place");
            _eraseBtn.Pressed     += () => ModeChanged?.Invoke("Erase");
            _roomBtn.Pressed      += () => ModeChanged?.Invoke("Room");
            _floodFillBtn.Pressed += () => ModeChanged?.Invoke("FloodFill");
            modeRow.AddChild(_placeBtn);
            modeRow.AddChild(_eraseBtn);
            modeRow.AddChild(_roomBtn);
            modeRow.AddChild(_floodFillBtn);

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

            // --- Search ---
            _search = new LineEdit { PlaceholderText = "search tiles…" };
            _search.TextChanged += _ => Repopulate();
            root.AddChild(_search);

            // --- Tile list ---
            _tileList = new ItemList { SizeFlagsVertical = SizeFlags.ExpandFill, CustomMinimumSize = new Vector2(0, 200) };
            _tileList.ItemSelected += OnItemSelected;
            root.AddChild(_tileList);

            // --- Footer ---
            var refresh = new Button { Text = "Refresh tiles" };
            refresh.Pressed += () => { TileLibrary.Refresh(); RefreshRequested?.Invoke(); Repopulate(); };
            root.AddChild(refresh);

            _selectedLabel = new Label { Text = "Selected: none" };
            root.AddChild(_selectedLabel);

            Repopulate();
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
                _filtered.Add(def);
                string label = string.IsNullOrEmpty(def.DisplayName) ? def.Id : def.DisplayName;
                _tileList.AddItem(label);
            }
        }

        public void RestoreState(string modeName, string tileId)
        {
            var btn = modeName switch
            {
                "Erase"     => _eraseBtn,
                "Room"      => _roomBtn,
                "FloodFill" => _floodFillBtn,
                _           => _placeBtn,
            };
            btn?.SetPressedNoSignal(true);

            if (!string.IsNullOrEmpty(tileId))
            {
                for (int i = 0; i < _filtered.Count; i++)
                {
                    if (_filtered[i].Id != tileId) { continue; }
                    _tileList.Select(i);
                    string label = string.IsNullOrEmpty(_filtered[i].DisplayName) ? _filtered[i].Id : _filtered[i].DisplayName;
                    _selectedLabel.Text = $"Selected: {label}";
                    break;
                }
            }
        }

        private void OnItemSelected(long index)
        {
            if (index < 0 || index >= _filtered.Count)
            {
                return;
            }
            var def = _filtered[(int)index];
            _selectedLabel.Text = $"Selected: {(string.IsNullOrEmpty(def.DisplayName) ? def.Id : def.DisplayName)}";
            TileSelected?.Invoke(def.Id);
        }
    }
}
#endif
