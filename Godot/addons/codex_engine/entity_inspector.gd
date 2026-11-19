@tool
extends EditorInspectorPlugin

const Entity = preload("res://addons/codex_engine/EntityAuthoring.cs")
var workbench: EditorPlugin

func _can_handle(object: Object) -> bool:
    return object is Entity or (object is Node and object.get_parent() is Entity
        and not workbench.palette.ComponentKey(object).is_empty())

func _parse_begin(object: Object):
    var node := object as Node
    var entity := node if node is Entity else node.get_parent()
    var panel := VBoxContainer.new()
    add_custom_control(panel)
    if entity != EditorInterface.get_edited_scene_root() and not entity.scene_file_path.is_empty():
        panel.add_child(workbench.action_button("Open Preset", "PackedScene", workbench.open_preset.bind(entity.scene_file_path)))
        return
    if node != entity:
        panel.add_child(workbench.action_button(entity.name, "Back", workbench.select_node.bind(entity)))
    var menu := MenuButton.new()
    menu.text = "Add Component"
    menu.icon = workbench.icon("Add")
    menu.flat = false
    panel.add_child(menu)
    var existing := {}
    for child in entity.get_children():
        var key: String = workbench.palette.ComponentKey(child)
        if key.is_empty():
            continue
        existing[key] = true
        if node == entity:
            var button: Button = workbench.action_button(child.name, workbench.palette.ComponentIcon(key), workbench.select_node.bind(child))
            button.alignment = HORIZONTAL_ALIGNMENT_LEFT
            button.flat = true
            panel.add_child(button)
    var popup := menu.get_popup()
    popup.allow_search = true
    var keys: PackedStringArray = workbench.palette.Components()
    var core_count: int = workbench.palette.CoreComponentCount()
    for i in keys.size():
        var key := keys[i]
        if i == core_count:
            popup.add_separator("Gameplay")
        popup.add_icon_item(workbench.icon(workbench.palette.ComponentIcon(key)), key.capitalize(), i)
        popup.set_item_disabled(popup.item_count - 1, existing.has(key))
    popup.id_pressed.connect(add_component.bind(entity, keys), CONNECT_DEFERRED)

func add_component(index: int, entity: Node, keys: PackedStringArray):
    var key := keys[index]
    workbench.attach(entity, workbench.palette.NewComponent(key), "Add " + key.capitalize())
