@tool
extends Control

## Edit the shared Theme resource through the Inspector; no automatic writes.
@export var shared_theme: Theme

func _notification(what: int) -> void:
	if what == NOTIFICATION_THEME_CHANGED or what == NOTIFICATION_RESIZED:
		queue_redraw()

func _draw() -> void:
	var font := get_theme_default_font()
	var indicator := get_theme_color("drop_target_highlight", "AppIndicators")
	var muted := get_theme_color("repeated_beat_modulate", "AppIndicators")
	draw_line(Vector2(8, 8), Vector2(8, 38), indicator, 3.0)
	draw_string(font, Vector2(18, 28), "Arrangement drop indicator", HORIZONTAL_ALIGNMENT_LEFT, -1, 14, get_theme_color("font_color", "Label"))
	draw_rect(Rect2(260, 8, 36, 30), muted)
	draw_string(font, Vector2(304, 28), "Repeated beat tint", HORIZONTAL_ALIGNMENT_LEFT, -1, 14, get_theme_color("font_color", "Label"))
	draw_string(font, Vector2(8, 65), "↻ M2 · linked repeat", HORIZONTAL_ALIGNMENT_LEFT, -1, 14, get_theme_color("linked_repeat_color", "AppIndicators"))
