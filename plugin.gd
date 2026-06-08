@tool
extends EditorPlugin

# Map tool editor entry point.
# Registers the editor dock / toolbar UI. See MAPTOOL_DESIGN.md for architecture.
# Portable core (MapData, MapRenderer, TileDefinition, IEditMode) stays editor-independent;
# this plugin layer is the ONLY place allowed to touch editor-only APIs.

func _enter_tree() -> void:
	# TODO: instantiate + add_control_to_dock the palette/tool UI here.
	pass

func _exit_tree() -> void:
	# TODO: remove + free any controls added in _enter_tree.
	pass
