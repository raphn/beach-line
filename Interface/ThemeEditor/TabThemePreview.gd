@tool
extends Control

# The production tablature control is C# and does not execute in the editor.
# Render its shared styles and tokens here so palette edits are visible without running the app.
@export var tab_panel: StyleBoxFlat:
	set(value):
		if tab_panel == value:
			return
		if tab_panel != null and tab_panel.changed.is_connected(_on_tab_panel_changed):
			tab_panel.changed.disconnect(_on_tab_panel_changed)
		tab_panel = value
		if tab_panel != null:
			tab_panel.changed.connect(_on_tab_panel_changed)
		queue_redraw()

@export var shared_theme: Theme:
	set(value):
		if shared_theme == value:
			return
		if shared_theme != null and shared_theme.changed.is_connected(_on_shared_theme_changed):
			shared_theme.changed.disconnect(_on_shared_theme_changed)
		shared_theme = value
		if shared_theme != null:
			shared_theme.changed.connect(_on_shared_theme_changed)
		queue_redraw()

func _notification(what: int) -> void:
	if what == NOTIFICATION_THEME_CHANGED or what == NOTIFICATION_RESIZED:
		queue_redraw()

func _on_tab_panel_changed() -> void:
	queue_redraw()

func _on_shared_theme_changed() -> void:
	queue_redraw()

func _draw() -> void:
	var kind := &"TabMeasureView"
	var font := get_theme_default_font()
	draw_style_box(get_theme_stylebox("panel", kind), Rect2(Vector2.ZERO, size))
	var repeat_overlay := Rect2(Vector2(2, 2), Vector2(maxf(0.0, size.x - 4.0), maxf(0.0, size.y - 4.0)))
	if tab_panel != null:
		draw_rect(repeat_overlay, get_theme_color("repeat_overlay_tint", kind))
		draw_rect(repeat_overlay, get_theme_color("repeat_overlay_outline", kind), false, 3.0)
	var thickness: float = float(get_theme_constant("line_thickness", kind)) if has_theme_constant("line_thickness", kind) else 1.0
	var left := 48.0
	var right := size.x - 14.0
	for i in range(6):
		var y := 55.0 + i * 23.0
		draw_line(Vector2(left, y), Vector2(right, y), get_theme_color("string_line", kind), thickness)
		draw_string(font, Vector2(12, y + 4), str(6 - i), HORIZONTAL_ALIGNMENT_LEFT, -1, 12, get_theme_color("string_text", kind))
	for i in range(5):
		var x := left + (right - left) * i / 4.0
		draw_line(Vector2(x, 32), Vector2(x, 175), get_theme_color("bar_line" if i == 0 or i == 4 else "grid", kind), thickness * (2.0 if i == 0 or i == 4 else 1.0))
		if i < 4:
			draw_string(font, Vector2(x + 4, 27), str(i + 1), HORIZONTAL_ALIGNMENT_LEFT, -1, 12, get_theme_color("beat_text", kind))
	for item in [[90.0, 69.0, "3", "note"], [165.0, 115.0, "7", "note_selected"], [240.0, 92.0, "5", "note_error"]]:
		var box := Rect2(item[0], item[1], 34, 23)
		draw_style_box(get_theme_stylebox(item[3], kind), box)
		var text_color := get_theme_color("selected_text" if item[3] == "note_selected" else "text", kind)
		draw_string(font, box.position + Vector2(11, 17), item[2], HORIZONTAL_ALIGNMENT_LEFT, -1, 12, text_color)
	if tab_panel != null:
		var start_x := left + 12.0
		var playback_x := left + (right - left) * 0.68
		draw_line(Vector2(start_x, 32), Vector2(start_x, 175), get_theme_color("playback_start_cursor_color", kind), 2.0, true)
		draw_line(Vector2(start_x - 4, 31), Vector2(start_x + 4, 31), get_theme_color("playback_start_cursor_color", kind), 3.0, true)
		draw_line(Vector2(playback_x, 32), Vector2(playback_x, 175), get_theme_color("playback_cursor_color", kind), 3.0, true)
		draw_line(Vector2(playback_x - 5, 31), Vector2(playback_x + 5, 31), get_theme_color("playback_cursor_color", kind), 4.0, true)
		draw_string(font, Vector2(right - 162, 17), "↻ REPEAT · M2", HORIZONTAL_ALIGNMENT_LEFT, 154, 12, get_theme_color("repeat_overlay_label_color", kind))
	draw_string(font, Vector2(left, size.y - 12), "TabMeasureView · repeat / start / playback", HORIZONTAL_ALIGNMENT_LEFT, -1, 11, get_theme_color("hint", kind))
