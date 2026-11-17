@tool
extends Control

## Editor-only comparison of normal and linked-repeat measures using production theme resources.
## Inspector geometry properties edit the shared Theme resource directly and leave it unsaved for review.

@export var shared_theme: Theme:
	set(value):
		if shared_theme == value:
			return
		if shared_theme != null and shared_theme.changed.is_connected(_on_resource_changed):
			shared_theme.changed.disconnect(_on_resource_changed)
		shared_theme = value
		if shared_theme != null:
			shared_theme.changed.connect(_on_resource_changed)
		update_minimum_size()
		notify_property_list_changed()
		queue_redraw()

@export_tool_button("Save Shared Theme", "Save") var save_shared_theme = _save_shared_theme

@export_group("Shared tab StyleBoxes")
@export var tab_panel: StyleBox:
	set(value): tab_panel = _replace_style(tab_panel, value)
@export var tab_entry: StyleBox:
	set(value): tab_entry = _replace_style(tab_entry, value)
@export var tab_note: StyleBox:
	set(value): tab_note = _replace_style(tab_note, value)
@export var tab_note_selected: StyleBox:
	set(value): tab_note_selected = _replace_style(tab_note_selected, value)
@export var tab_note_error: StyleBox:
	set(value): tab_note_error = _replace_style(tab_note_error, value)

func _get_property_list() -> Array[Dictionary]:
	return [
		{"name": "fret_row_height", "type": TYPE_INT, "hint": PROPERTY_HINT_RANGE, "hint_string": "8,200,1", "usage": PROPERTY_USAGE_EDITOR},
		{"name": "fret_note_height", "type": TYPE_INT, "hint": PROPERTY_HINT_RANGE, "hint_string": "8,100,1", "usage": PROPERTY_USAGE_EDITOR},
	]

func _get(property: StringName) -> Variant:
	if property == &"fret_row_height": return int(_theme_constant(&"row_height", 34.0))
	if property == &"fret_note_height": return int(_theme_constant(&"note_height", 23.0))
	return null

func _set(property: StringName, value: Variant) -> bool:
	if shared_theme == null:
		return false
	if property == &"fret_row_height":
		shared_theme.set_theme_item(Theme.DATA_TYPE_CONSTANT, &"row_height", &"TabMeasureView", int(value))
		return true
	if property == &"fret_note_height":
		shared_theme.set_theme_item(Theme.DATA_TYPE_CONSTANT, &"note_height", &"TabMeasureView", int(value))
		return true
	return false

func _save_shared_theme() -> void:
	if shared_theme == null or not shared_theme.resource_path.begins_with("res://") or "::" in shared_theme.resource_path:
		push_warning("Select an external shared Theme before saving.")
		return
	var result := ResourceSaver.save(shared_theme, shared_theme.resource_path)
	if result != OK:
		push_error("Could not save the shared tab theme: %s" % error_string(result))
	else:
		print("Saved shared theme: %s" % shared_theme.resource_path)

func _replace_style(previous: StyleBox, value: StyleBox) -> StyleBox:
	if previous != null and previous.changed.is_connected(_on_resource_changed): previous.changed.disconnect(_on_resource_changed)
	if value != null and not value.changed.is_connected(_on_resource_changed): value.changed.connect(_on_resource_changed)
	queue_redraw()
	return value

func _notification(what: int) -> void:
	if what == NOTIFICATION_THEME_CHANGED or what == NOTIFICATION_RESIZED: queue_redraw()

func _on_resource_changed() -> void:
	update_minimum_size()
	queue_redraw()

func _get_minimum_size() -> Vector2:
	return Vector2(560, 220 + clampf(_theme_constant(&"row_height", 34.0), 8.0, 200.0) * 5.0)

func _theme_constant(name: StringName, fallback: float) -> float:
	if shared_theme != null and shared_theme.has_theme_item(Theme.DATA_TYPE_CONSTANT, name, &"TabMeasureView"):
		return float(shared_theme.get_theme_item(Theme.DATA_TYPE_CONSTANT, name, &"TabMeasureView"))
	return fallback

func _theme_color(name: StringName, fallback: Color = Color.WHITE) -> Color:
	if shared_theme != null and shared_theme.has_theme_item(Theme.DATA_TYPE_COLOR, name, &"TabMeasureView"):
		return shared_theme.get_theme_item(Theme.DATA_TYPE_COLOR, name, &"TabMeasureView")
	return fallback

func _draw() -> void:
	var row_height := clampf(_theme_constant(&"row_height", 34.0), 8.0, 200.0)
	var note_height := minf(clampf(_theme_constant(&"note_height", 23.0), 8.0, 100.0), maxf(1.0, row_height - 8.0))
	var bg := _theme_color(&"background", Color(0.0534, 0.0556, 0.06))
	var line := _theme_color(&"string_line", Color(0.6, 0.6, 0.6))
	var text_color := _theme_color(&"text", Color.WHITE)
	var line_thickness := maxf(1.0, _theme_constant(&"line_thickness", 1.0))
	var font := get_theme_default_font()
	var gap := 24.0
	var left_margin := 44.0
	var top := 47.0
	var measure_width := maxf(220.0, (size.x - gap - left_margin - 24.0) * 0.5)
	var row_bottom := top + row_height * 5.0
	draw_string(font, Vector2(12, 19), "TabMeasureView · shared production theme", HORIZONTAL_ALIGNMENT_LEFT, -1, 14, text_color)
	_draw_measure(Rect2(left_margin, 29, measure_width, size.y - 45), "Original measure", false, row_height, note_height, line, line_thickness, font, text_color)
	_draw_measure(Rect2(left_margin + measure_width + gap, 29, measure_width, size.y - 45), "Linked repeat · M1", true, row_height, note_height, line, line_thickness, font, text_color)
	var note_info := "Fret row %.0f px · note/fret block %.0f px (capped at row − 8 px)" % [row_height, note_height]
	draw_string(font, Vector2(12, size.y - 24), note_info, HORIZONTAL_ALIGNMENT_LEFT, -1, 11, _theme_color(&"hint", text_color))
	draw_string(font, Vector2(12, size.y - 7), "Inspector height edits change the shared Theme. Use Save Shared Theme to keep them.", HORIZONTAL_ALIGNMENT_LEFT, -1, 10, _theme_color(&"hint", text_color))

func _draw_measure(rect: Rect2, title: String, linked: bool, row_height: float, note_height: float, line: Color, line_thickness: float, font: Font, text_color: Color) -> void:
	var panel := rect
	if tab_panel != null: draw_style_box(tab_panel, panel)
	else: draw_rect(panel, _theme_color(&"background", Color(0.0534, 0.0556, 0.06)))
	var left := panel.position.x + 30.0
	var right := panel.end.x - 10.0
	var top := panel.position.y + 39.0
	var bottom := top + row_height * 5.0
	# Production draws linked repeat tint over the panel before the fret grid and notes.
	if linked:
		draw_rect(panel, _theme_color(&"repeat_overlay_tint", Color(0.31, 0.31, 0.31, 0.25)))
	draw_string(font, panel.position + Vector2(8, 17), title, HORIZONTAL_ALIGNMENT_LEFT, -1, 12, text_color)
	for i in range(6):
		var y := top + i * row_height
		draw_line(Vector2(left, y), Vector2(right, y), line, line_thickness)
		draw_string(font, Vector2(panel.position.x + 8, y + 4), str(6 - i), HORIZONTAL_ALIGNMENT_LEFT, -1, 11, _theme_color(&"string_text", text_color))
	for beat in range(5):
		var x := lerpf(left, right, float(beat) / 4.0)
		var edge := beat == 0 or beat == 4
		var grid_color := _theme_color(&"bar_line", line) if edge else _theme_color(&"grid", line)
		draw_line(Vector2(x, top - 5), Vector2(x, bottom + 4), grid_color, line_thickness * (1.5 if edge else 1.0))
		if beat < 4: draw_string(font, Vector2(x + 4, top - 8), str(beat + 1), HORIZONTAL_ALIGNMENT_LEFT, -1, 10, _theme_color(&"beat_text", text_color))
	var note_y := top + row_height * 0.6
	_draw_note(Rect2(left + (right-left)*0.14, note_y, 29, note_height), tab_note, "3", false, font)
	_draw_note(Rect2(left + (right-left)*0.43, note_y + row_height, 29, note_height), tab_note_selected, "7", true, font)
	_draw_note(Rect2(left + (right-left)*0.70, note_y + row_height*2.0, 29, note_height), tab_note_error, "5", false, font)
	if linked:
		draw_rect(panel, _theme_color(&"repeat_overlay_outline", line), false, 3.0)
		draw_string(font, Vector2(right - 78, top - 9), "↻ REPEAT", HORIZONTAL_ALIGNMENT_LEFT, 76, 10, _theme_color(&"repeat_overlay_label_color", text_color))

func _draw_note(rect: Rect2, style: StyleBox, label: String, selected: bool, font: Font) -> void:
	if style != null: draw_style_box(style, rect)
	else: draw_rect(rect, _theme_color(&"note", Color(0.12, 0.12, 0.12)))
	var color := _theme_color(&"selected_text", Color.BLACK) if selected else _theme_color(&"text", Color.WHITE)
	draw_string(font, rect.position + Vector2(9, rect.size.y - 5), label, HORIZONTAL_ALIGNMENT_LEFT, -1, 11, color)
