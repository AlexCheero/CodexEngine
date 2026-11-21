@tool
extends EditorPlugin

const Entity = preload("res://addons/codex_engine/EntityAuthoring.cs")
const DEFAULT_PALETTE = "res://addons/codex_engine/EntityWorkbenchPalette.cs"
const Inspector = preload("entity_inspector.gd")
const PipelineDock = preload("pipeline_dock.gd")

var palette
var dock := EditorDock.new()
var pipeline_dock := EditorDock.new()
var presets := ItemList.new()
var filter := LineEdit.new()
var dialog := ConfirmationDialog.new()
var preset_name := LineEdit.new()
var name_status := Label.new()
var inspector := Inspector.new()
var toolbar: Button
var add_entity: Button
var place: Button
var framed := {}

func _enter_tree():
    palette = load(ProjectSettings.get_setting("codex/editor/entity_palette", DEFAULT_PALETTE)).new()
    dock.title = "Entity Presets"
    dock.layout_key = "CodexEntityPresets"
    dock.default_slot = EditorDock.DOCK_SLOT_RIGHT_BL
    dock.icon_name = &"PackedScene"
    var column := VBoxContainer.new()
    dock.add_child(column)
    column.add_child(action_button("New Preset…", "PackedScene", prompt_preset))
    add_entity = action_button("Add Entity", "Add", create_entity)
    add_entity.tooltip_text = "Add an empty entity to the open scene"
    column.add_child(add_entity)
    filter.placeholder_text = "Filter presets"
    filter.clear_button_enabled = true
    filter.text_changed.connect(refresh.unbind(1))
    column.add_child(filter)
    presets.size_flags_vertical = Control.SIZE_EXPAND_FILL
    presets.custom_minimum_size.y = 105 * EditorInterface.get_editor_scale()
    presets.allow_reselect = true
    presets.item_selected.connect(update_actions.unbind(1))
    presets.item_activated.connect(open_item)
    column.add_child(presets)
    place = action_button("Place in Scene", "Instance", place_selected)
    column.add_child(place)
    add_dock(dock)
    pipeline_dock.title = "Pipeline"
    pipeline_dock.layout_key = "CodexPipeline"
    pipeline_dock.default_slot = EditorDock.DOCK_SLOT_RIGHT_BL
    pipeline_dock.icon_name = &"Script"
    var pipeline := PipelineDock.new()
    pipeline.workbench = self
    pipeline_dock.add_child(pipeline)
    add_dock(pipeline_dock)
    toolbar = action_button("Entity Presets", "PackedScene", dock.make_visible)
    toolbar.flat = true
    add_control_to_container(CONTAINER_SPATIAL_EDITOR_MENU, toolbar)

    dialog.title = "New Entity Preset"
    dialog.ok_button_text = "Create"
    dialog.dialog_hide_on_ok = false
    var fields := VBoxContainer.new()
    var label := Label.new()
    label.text = "Preset name"
    fields.add_child(label)
    preset_name.placeholder_text = "MyEntity"
    fields.add_child(preset_name)
    name_status.autowrap_mode = TextServer.AUTOWRAP_WORD_SMART
    fields.add_child(name_status)
    dialog.add_child(fields)
    add_child(dialog)
    dialog.register_text_enter(preset_name)
    dialog.confirmed.connect(create_preset)
    preset_name.text_changed.connect(validate_name.unbind(1))
    inspector.workbench = self
    add_inspector_plugin(inspector)
    EditorInterface.get_resource_filesystem().filesystem_changed.connect(refresh)
    scene_changed.connect(scene_opened)
    scene_closed.connect(func(path: String): framed.erase(path))
    refresh()

func _exit_tree():
    EditorInterface.get_resource_filesystem().filesystem_changed.disconnect(refresh)
    remove_inspector_plugin(inspector)
    remove_control_from_container(CONTAINER_SPATIAL_EDITOR_MENU, toolbar)
    remove_dock(dock)
    remove_dock(pipeline_dock)
    pipeline_dock.queue_free()
    toolbar.queue_free()
    dialog.queue_free()
    dock.queue_free()

func icon(key: String) -> Texture2D:
    return EditorInterface.get_base_control().get_theme_icon(key, "EditorIcons")

func action_button(label: String, icon_name: String, action: Callable) -> Button:
    var button := Button.new()
    button.text = label
    button.icon = icon(icon_name)
    # Navigating in the Inspector may free the button that emitted the signal.
    button.pressed.connect(action, CONNECT_DEFERRED)
    return button

func selected_path() -> String:
    var selected := presets.get_selected_items()
    return "" if selected.is_empty() else presets.get_item_metadata(selected[0])

func refresh():
    var previous := selected_path()
    presets.clear()
    for path: String in palette.Presets():
        var title := path.get_file().get_basename()
        if not filter.text.is_empty() and title.findn(filter.text) == -1:
            continue
        var i := presets.add_item(title, icon("PackedScene"))
        presets.set_item_metadata(i, path)
        presets.set_item_tooltip(i, "Double-click to open " + path)
        if path == previous:
            presets.select(i)
    update_actions()

func update_actions():
    var root := EditorInterface.get_edited_scene_root()
    add_entity.disabled = root == null
    place.disabled = root == null or root is Entity or selected_path().is_empty()

func open_item(index: int):
    open_preset(presets.get_item_metadata(index))

func open_preset(path: String):
    EditorInterface.open_scene_from_path(path)
    EditorInterface.set_main_screen_editor("3D")
    var root := EditorInterface.get_edited_scene_root()
    if root is Entity and root.scene_file_path == path:
        inspect_root.call_deferred(root)

func scene_opened(root: Node):
    update_actions()
    if root is Entity:
        EditorInterface.set_main_screen_editor("3D")
        inspect_root.call_deferred(root)
        if not framed.has(root.scene_file_path):
            framed[root.scene_file_path] = true
            frame_model.call_deferred(root)

func inspect_root(entity: Node):
    if not is_instance_valid(entity) or entity != EditorInterface.get_edited_scene_root():
        return
    EditorInterface.get_selection().clear()
    EditorInterface.inspect_object(entity, "", true)

func frame_model(entity: Node):
    if not is_instance_valid(entity) or entity != EditorInterface.get_edited_scene_root():
        return
    var bounds := AABB()
    var found := false
    for mesh: MeshInstance3D in entity.find_children("*", "MeshInstance3D", true, false):
        if mesh.mesh == null or not mesh.is_visible_in_tree():
            continue
        var box := mesh.global_transform * mesh.get_aabb()
        bounds = bounds.merge(box) if found else box
        found = true
    if not found:
        return
    var viewport := EditorInterface.get_editor_viewport_3d()
    var camera := viewport.get_camera_3d()
    var aspect := float(viewport.size.x) / maxi(1, viewport.size.y)
    var half_fov := atan(tan(deg_to_rad(camera.fov * 0.5)) * minf(1.0, aspect))
    var distance := maxf(0.2, bounds.size.length() * 0.58 / sin(half_fov))
    var center := bounds.get_center()
    camera.look_at_from_position(center + Vector3(0.42, 0.22, -1).normalized() * distance, center)

func prompt_preset():
    preset_name.clear()
    validate_name()
    dialog.popup_centered(Vector2i(int(340 * EditorInterface.get_editor_scale()), 0))
    preset_name.grab_focus()

func validate_name():
    var title := preset_name.text.strip_edges()
    var issue: String = palette.NameError(title)
    dialog.get_ok_button().disabled = not issue.is_empty()
    name_status.text = issue if not issue.is_empty() else palette.PresetPath(title)

func create_preset():
    var title := preset_name.text.strip_edges()
    var issue: String = palette.CreatePreset(title)
    if not issue.is_empty():
        name_status.text = issue
        return
    var path: String = palette.PresetPath(title)
    dialog.hide()
    EditorInterface.get_resource_filesystem().update_file(path)
    filter.clear()
    refresh()
    open_preset(path)

func create_entity():
    var entity = Entity.new()
    entity.name = "Entity"
    attach(EditorInterface.get_edited_scene_root(), entity, "Add Entity")

func place_selected():
    var scene := load(selected_path()) as PackedScene
    attach(EditorInterface.get_edited_scene_root(), scene.instantiate(PackedScene.GEN_EDIT_STATE_INSTANCE), "Place Entity Preset")

func attach(parent: Node, child: Node, action: String):
    var owner_root := EditorInterface.get_edited_scene_root()
    var undo := get_undo_redo()
    undo.create_action(action, UndoRedo.MERGE_DISABLE, owner_root)
    undo.add_do_method(self, "attach_and_select", parent, child, owner_root)
    undo.add_undo_method(self, "detach_and_select", parent, child)
    undo.add_do_reference(child)
    undo.commit_action()

func attach_and_select(parent: Node, child: Node, owner_root: Node):
    parent.add_child(child, true)
    child.owner = owner_root
    select_node(child)

func detach_and_select(parent: Node, child: Node):
    select_node(parent)
    parent.remove_child(child)

func select_node(node: Node):
    var selection := EditorInterface.get_selection()
    selection.clear()
    selection.add_node(node)
    EditorInterface.edit_node(node)
