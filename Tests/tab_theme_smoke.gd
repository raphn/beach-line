extends SceneTree

# Exercises Godot's Control theme lookup, not just the values stored in the Theme.
func _initialize() -> void:
	call_deferred("_check_theme")

func _check_theme() -> void:
	var theme := load("res://Interface/Themes/main.tres") as Theme
	var view := Control.new()
	root.add_child(view)
	view.theme = theme
	view.theme_type_variation = &"TabMeasureView"
	var geometry := ["row_height", "percussion_row_height", "note_height",
		"minimum_measure_width", "minimum_cell_width"]
	for key in geometry:
		if not theme.has_constant(key, "TabMeasureView") or theme.get_constant(key, "TabMeasureView") <= 0:
			push_error("Missing or unexpected tab geometry constant: " + key)
			view.free()
			quit(1)
			return
	var tokens := ["background", "bar_line", "beat_text", "caret", "continuation",
		"entry_background", "entry_border", "error", "grid", "hint", "note",
		"note_border", "note_selected", "note_selected_border", "selected_text",
		"string_line", "string_text", "subdivision", "text", "timeline"]
	for token in tokens:
		if not theme.has_color(token, "TabMeasureView") or not view.has_theme_color(token, "TabMeasureView"):
			push_error("Missing tab theme color: " + token)
			view.free()
			quit(1)
			return
		if view.get_theme_color(token, "TabMeasureView") != theme.get_color(token, "TabMeasureView"):
			push_error("Tab control could not resolve its theme color: " + token)
			view.free()
			quit(1)
			return
	view.free()
	print("Tab theme colors resolve correctly.")
	quit(0)
