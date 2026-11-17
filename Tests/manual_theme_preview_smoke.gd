extends SceneTree

# Run headlessly after importing the project. The temporary resource is in user://.
func _initialize() -> void:
	call_deferred("_check")

func _check() -> void:
	var theme := load("res://Interface/Themes/main.tres") as Theme
	var packed := load("res://Interface/ThemeEditor/ThemeEditor.tscn") as PackedScene
	if theme == null or packed == null:
		push_error("Shared theme or preview scene failed to load")
		quit(1)
		return
	var scene := packed.instantiate()
	root.add_child(scene)
	await process_frame
	var tab := scene.find_child("Sample4_0", true, false)
	var beat := scene.find_child("Sample17_6", true, false)
	if tab == null or beat == null or tab.get("tab_panel") != theme.get_stylebox("panel", "TabMeasureView") or beat.get("beat_style") != theme.get_stylebox("panel", "BeatEditorPanel") or tab.get("shared_theme") != theme:
		push_error("Preview examples do not expose the application's shared theme resources")
		quit(1)
		return
	if not theme.has_constant("line_thickness", "TabMeasureView") or not theme.has_color("drop_target_highlight", "AppIndicators") or not theme.has_color("repeated_beat_modulate", "AppIndicators"):
		push_error("Shared theme is missing custom drawing settings")
		quit(1)
		return
	for key in ["repeat_overlay_tint", "repeat_overlay_outline", "repeat_overlay_label_color", "playback_start_cursor_color", "playback_cursor_color"]:
		if not theme.has_color(key, "TabMeasureView"):
			push_error("Missing tab drawing color: " + key)
			quit(1)
			return
	var panel := theme.get_stylebox("panel", "TabMeasureView")
	if panel.get_script() != null:
		push_error("Project-wide theme still loads a scripted tab style during startup")
		quit(1)
		return
	var copy := theme.duplicate(false) as Theme
	copy.set_color("repeat_overlay_outline", "TabMeasureView", Color(0.2, 0.8, 0.4))
	var path := "user://manual_theme_preview_smoke.tres"
	if ResourceSaver.save(copy, path) != OK:
		push_error("Could not save temporary shared theme copy")
		quit(1)
		return
	var restored := ResourceLoader.load(path, "", ResourceLoader.CACHE_MODE_IGNORE) as Theme
	if restored == null or not restored.get_color("repeat_overlay_outline", "TabMeasureView").is_equal_approx(Color(0.2, 0.8, 0.4)):
		push_error("Custom theme color did not survive a resource save")
		quit(1)
		return
	DirAccess.remove_absolute(ProjectSettings.globalize_path(path))
	scene.free()
	print("Manual theme preview shares app resources and persists custom theme colors.")
	quit(0)
