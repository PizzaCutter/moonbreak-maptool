@tool
extends EditorPlugin

var _impl: Node = null
var _dock = null  # EditorDock — kept here so it survives C# assembly reload

func _enter_tree() -> void:
	_full_setup()

func _exit_tree() -> void:
	_remove_dock()
	if is_instance_valid(_impl):
		_impl.queue_free()
	_impl = null

func _process(_delta: float) -> void:
	if not is_instance_valid(_impl) or not _impl.IsInitialized():
		_full_setup()

func _forward_3d_gui_input(camera: Camera3D, event: InputEvent) -> int:
	if not is_instance_valid(_impl): return 0
	return _impl.Forward3DGuiInputImpl(camera, event)

func _handles(object: Object) -> bool:
	if not is_instance_valid(_impl): return false
	return _impl.HandlesImpl(object)

func _edit(object: Object) -> void:
	if is_instance_valid(_impl): _impl.EditImpl(object)

func _make_visible(visible: bool) -> void:
	if is_instance_valid(_impl): _impl.MakeVisibleImpl(visible)

func _remove_dock() -> void:
	if is_instance_valid(_dock):
		remove_dock(_dock)
		_dock.queue_free()
	_dock = null

func _full_setup() -> void:
	_remove_dock()
	# Reuse the existing impl node when it's still valid — its [Export] fields survive C# reload
	# and carry saved state (tile selection, mode) into the new SetupImpl call.
	# Only create a fresh node when the GodotObject itself is gone.
	if not is_instance_valid(_impl):
		for child in get_children():
			child.queue_free()
		_impl = null
		var ImplClass = load("res://addons/moonbreak_maptool/MaptoolPlugin.cs")
		if ImplClass == null:
			return
		_impl = ImplClass.new()
		add_child(_impl)
	_dock = _impl.SetupImpl()
