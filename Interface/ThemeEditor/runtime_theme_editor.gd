extends Control

const PaletteScript = preload("res://Interface/ThemeEditor/theme_palette.gd")
const CoreScript = preload("res://Interface/ThemeEditor/theme_palette_core.gd")
const Service = preload("res://Interface/ThemeEditor/theme_palette_service.gd")
const PICKERS := {
	"Background": ["background_color", &"surface"],
	"Panel": ["panel_color", &"surface"],
	"Text": ["text_color", &"text"],
	"Muted": ["muted_text_color", &"muted_text"],
	"Highlight": ["highlight_color", &"highlight"],
	"Mute": ["mute_button_color", &"mute"],
	"Solo": ["solo_button_color", &"solo"],
	"Destructive": ["destructive_button_color", &"destructive"],
	"Intensity": ["intensity_color", &"intensity"],
	"TabBackground": ["tab_background_color", &"tab_background"],
	"TabGrid": ["tab_grid_color", &"tab_grid"],
	"TabString": ["tab_string_color", &"tab_string"],
	"TabText": ["tab_text_color", &"tab_text"],
	"BeatBackground": ["beat_background_color", &"beat_background"],
}

var core = CoreScript.new()
var palette
var preview_theme: Theme
var _pending_groups: Array[StringName] = []
var _preview_queued := false
var _status_key := ""
var _status_error := -1
@onready var preview: Control = find_child("PreviewInstance", true, false) as Control
@onready var status: Label = find_child("Status", true, false) as Label
@onready var difference: HSlider = find_child("ButtonHighlightDifference", true, false) as HSlider
@onready var tab_thickness: HSlider = find_child("TabLineThickness", true, false) as HSlider
@onready var notation_preview: Control = find_child("NotationPreview", true, false) as Control

func _ready() -> void:
	var saved = Service.load_effective_palette()
	palette = saved.copy() if saved != null else PaletteScript.new()
	var source := load(Service.THEME_PATH) as Theme
	preview_theme = core.create_preview_theme(source, palette)
	if preview_theme == null:
		_set_status("Could not create the theme preview.")
		return
	preview.theme = preview_theme
	if notation_preview != null:
		notation_preview.theme = preview_theme
	_initialize_notation_defaults(preview_theme)
	for picker_name in PICKERS:
		var picker := find_child(picker_name, true, false) as ColorPickerButton
		var fields: Array = PICKERS[picker_name]
		picker.color = palette.get(fields[0])
		picker.color_changed.connect(_on_color_changed.bind(fields[0], fields[1]))
	difference.set_value_no_signal(palette.button_highlight_difference)
	difference.value_changed.connect(_on_difference_changed)
	tab_thickness.set_value_no_signal(palette.tab_line_thickness)
	tab_thickness.value_changed.connect(_on_tab_thickness_changed)
	(find_child("SaveProjectDefault", true, false) as Button).pressed.connect(save_project_default)
	(find_child("SaveUserPreference", true, false) as Button).pressed.connect(save_user_preference)
	(find_child("Reset", true, false) as Button).pressed.connect(reset_preview)
	if not OS.has_feature("editor"):
		(find_child("SaveProjectDefault", true, false) as Button).disabled = true
		(find_child("SaveProjectDefault", true, false) as Button).tooltip_text = "Project defaults can only be saved from the editable project."

func _on_color_changed(value: Color, field: String, group: StringName) -> void:
	palette.set(field, value)
	_queue_preview(group)

func _on_difference_changed(value: float) -> void:
	palette.button_highlight_difference = value
	_queue_preview(&"button_difference")

func _on_tab_thickness_changed(value: float) -> void:
	palette.tab_line_thickness = roundi(value)
	_queue_preview(&"tab_line_thickness")

func _initialize_notation_defaults(source: Theme) -> void:
	# Read inactive overrides from the actual current theme rather than guessing.
	if not palette.active_groups.has(&"tab_background"):
		var tab_panel := source.get_stylebox("panel", "TabMeasureView") as StyleBoxFlat
		if tab_panel != null: palette.tab_background_color = tab_panel.bg_color
	if not palette.active_groups.has(&"tab_grid"): palette.tab_grid_color = source.get_color("grid", "TabMeasureView")
	if not palette.active_groups.has(&"tab_string"): palette.tab_string_color = source.get_color("string_line", "TabMeasureView")
	if not palette.active_groups.has(&"tab_text"): palette.tab_text_color = source.get_color("text", "TabMeasureView")
	if not palette.active_groups.has(&"beat_background"):
		var beat := source.get_stylebox("panel", "BeatEditorPanel") as StyleBoxFlat
		if beat != null: palette.beat_background_color = beat.bg_color
	if not palette.active_groups.has(&"tab_line_thickness"):
		palette.tab_line_thickness = source.get_constant("line_thickness", "TabMeasureView") if source.has_constant("line_thickness", "TabMeasureView") else 1

func _queue_preview(group: StringName) -> void:
	if not palette.active_groups.has(group):
		palette.active_groups.append(group)
	if not _pending_groups.has(group):
		_pending_groups.append(group)
	if not _preview_queued:
		_preview_queued = true
		call_deferred("_flush_preview")
	_set_status("Preview only — save to keep these colors.")

func _flush_preview() -> void:
	_preview_queued = false
	if _pending_groups.is_empty():
		return
	core.apply_to_preview(preview_theme, palette, _pending_groups)
	_initialize_notation_defaults(preview_theme)
	for picker_name in ["TabBackground", "TabGrid", "TabString", "TabText", "BeatBackground"]:
		var fields: Array = PICKERS[picker_name]
		if not palette.active_groups.has(fields[1]):
			(find_child(picker_name, true, false) as ColorPickerButton).color = palette.get(fields[0])
	_pending_groups.clear()
	preview.queue_redraw()

func save_project_default() -> void:
	if not OS.has_feature("editor"):
		_set_status("Project defaults can only be saved from the editable project.")
		return
	_save_palette(Service.PROJECT_PATH, "Palette saved for the standalone editor. It is not applied at app startup.")

func save_user_preference() -> void:
	_save_palette(Service.USER_PATH, "Palette saved for the standalone editor. It is not applied at app startup.")

func _save_palette(path: String, success_message: String) -> void:
	_flush_preview()
	var error: Error = core.save_palette(palette, path)
	_set_status(success_message if error == OK else "Could not save palette (error %d).", -1 if error == OK else error)

func reset_preview() -> void:
	var saved = Service.load_effective_palette()
	palette = saved.copy() if saved != null else PaletteScript.new()
	_pending_groups.clear()
	preview_theme = core.create_preview_theme(load(Service.THEME_PATH) as Theme, palette)
	preview.theme = preview_theme
	if notation_preview != null:
		notation_preview.theme = preview_theme
	_initialize_notation_defaults(preview_theme)
	for picker_name in PICKERS:
		var picker := find_child(picker_name, true, false) as ColorPickerButton
		picker.color = palette.get(PICKERS[picker_name][0])
	difference.set_value_no_signal(palette.button_highlight_difference)
	tab_thickness.set_value_no_signal(palette.tab_line_thickness)
	_set_status("Restored the last saved palette.")

func _set_status(key: String, error_code: int = -1) -> void:
	_status_key = key
	_status_error = error_code
	_refresh_status()

func _refresh_status() -> void:
	if not is_instance_valid(status) or _status_key.is_empty():
		return
	status.text = tr(_status_key) if _status_error < 0 else tr(_status_key) % _status_error

func _notification(what: int) -> void:
	if what == NOTIFICATION_TRANSLATION_CHANGED:
		_refresh_status()
