extends SceneTree

## Regression coverage for runtime palette persistence, isolation and preview UI.
## Run only from a disposable copy of the project; temporary palette files live in user://.
const PaletteScript = preload("res://Interface/ThemeEditor/theme_palette.gd")
const CoreScript = preload("res://Interface/ThemeEditor/theme_palette_core.gd")
const Service = preload("res://Interface/ThemeEditor/theme_palette_service.gd")
const MAIN_THEME_PATH := "res://Interface/Themes/main.tres"
const BACKGROUND_STYLE_PATH := "res://Interface/Themes/Panels/background.tres"
const TEMP_DIR := "user://theme_palette_runtime_smoke"

var _failures: Array[String] = []

func _initialize() -> void:
	call_deferred("_run")

func _run() -> void:
	var theme_bytes_before := FileAccess.get_file_as_bytes(MAIN_THEME_PATH)
	var background_bytes_before := FileAccess.get_file_as_bytes(BACKGROUND_STYLE_PATH)
	var source := load(MAIN_THEME_PATH) as Theme
	_check(source != null, "canonical application Theme loads")
	if source == null:
		_finish()
		return

	var core = CoreScript.new()
	var source_background := source.get_stylebox("panel", "AppBackgroundPanel") as StyleBoxTexture
	_check(source_background != null, "canonical background StyleBoxTexture exists")
	var source_gradient: Gradient
	var source_gradient_colors: PackedColorArray
	if source_background != null:
		var source_texture := source_background.texture as GradientTexture2D
		_check(source_texture != null and source_texture.gradient != null, "background style has a nested Gradient")
		if source_texture != null and source_texture.gradient != null:
			source_gradient = source_texture.gradient
			source_gradient_colors = source_gradient.colors.duplicate()

	# A palette with no active edits is the startup default: generation leaves the
	# authored colors alone. Service must treat a missing palette as no application.
	var empty_palette = PaletteScript.new()
	var default_preview: Theme = core.create_preview_theme(source, empty_palette)
	_check(default_preview != null, "default preview Theme is created")
	if default_preview != null:
		_check(default_preview.get_color("background", "TabMeasureView") == source.get_color("background", "TabMeasureView"),
			"an empty palette preserves the authored notation background")
		var default_background := default_preview.get_stylebox("panel", "AppBackgroundPanel") as StyleBoxTexture
		if source_background != null and default_background != null:
			var original_texture := source_background.texture as GradientTexture2D
			var copied_texture := default_background.texture as GradientTexture2D
			_check(original_texture != null and copied_texture != null and original_texture.get_instance_id() != copied_texture.get_instance_id(),
				"preview background texture is detached from the source")
			if original_texture != null and copied_texture != null:
				_check(original_texture.gradient != null and copied_texture.gradient != null and original_texture.gradient.get_instance_id() != copied_texture.gradient.get_instance_id(),
					"nested preview Gradient is detached from the source")
	var untouched_theme := source.duplicate(false) as Theme
	var untouched_background := untouched_theme.get_stylebox("panel", "AppBackgroundPanel")
	_check(not Service.apply_to_shared_theme(untouched_theme, null), "missing palette is a no-op at startup")
	_check(untouched_theme.get_stylebox("panel", "AppBackgroundPanel") == untouched_background,
		"no-palette startup leaves the existing shared style reference in place")

	# Exercise independent persistence and user-over-project group precedence with
	# temporary files in this test's user:// namespace, never the shipped palette path.
	var absolute_temp_dir := ProjectSettings.globalize_path(TEMP_DIR)
	DirAccess.make_dir_recursive_absolute(absolute_temp_dir)
	var project_path := TEMP_DIR.path_join("project_default.tres")
	var user_path := TEMP_DIR.path_join("user_preference.tres")
	var project_palette = PaletteScript.new()
	project_palette.background_color = Color("192b3d")
	project_palette.panel_color = Color("26394c")
	project_palette.text_color = Color("e8edf9")
	project_palette.highlight_color = Color("cc4455")
	project_palette.tab_line_thickness = 2
	project_palette.active_groups.assign([&"surface", &"text", &"highlight", &"tab_line_thickness"])
	var user_palette = PaletteScript.new()
	user_palette.background_color = Color("aabbcc")
	user_palette.text_color = Color("00ff00")
	user_palette.highlight_color = Color("2244ee")
	user_palette.tab_line_thickness = 4
	user_palette.active_groups.assign([&"highlight", &"tab_line_thickness"])
	_check(core.save_palette(project_palette, project_path) == OK, "temporary project default saves")
	_check(core.save_palette(user_palette, user_path) == OK, "temporary user preference saves independently")
	var loaded_project = core.load_palette(project_path)
	var loaded_user = core.load_palette(user_path)
	_check(loaded_project != null and loaded_user != null, "both temporary palettes load")
	if loaded_project != null and loaded_user != null:
		var effective = Service.merge_palettes(loaded_project, loaded_user)
		_check(effective.background_color == project_palette.background_color, "user palette leaves unedited surface group at project default")
		_check(effective.text_color == project_palette.text_color, "user palette leaves unedited text group at project default")
		_check(effective.highlight_color == user_palette.highlight_color, "user palette overrides the same project group")
		_check(effective.tab_line_thickness == 4, "user notation thickness overrides the project default")
		_check(effective.active_groups.has(&"surface") and effective.active_groups.has(&"highlight"), "effective palette retains project and user active groups")

	# Instantiate the actual editor screen and send the same ColorPicker signal the
	# controller connects. The deferred preview update must remain memory-only.
	var editor_scene := load("res://Interface/ThemeEditor/RuntimeThemeEditor.tscn") as PackedScene
	_check(editor_scene != null, "runtime palette editor scene loads")
	if editor_scene != null:
		var editor := editor_scene.instantiate()
		root.add_child(editor)
		await process_frame
		var picker := editor.find_child("Background", true, false) as ColorPickerButton
		var preview := editor.get_node_or_null("Scroll/Content/PreviewFrame/PreviewInstance") as Control
		_check(picker != null and preview != null, "runtime editor exposes its background picker and live preview")
		if picker != null and preview != null:
			var chosen := Color("2167a8")
			picker.color = chosen
			picker.color_changed.emit(chosen)
			await process_frame
			await process_frame
			var preview_theme := editor.get("preview_theme") as Theme
			_check(preview_theme != null, "editor owns an isolated preview Theme")
			if preview_theme != null:
				_check(preview_theme.get_color("background", "TabMeasureView") == chosen,
					"ColorPicker change updates the preview Theme")
				_check(preview.theme == preview_theme, "visible preview Control uses the updated Theme")
				var preview_background := preview_theme.get_stylebox("panel", "AppBackgroundPanel") as StyleBoxTexture
				if source_gradient != null and preview_background != null:
					var preview_texture := preview_background.texture as GradientTexture2D
					_check(preview_texture != null and preview_texture.gradient != null and preview_texture.gradient.get_instance_id() != source_gradient.get_instance_id(),
						"UI preview keeps nested gradient independent while recoloring")
				_check(source_gradient == null or source_gradient.colors == source_gradient_colors,
					"preview recoloring leaves the canonical source Gradient unchanged")
		# Each notation override must reach the themed production controls, survive
		# broader palette edits, and remain isolated from the authored resources.
		editor._on_color_changed(Color("152a44"), "tab_background_color", &"tab_background")
		(editor.find_child("TabGrid", true, false) as ColorPickerButton).color_changed.emit(Color("55aacc"))
		editor._on_color_changed(Color("bbaaff"), "tab_string_color", &"tab_string")
		editor._on_color_changed(Color("f2e4c1"), "tab_text_color", &"tab_text")
		editor._on_color_changed(Color("2d465c"), "beat_background_color", &"beat_background")
		(editor.find_child("TabLineThickness", true, false) as HSlider).value_changed.emit(3.0)
		await process_frame
		await process_frame
		var notation := editor.get("preview_theme") as Theme
		var tab_panel := notation.get_stylebox("panel", "TabMeasureView") as StyleBoxFlat
		var beat_panel := notation.get_stylebox("panel", "BeatEditorPanel") as StyleBoxFlat
		_check(tab_panel.bg_color.is_equal_approx(Color("152a44")), "tab background changes the actual tab panel")
		_check(beat_panel.bg_color.is_equal_approx(Color("2d465c")), "beat background changes the actual beat panel")
		_check(notation.get_color("grid", "TabMeasureView").is_equal_approx(Color("55aacc")), "tab grid color is applied")
		_check(notation.get_color("string_line", "TabMeasureView").is_equal_approx(Color("bbaaff")), "tab string color is applied")
		_check(notation.get_color("text", "TabMeasureView").is_equal_approx(Color("f2e4c1")), "tab text color is applied")
		_check(notation.get_constant("line_thickness", "TabMeasureView") == 3, "tab line thickness is applied")
		editor._on_color_changed(Color("223344"), "panel_color", &"surface")
		await process_frame
		await process_frame
		_check(tab_panel.bg_color.is_equal_approx(Color("152a44")) and beat_panel.bg_color.is_equal_approx(Color("2d465c")),
			"notation backgrounds survive later general palette changes")
		_check(notation.get_color("grid", "TabMeasureView").is_equal_approx(Color("55aacc")),
			"notation grid survives later general palette changes")
		var notation_path := TEMP_DIR.path_join("notation.tres")
		_check(core.save_palette(editor.get("palette"), notation_path) == OK, "notation palette saves")
		var restored = core.load_palette(notation_path)
		_check(restored != null and restored.tab_line_thickness == 3 and restored.active_groups.has(&"beat_background"),
			"notation palette settings survive reload")
		if restored != null:
			var reapplied := source.duplicate(false) as Theme
			_check(Service.apply_to_shared_theme(reapplied, restored), "saved notation palette applies on startup")
			_check(reapplied.get_constant("line_thickness", "TabMeasureView") == 3, "startup applies notation thickness")
		_cleanup_temp_file(notation_path)
		editor.queue_free()
		await process_frame

	_check(FileAccess.get_file_as_bytes(MAIN_THEME_PATH) == theme_bytes_before, "runtime preview does not write main.tres")
	_check(FileAccess.get_file_as_bytes(BACKGROUND_STYLE_PATH) == background_bytes_before, "runtime preview does not write the nested-gradient StyleBox")
	_cleanup_temp_file(project_path)
	_cleanup_temp_file(user_path)
	DirAccess.remove_absolute(absolute_temp_dir)
	_finish()

func _check(condition: bool, message: String) -> void:
	if condition:
		return
	_failures.append(message)
	push_error("Theme palette runtime smoke: " + message)

func _cleanup_temp_file(path: String) -> void:
	var absolute_path := ProjectSettings.globalize_path(path)
	if FileAccess.file_exists(path):
		DirAccess.remove_absolute(absolute_path)

func _finish() -> void:
	if _failures.is_empty():
		print("Theme palette runtime smoke passed.")
		quit(0)
	else:
		push_error("Theme palette runtime smoke failed: %d check(s)." % _failures.size())
		quit(1)
