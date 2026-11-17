extends Node

## Utilities for the standalone palette editor; the app does not load this service.
const PROJECT_PATH := "res://Interface/Themes/default_palette.tres"
const USER_PATH := "user://theme_palette.tres"
const THEME_PATH := "res://Interface/Themes/main.tres"
const PaletteScript = preload("res://Interface/ThemeEditor/theme_palette.gd")
const CoreScript = preload("res://Interface/ThemeEditor/theme_palette_core.gd")

static func load_effective_palette():
	var core = CoreScript.new()
	var project = core.load_palette(PROJECT_PATH) if FileAccess.file_exists(PROJECT_PATH) else null
	var user = core.load_palette(USER_PATH) if FileAccess.file_exists(USER_PATH) else null
	if project == null:
		return user
	if user == null:
		return project
	return merge_palettes(project, user)

static func merge_palettes(project, user):
	var result = project.copy()
	for group in user.active_groups:
		match String(group):
			"surface":
				result.background_color = user.background_color
				result.panel_color = user.panel_color
			"text": result.text_color = user.text_color
			"muted_text": result.muted_text_color = user.muted_text_color
			"highlight": result.highlight_color = user.highlight_color
			"destructive": result.destructive_button_color = user.destructive_button_color
			"mute": result.mute_button_color = user.mute_button_color
			"solo": result.solo_button_color = user.solo_button_color
			"intensity": result.intensity_color = user.intensity_color
			"button_difference": result.button_highlight_difference = user.button_highlight_difference
			"tab_background": result.tab_background_color = user.tab_background_color
			"tab_grid": result.tab_grid_color = user.tab_grid_color
			"tab_string": result.tab_string_color = user.tab_string_color
			"tab_text": result.tab_text_color = user.tab_text_color
			"beat_background": result.beat_background_color = user.beat_background_color
			"tab_line_thickness": result.tab_line_thickness = user.tab_line_thickness
		if not result.active_groups.has(group):
			result.active_groups.append(group)
	return result

static func apply_to_shared_theme(shared: Theme, palette) -> bool:
	var generated: Theme = CoreScript.new().create_preview_theme(shared, palette)
	if generated == null:
		return false
	# Preserve the original Theme identity, which existing scenes also reference.
	for type_name in generated.get_type_list():
		for key in generated.get_color_list(type_name):
			shared.set_color(key, type_name, generated.get_color(key, type_name))
		for key in generated.get_constant_list(type_name):
			shared.set_constant(key, type_name, generated.get_constant(key, type_name))
		for key in generated.get_stylebox_list(type_name):
			shared.set_stylebox(key, type_name, generated.get_stylebox(key, type_name))
	return true
