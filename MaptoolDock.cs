#if TOOLS
using Godot;
using System;
using System.Collections.Generic;

namespace Moonbreak.Maptool
{
    // Editor dock: pick the active tile, mode, and build layer. Pure UI — it raises C# events the
    // plugin listens to and never touches MapData, picking, or undo itself. Built in code (no .tscn)
    // so the addon stays a flat set of scripts.
    //
    // Extends EditorDock (Godot 4.6+ dock API): the dock owns its own title + default slot, so the
    // plugin just calls AddDock(this). EditorDock is a MarginContainer (single child) — content goes
    // in one VBox. Editor-only type → whole file is #if TOOLS.
    [Tool]
    public partial class MaptoolDock : EditorDock
    {
        public Action<string> TileSelected;     // tile Id, or null when cleared
        public Action<string> ModeChanged;      // "Place" | "Erase" | "BoxFill" | "FloodFill"
        public Action<int> LayerChanged;
        public Action RefreshRequested;

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
            var placeBtn    = new Button { Text = "Place",     ToggleMode = true, ButtonGroup = group, ButtonPressed = true };
            var eraseBtn    = new Button { Text = "Erase",     ToggleMode = true, ButtonGroup = group };
            var boxFillBtn  = new Button { Text = "BoxFill",   ToggleMode = true, ButtonGroup = group };
            var floodFillBtn = new Button { Text = "Flood",    ToggleMode = true, ButtonGroup = group };
            placeBtn.Pressed     += () => ModeChanged?.Invoke("Place");
            eraseBtn.Pressed     += () => ModeChanged?.Invoke("Erase");
            boxFillBtn.Pressed   += () => ModeChanged?.Invoke("BoxFill");
            floodFillBtn.Pressed += () => ModeChanged?.Invoke("FloodFill");
            modeRow.AddChild(placeBtn);
            modeRow.AddChild(eraseBtn);
            modeRow.AddChild(boxFillBtn);
            modeRow.AddChild(floodFillBtn);

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

        // Rebuild the visible tile list from the library, filtered + ranked by the search box.
        private void Repopulate()
        {
            _tileList.Clear();
            _filtered.Clear();

            string query = _search.Text;
            var scored = new List<(TileDefinition def, int score)>();
            foreach (var def in TileLibrary.GetAll())
            {
                if (def == null)
                {
                    continue;
                }
                string label = string.IsNullOrEmpty(def.DisplayName) ? def.Id : def.DisplayName;
                int score = FuzzySearch.Score(query, label + " " + string.Join(" ", def.Tags));
                if (score >= 0)
                {
                    scored.Add((def, score));
                }
            }
            scored.Sort((a, b) => b.score.CompareTo(a.score));

            foreach (var (def, _) in scored)
            {
                _filtered.Add(def);
                string label = string.IsNullOrEmpty(def.DisplayName) ? def.Id : def.DisplayName;
                _tileList.AddItem(label);
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
