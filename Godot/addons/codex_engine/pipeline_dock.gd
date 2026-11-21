@tool
extends VBoxContainer

# Edits pipeline assets: which systems tick in each category, in what order, and which start active.
# A pipeline is the JSON file that CodexECS.PipelineAsset reads. The systems on offer come from systems.json in
# the same folder, which the game exports from its own assemblies.

const CATEGORIES: Array[String] = ["Init", "Update", "LateUpdate", "FixedUpdate", "LateFixedUpdate", "OnEnable", "OnDisable", "Reactive"]
const DIRECTORY_SETTING := "codex/editor/pipeline_directory"
const SUFFIX := ".pipeline.json"

var workbench: EditorPlugin
var directory := ""
var asset_path := ""
# Category name -> Array of {"Name": String, "Active": bool, "NonPausable": bool}, in tick order.
var asset := {}
# Array of {"Name": String, "Assembly": String, "Categories": Array}.
var catalog: Array = []
var known := {}
var folded := {}
var dirty := false

var assets := OptionButton.new()
var save_button: Button
var revert_button: Button
var status := Label.new()
var category := OptionButton.new()
var any_category := CheckBox.new()
var add_search := LineEdit.new()
var available := VBoxContainer.new()
var added_search := LineEdit.new()
var lists := VBoxContainer.new()

func _ready():
    var files := HBoxContainer.new()
    add_child(files)
    assets.size_flags_horizontal = Control.SIZE_EXPAND_FILL
    assets.tooltip_text = "Pipeline asset to edit"
    assets.item_selected.connect(_on_asset_selected)
    files.add_child(assets)
    save_button = workbench.action_button("Save", "Save", save)
    revert_button = workbench.action_button("Revert", "Reload", revert)
    files.add_child(save_button)
    files.add_child(revert_button)
    save_button.tooltip_text = "Write the pipeline asset"
    revert_button.tooltip_text = "Discard unsaved changes"
    var rescan: Button = workbench.action_button("", "Search", reload_all)
    rescan.tooltip_text = "Look for pipeline assets and exported systems again"
    files.add_child(rescan)

    status.autowrap_mode = TextServer.AUTOWRAP_WORD_SMART
    add_child(status)

    var target := HBoxContainer.new()
    add_child(target)
    var label := Label.new()
    label.text = "Add to"
    target.add_child(label)
    for category_name in CATEGORIES:
        category.add_item(category_name)
    category.select(CATEGORIES.find("FixedUpdate"))
    category.item_selected.connect(refresh.unbind(1))
    target.add_child(category)
    any_category.text = "Any system"
    any_category.tooltip_text = "Also offer systems whose System attribute names another category"
    any_category.toggled.connect(refresh_available.unbind(1))
    target.add_child(any_category)

    add_search.placeholder_text = "Find a system to add"
    add_search.clear_button_enabled = true
    add_search.text_changed.connect(refresh_available.unbind(1))
    add_child(add_search)
    var offer := ScrollContainer.new()
    offer.custom_minimum_size.y = 120 * EditorInterface.get_editor_scale()
    offer.horizontal_scroll_mode = ScrollContainer.SCROLL_MODE_DISABLED
    available.size_flags_horizontal = Control.SIZE_EXPAND_FILL
    offer.add_child(available)
    add_child(offer)

    add_child(HSeparator.new())
    added_search.placeholder_text = "Filter the pipeline's systems"
    added_search.clear_button_enabled = true
    added_search.text_changed.connect(refresh_lists.unbind(1))
    add_child(added_search)
    var scroll := ScrollContainer.new()
    scroll.size_flags_vertical = Control.SIZE_EXPAND_FILL
    scroll.horizontal_scroll_mode = ScrollContainer.SCROLL_MODE_DISABLED
    lists.size_flags_horizontal = Control.SIZE_EXPAND_FILL
    scroll.add_child(lists)
    add_child(scroll)
    reload_all()

# The folder holding *.pipeline.json and systems.json: the project setting, the project's own Pipelines folder,
# or the content of the game this standalone editor sits in (<game>/Engine/Godot).
func pipeline_directory() -> String:
    var configured: String = ProjectSettings.get_setting(DIRECTORY_SETTING, "")
    if not configured.is_empty():
        return ProjectSettings.globalize_path(configured) if configured.begins_with("res://") else configured
    var local := ProjectSettings.globalize_path("res://Pipelines")
    if DirAccess.dir_exists_absolute(local):
        return local
    return ProjectSettings.globalize_path("res://").path_join("../../Content/Pipelines").simplify_path()

func reload_all():
    directory = pipeline_directory()
    load_catalog()
    var previous := asset_path
    assets.clear()
    var found: Array[String] = []
    if DirAccess.dir_exists_absolute(directory):
        for file in DirAccess.get_files_at(directory):
            if file.ends_with(SUFFIX):
                found.append(file)
    found.sort()
    for file in found:
        assets.add_item(file.trim_suffix(SUFFIX))
        assets.set_item_metadata(assets.item_count - 1, directory.path_join(file))
    if found.is_empty():
        asset_path = ""
        asset = {}
        dirty = false
        refresh()
        return
    var index := 0
    for i in assets.item_count:
        if assets.get_item_metadata(i) == previous:
            index = i
    assets.select(index)
    if assets.get_item_metadata(index) != previous or not dirty:
        open_asset(assets.get_item_metadata(index))
    else:
        refresh()

func load_catalog():
    catalog = []
    known = {}
    var path := directory.path_join("systems.json")
    if not FileAccess.file_exists(path):
        return
    var parsed = JSON.parse_string(FileAccess.get_file_as_string(path))
    if parsed is Array:
        catalog = parsed
        for entry in catalog:
            known[entry.get("Name", "")] = true

func open_asset(path: String):
    asset_path = path
    asset = {}
    for category_name in CATEGORIES:
        asset[category_name] = []
    var parsed = JSON.parse_string(FileAccess.get_file_as_string(path))
    if parsed is Dictionary:
        for category_name in CATEGORIES:
            for entry in parsed.get(category_name, []):
                asset[category_name].append({
                    "Name": str(entry.get("Name", "")),
                    "Active": bool(entry.get("Active", true)),
                    "NonPausable": bool(entry.get("NonPausable", false))})
    dirty = false
    refresh()

func _on_asset_selected(index: int):
    open_asset(assets.get_item_metadata(index))

func save():
    if asset_path.is_empty():
        return
    var file := FileAccess.open(asset_path, FileAccess.WRITE)
    if file == null:
        status.text = "Could not write " + asset_path
        return
    # Categories and entry fields keep their order, so the file diffs cleanly.
    file.store_string(JSON.stringify(asset, "  ", false) + "\n")
    file.close()
    dirty = false
    refresh_status()

func revert():
    if not asset_path.is_empty():
        open_asset(asset_path)

func mark_dirty():
    dirty = true
    refresh()

func refresh():
    refresh_status()
    refresh_available()
    refresh_lists()

func refresh_status():
    save_button.disabled = not dirty
    revert_button.disabled = not dirty
    if asset_path.is_empty():
        status.text = "No %s file in %s" % ["*" + SUFFIX, directory]
    elif catalog.is_empty():
        status.text = "No systems.json beside the pipeline: export the game's systems to add new ones."
    else:
        status.text = ("Unsaved changes. " if dirty else "") + "%d systems on offer." % catalog.size()

func short_name(full: String) -> String:
    return full.get_slice(".", full.get_slice_count(".") - 1)

func selected_category() -> String:
    return CATEGORIES[category.selected]

func has_system(target: String, full: String) -> bool:
    for entry in asset.get(target, []):
        if entry["Name"] == full:
            return true
    return false

func refresh_available():
    for child in available.get_children():
        child.queue_free()
    if asset_path.is_empty():
        return
    var target := selected_category()
    var query := add_search.text.strip_edges()
    for entry in catalog:
        var full: String = entry.get("Name", "")
        if has_system(target, full):
            continue
        if not any_category.button_pressed and not entry.get("Categories", []).has(target):
            continue
        if not query.is_empty() and full.findn(query) == -1:
            continue
        var row := HBoxContainer.new()
        var add := workbench.action_button("", "Add", add_system.bind(target, full))
        add.tooltip_text = "Add to " + target
        row.add_child(add)
        var label := Label.new()
        label.text = short_name(full)
        label.tooltip_text = "%s (%s)" % [full, entry.get("Assembly", "")]
        label.mouse_filter = Control.MOUSE_FILTER_PASS
        label.size_flags_horizontal = Control.SIZE_EXPAND_FILL
        label.clip_text = true
        row.add_child(label)
        available.add_child(row)

func add_system(target: String, full: String):
    if has_system(target, full):
        return
    asset[target].append({"Name": full, "Active": true, "NonPausable": false})
    folded[target] = false
    mark_dirty()

func remove_system(target: String, index: int):
    asset[target].remove_at(index)
    mark_dirty()

func move_system(target: String, from: int, to: int):
    var entries: Array = asset[target]
    to = clampi(to, 0, entries.size() - 1)
    if from == to:
        return
    var entry = entries[from]
    entries.remove_at(from)
    entries.insert(to, entry)
    mark_dirty()

func set_flag(pressed: bool, target: String, index: int, flag: String):
    asset[target][index][flag] = pressed
    dirty = true
    refresh_status()

func toggle_fold(target: String):
    folded[target] = not folded.get(target, false)
    refresh_lists()

func refresh_lists():
    for child in lists.get_children():
        child.queue_free()
    if asset_path.is_empty():
        return
    var query := added_search.text.strip_edges()
    for target in CATEGORIES:
        var entries: Array = asset[target]
        if entries.is_empty() and target != selected_category():
            continue
        var header := Button.new()
        header.flat = true
        header.alignment = HORIZONTAL_ALIGNMENT_LEFT
        header.text = "%s (%d)" % [target, entries.size()]
        header.icon = workbench.icon("GuiTreeArrowRight" if folded.get(target, false) else "GuiTreeArrowDown")
        header.pressed.connect(toggle_fold.bind(target), CONNECT_DEFERRED)
        lists.add_child(header)
        if folded.get(target, false):
            continue
        for index in entries.size():
            var entry: Dictionary = entries[index]
            if not query.is_empty() and entry["Name"].findn(query) == -1:
                continue
            lists.add_child(system_row(target, index, entry, query.is_empty()))

func system_row(target: String, index: int, entry: Dictionary, reorder: bool) -> Control:
    var row := PipelineRow.new()
    row.dock = self
    row.category = target
    row.index = index
    row.reorder = reorder
    var up := workbench.action_button("", "MoveUp", move_system.bind(target, index, index - 1))
    var down := workbench.action_button("", "MoveDown", move_system.bind(target, index, index + 1))
    up.flat = true
    down.flat = true
    up.disabled = not reorder or index == 0
    down.disabled = not reorder or index == asset[target].size() - 1
    up.tooltip_text = "Tick earlier"
    down.tooltip_text = "Tick later"
    row.add_child(up)
    row.add_child(down)
    var active := CheckBox.new()
    active.button_pressed = entry["Active"]
    active.tooltip_text = "Active when the pipeline starts"
    active.toggled.connect(set_flag.bind(target, index, "Active"))
    row.add_child(active)
    var label := Label.new()
    label.text = short_name(entry["Name"])
    label.tooltip_text = entry["Name"]
    label.mouse_filter = Control.MOUSE_FILTER_PASS
    label.size_flags_horizontal = Control.SIZE_EXPAND_FILL
    label.clip_text = true
    if not catalog.is_empty() and not known.has(entry["Name"]):
        label.add_theme_color_override("font_color", get_theme_color("warning_color", "Editor"))
        label.tooltip_text += "\nNot in systems.json: the game will refuse a pipeline that names a missing system."
    row.add_child(label)
    var pausable := CheckBox.new()
    pausable.text = "Runs paused"
    pausable.button_pressed = entry["NonPausable"]
    pausable.tooltip_text = "Keeps ticking while the pipeline is paused"
    pausable.toggled.connect(set_flag.bind(target, index, "NonPausable"))
    row.add_child(pausable)
    var remove := workbench.action_button("", "Remove", remove_system.bind(target, index))
    remove.flat = true
    remove.tooltip_text = "Remove from " + target
    row.add_child(remove)
    return row

# One system of a category. Dragging a row onto another of the same category moves it there.
class PipelineRow extends HBoxContainer:
    var dock: Control
    var category := ""
    var index := 0
    var reorder := true

    func _get_drag_data(_at: Vector2):
        if not reorder:
            return null
        var preview := Label.new()
        preview.text = dock.short_name(dock.asset[category][index]["Name"])
        set_drag_preview(preview)
        return {"codex_pipeline_category": category, "index": index}

    func _can_drop_data(_at: Vector2, data) -> bool:
        return reorder and data is Dictionary and data.get("codex_pipeline_category", "") == category

    func _drop_data(_at: Vector2, data):
        dock.move_system.call_deferred(category, data["index"], index)
