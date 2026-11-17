class_name ThemePaletteCore
extends RefCounted

## Applies compact palette data to an isolated Theme copy. Never saves the source theme.
const PALETTE_SCRIPT := preload("res://Interface/ThemeEditor/theme_palette.gd")
var _style_paths: Dictionary = {}

func load_palette(path: String) -> Resource:
	var loaded := ResourceLoader.load(path, "", ResourceLoader.CACHE_MODE_IGNORE)
	if loaded == null or not loaded is ThemePalette:
		return null
	return (loaded as ThemePalette).copy()

func save_palette(palette: Resource, path: String) -> Error:
	if palette == null or not palette is ThemePalette or path.is_empty():
		return ERR_INVALID_PARAMETER
	return ResourceSaver.save((palette as ThemePalette).copy(), path)

func create_preview_theme(source: Theme, palette: Resource) -> Theme:
	if source == null or palette == null or not palette is ThemePalette:
		return null
	var theme := source.duplicate(false) as Theme
	if theme == null:
		return null
	var resource_copies: Dictionary = {}
	for type_name in source.get_type_list():
		for style_name in source.get_stylebox_list(type_name):
			var original := source.get_stylebox(style_name, type_name)
			if original == null:
				continue
			var instance_id := original.get_instance_id()
			if not resource_copies.has(instance_id):
				var cloned := original.duplicate(true) as StyleBox
				if cloned == null:
					continue
				if cloned is StyleBoxTexture and original is StyleBoxTexture:
					var source_texture := (original as StyleBoxTexture).texture
					if source_texture != null:
						var texture_copy := source_texture.duplicate(true) as Texture2D
						if texture_copy is GradientTexture2D and source_texture is GradientTexture2D:
							var source_gradient := (source_texture as GradientTexture2D).gradient
							if source_gradient != null:
								(texture_copy as GradientTexture2D).gradient = source_gradient.duplicate(true) as Gradient
							(cloned as StyleBoxTexture).texture = texture_copy
				resource_copies[instance_id] = cloned
				var original_path := String(original.get_meta("theme_palette_source_path", original.resource_path)).to_lower()
				cloned.set_meta("theme_palette_source_path", original_path)
				_style_paths[cloned.get_instance_id()] = original_path
			theme.set_stylebox(style_name, type_name, resource_copies[instance_id])
	var p := palette as ThemePalette
	if not p.active_groups.is_empty():
		apply_to_preview(theme, p, p.active_groups)
	return theme

## Recolors an already detached preview theme without cloning or assigning resources again.
func apply_to_preview(theme: Theme, palette: ThemePalette, groups: Array[StringName]) -> void:
	if theme == null or palette == null or groups.is_empty():
		return
	var applied_palette := palette.copy()
	var effective_groups: Array[StringName] = groups.duplicate()
	# A broad palette adjustment can touch the same tokens and styleboxes as
	# notation-specific overrides. Always apply active specific overrides last.
	for group in [&"tab_background", &"tab_grid", &"tab_string", &"tab_text", &"beat_background", &"tab_line_thickness"]:
		if palette.active_groups.has(group) and not effective_groups.has(group):
			effective_groups.append(group)
	applied_palette.active_groups = effective_groups
	_apply_theme_colors(theme, applied_palette)
	_apply_theme_constants(theme, applied_palette)
	var applied_ids: Dictionary = {}
	for type_name in theme.get_type_list():
		for style_name in theme.get_stylebox_list(type_name):
			var style := theme.get_stylebox(style_name, type_name)
			if style == null:
				continue
			var instance_id := style.get_instance_id()
			if applied_ids.has(instance_id):
				continue
			applied_ids[instance_id] = true
			var path: String = _style_paths.get(instance_id, "")
			if _style_path_is_affected(path, effective_groups):
				_apply_stylebox(style, path, applied_palette)

func _style_path_is_affected(path: String, groups: Array[StringName]) -> bool:
	if path.ends_with("/panels/tab_panel.tres") and groups.has(&"tab_background"):
		return true
	if path.ends_with("/panels/beat_editor.tres") and groups.has(&"beat_background"):
		return true
	if groups.has(&"surface") or groups.has(&"highlight"):
		return true
	if path.contains("/destructive/") or path.contains("/beatremove/"):
		return groups.has(&"destructive") or groups.has(&"button_difference")
	if path.contains("/mute/"):
		return groups.has(&"mute") or groups.has(&"button_difference")
	if path.contains("/solo/"):
		return groups.has(&"solo") or groups.has(&"button_difference")
	if path.contains("/volume/"):
		return groups.has(&"intensity") or groups.has(&"button_difference")
	if path.contains("/inputs/"):
		return groups.has(&"muted_text")
	if path.contains("/buttons/") or path.contains("/instruments/"):
		return groups.has(&"button_difference")
	return false

func _apply_theme_constants(theme: Theme, palette: ThemePalette) -> void:
	if palette.active_groups.has(&"tab_line_thickness"):
		theme.set_constant("line_thickness", &"TabMeasureView", clampi(palette.tab_line_thickness, 1, 4))

func _has_explicit_tab_group(type_key: String, key: String, groups: Array[StringName]) -> bool:
	if type_key != "tabmeasureview":
		return false
	var explicit_group: StringName = &""
	if key == "background": explicit_group = &"tab_background"
	elif key in ["grid", "bar_line", "timeline", "subdivision"]: explicit_group = &"tab_grid"
	elif key == "string_line": explicit_group = &"tab_string"
	elif key in ["text", "string_text", "beat_text", "hint"]: explicit_group = &"tab_text"
	return not explicit_group.is_empty() and groups.has(explicit_group)

func _theme_color_group(type_key: String, key: String) -> StringName:
	if (key.contains("font") or key.contains("icon")) and key.contains("pressed"):
		if type_key == "appmutebutton": return &"mute"
		if type_key == "appsolobutton": return &"solo"
		if type_key in ["appdestructivebutton", "beatremovebutton"]: return &"destructive"
		if type_key == "beatintensitybutton": return &"intensity"
		if type_key.ends_with("button") or type_key in ["fret", "handfret", "markfret", "blackkey", "whitekey"]: return &"highlight"
	if type_key == "tabmeasureview":
		if key == "background" or key in ["entry_background", "note", "subdivision"]:
			return &"surface"
		if key == "error":
			return &"destructive"
		if key == "caret":
			return &"solo"
		if key in ["entry_border", "note_selected", "note_selected_border", "continuation"]:
			return &"highlight"
		if key in ["hint", "beat_text", "grid", "string_line", "bar_line", "timeline"]:
			return &"muted_text"
		if key == "selected_text":
			return &"highlight"
		return &"text"
	if type_key == "appstatus":
		if key.contains("muted"):
			return &"muted_text"
		if key.contains("error") or key.contains("danger"):
			return &"destructive"
		if key.contains("warning"):
			return &"mute"
		return &"solo"
	if type_key == "appsuccesslabel":
		return &"solo"
	if key.contains("caret"):
		return &"solo"
	if key.contains("selection"):
		return &"highlight"
	if key.contains("error"):
		return &"destructive"
	if key.contains("font") or key.contains("text") or key.contains("title_color"):
		if key.contains("disabled") or key.contains("placeholder") or key.contains("uneditable"):
			return &"muted_text"
		if key.contains("pressed") or key.contains("selected"):
			return &"surface"
		if type_key == "appmutedlabel":
			return &"muted_text"
		return &"text"
	if key.contains("disabled"):
		return &"muted_text"
	return &"surface"

func _apply_theme_colors(theme: Theme, palette: ThemePalette) -> void:
	for type_name in theme.get_type_list():
		for color_name in theme.get_color_list(type_name):
			var key := String(color_name).to_lower()
			var type_key := String(type_name).to_lower()
			if not palette.active_groups.has(_theme_color_group(type_key, key)) and not _has_explicit_tab_group(type_key, key, palette.active_groups):
				continue
			var color := theme.get_color(color_name, type_name)
			var replacement := _mapped_theme_color(type_key, key, color, palette)
			replacement = _keep_alpha(replacement, color)
			if replacement != color:
				theme.set_color(color_name, type_name, replacement)

func _mapped_theme_color(type_key: String, key: String, original: Color, palette: ThemePalette) -> Color:
	if (key.contains("font") or key.contains("icon")) and key.contains("pressed"):
		if type_key == "appmutebutton": return _readable_text(palette.mute_button_color)
		if type_key == "appsolobutton": return _readable_text(palette.solo_button_color)
		if type_key in ["appdestructivebutton", "beatremovebutton"]: return _readable_text(palette.destructive_button_color)
		if type_key == "beatintensitybutton": return _readable_text(palette.intensity_color)
		if type_key.ends_with("button") or type_key in ["fret", "handfret", "markfret", "blackkey", "whitekey"]: return _readable_text(palette.highlight_color)
	if type_key in ["blackkey", "whitekey"] and (key.contains("font") or key.contains("icon")):
		if key.contains("disabled"):
			return palette.muted_text_color
		if key.contains("pressed"):
			return _ink_color(palette)
		return _ink_color(palette) if type_key == "whitekey" else palette.text_color
	if type_key == "tabmeasureview":
		match key:
			"background": return palette.tab_background_color if palette.active_groups.has(&"tab_background") else palette.background_color
			"entry_background", "note": return _mix(palette.panel_color, palette.background_color, 0.36)
			"subdivision": return palette.tab_grid_color if palette.active_groups.has(&"tab_grid") else _mix(palette.panel_color, palette.background_color, 0.36)
			"entry_border", "note_selected", "note_selected_border": return palette.highlight_color
			"grid", "bar_line", "timeline": return palette.tab_grid_color if palette.active_groups.has(&"tab_grid") else _mix(palette.muted_text_color, palette.background_color, 0.48)
			"string_line": return palette.tab_string_color if palette.active_groups.has(&"tab_string") else _mix(palette.muted_text_color, palette.background_color, 0.48)
			"hint", "beat_text": return palette.tab_text_color if palette.active_groups.has(&"tab_text") else palette.muted_text_color
			"continuation": return _mix(palette.highlight_color, palette.solo_button_color, 0.35)
			"string_text", "text": return palette.tab_text_color if palette.active_groups.has(&"tab_text") else palette.text_color
			"error": return palette.destructive_button_color
			"caret": return palette.solo_button_color
			"selected_text": return _readable_text(palette.highlight_color)
	if type_key == "appstatus":
		if key.contains("muted"):
			return palette.muted_text_color
		if key.contains("warning"):
			return palette.mute_button_color
		if key.contains("error") or key.contains("danger"):
			return palette.destructive_button_color
		return palette.solo_button_color
	if type_key == "appsuccesslabel":
		return palette.solo_button_color
	if key.contains("font") or key == "text" or key.ends_with("_text") or key == "title_color":
		if key.contains("disabled") or key.contains("placeholder") or key.contains("uneditable"):
			return palette.muted_text_color
		if key.contains("selected") and type_key != "tabmeasureview":
			return _ink_color(palette)
		if key.contains("hover_pressed") or key.contains("pressed"):
			return _ink_color(palette)
		return palette.text_color
	if key.contains("caret"):
		return palette.solo_button_color
	if key.contains("selection"):
		return _mix(palette.highlight_color, palette.panel_color, 0.42)
	if type_key == "spinbox" and key.contains("icon"):
		return palette.muted_text_color if key.contains("disabled") else palette.text_color
	if key.contains("disabled"):
		return palette.muted_text_color
	return original

func _apply_stylebox(style: StyleBox, path: String, palette: ThemePalette) -> bool:
	if style is StyleBoxFlat and path.ends_with("/panels/tab_panel.tres") and palette.active_groups.has(&"tab_background"):
		(style as StyleBoxFlat).bg_color = _keep_alpha(palette.tab_background_color, (style as StyleBoxFlat).bg_color)
		return true
	if style is StyleBoxFlat and path.ends_with("/panels/beat_editor.tres") and palette.active_groups.has(&"beat_background"):
		(style as StyleBoxFlat).bg_color = _keep_alpha(palette.beat_background_color, (style as StyleBoxFlat).bg_color)
		return true
	if style is StyleBoxTexture:
		return _apply_texture_style(style as StyleBoxTexture, path, palette)
	if style is StyleBoxLine and path.contains("/inputs/"):
		var line := style as StyleBoxLine
		line.color = _keep_alpha(palette.muted_text_color, line.color)
		return true
	if not style is StyleBoxFlat:
		return false
	var flat := style as StyleBoxFlat
	var file_name := path.get_file().get_basename()
	var is_button := path.contains("/buttons/") or path.contains("/instruments/")
	var is_panel := path.contains("/panels/")
	var is_input := path.contains("/inputs/")
	var is_tab := file_name.begins_with("tab_") or file_name in ["tab_panel", "tab_entry", "tab_note", "tab_note_selected", "tab_note_error"]
	var primary := palette.panel_color
	var accent := palette.highlight_color
	if path.contains("/destructive/") or path.contains("/beatremove/"):
		accent = palette.destructive_button_color
	elif path.contains("/solo/"):
		accent = palette.solo_button_color
	elif path.contains("/mute/"):
		accent = palette.mute_button_color
	elif path.contains("/volume/"):
		accent = palette.intensity_color
	elif file_name.contains("solo"):
		accent = palette.solo_button_color
	elif file_name.contains("mute"):
		accent = palette.mute_button_color
	elif file_name.contains("realize"):
		accent = palette.intensity_color
	elif path.contains("/whitekey/"):
		accent = palette.highlight_color
		primary = Color("d9deea")
	elif path.contains("/blackkey/"):
		accent = palette.highlight_color
		primary = Color("20253a")

	if is_tab:
		return _apply_tab_style(flat, file_name, palette)
	if is_button:
		return _apply_button_style(flat, file_name, primary, accent, palette, path.contains("/whitekey/"), path.contains("/blackkey/"))
	if is_input:
		return _apply_input_style(flat, file_name, palette)
	if is_panel:
		return _apply_panel_style(flat, file_name, palette)
	return false

func _apply_texture_style(style: StyleBoxTexture, path: String, palette: ThemePalette) -> bool:
	if not path.ends_with("/panels/background.tres"):
		return false
	var gradient_texture := style.texture as GradientTexture2D
	if gradient_texture == null or gradient_texture.gradient == null:
		return false
	var gradient := gradient_texture.gradient
	for point in range(gradient.get_point_count()):
		var amount := gradient.get_offset(point)
		gradient.set_color(point, _mix(palette.background_color, _ink_color(palette), amount * 0.32))
	return true

func _apply_button_style(style: StyleBoxFlat, state: String, base: Color, accent: Color, palette: ThemePalette, is_white_key := false, is_black_key := false) -> bool:
	var color := base
	match state:
		"normal": color = base if is_white_key or is_black_key else _mix(palette.panel_color, palette.background_color, 0.22)
		"hover": color = _adjust_lightness(base, palette.button_highlight_difference)
		"pressed", "hover_pressed": color = accent
		"disabled": color = _mix(base, palette.panel_color, 0.35) if is_white_key or is_black_key else _mix(palette.panel_color, palette.background_color, 0.35)
		"focus": color = _adjust_lightness(base, palette.button_highlight_difference * 0.7)
		_: return false
	if is_black_key and state == "normal":
		color = base
	style.bg_color = _keep_alpha(color, style.bg_color)
	style.border_color = _keep_alpha(_mix(accent, color, 0.45), style.border_color)
	style.shadow_color = _keep_alpha(_mix(palette.background_color, color, 0.35), style.shadow_color)
	return true

func _apply_panel_style(style: StyleBoxFlat, name: String, palette: ThemePalette) -> bool:
	var fill := palette.panel_color
	if name.contains("background"):
		fill = palette.background_color
	elif name.contains("header") or name.contains("title"):
		fill = _mix(palette.panel_color, palette.highlight_color, 0.16)
	elif name.contains("popup") or name.contains("window"):
		fill = _mix(palette.panel_color, palette.background_color, 0.12)
	elif name.contains("active"):
		fill = _mix(palette.panel_color, palette.highlight_color, 0.25)
	style.bg_color = _keep_alpha(fill, style.bg_color)
	var border_mix := 0.68
	if name.contains("focus"):
		border_mix = 0.22
	style.border_color = _keep_alpha(_mix(palette.highlight_color, fill, border_mix), style.border_color)
	style.shadow_color = _keep_alpha(_mix(palette.background_color, fill, 0.3), style.shadow_color)
	return true

func _apply_input_style(style: StyleBoxFlat, name: String, palette: ThemePalette) -> bool:
	var fill := _mix(palette.background_color, palette.panel_color, 0.28)
	var border := _mix(palette.muted_text_color, fill, 0.7)
	if name.contains("grabber") or name.contains("handle") or name.contains("slider_fill") or name.contains("slider_active"):
		fill = _mix(palette.highlight_color, palette.panel_color, 0.32)
		border = palette.highlight_color
	elif name.contains("selected") or name.contains("focus"):
		border = palette.highlight_color
	elif name.contains("readonly") or name.contains("disabled"):
		fill = _mix(palette.panel_color, palette.background_color, 0.3)
		border = _mix(palette.muted_text_color, fill, 0.72)
	style.bg_color = _keep_alpha(fill, style.bg_color)
	style.border_color = _keep_alpha(border, style.border_color)
	style.shadow_color = _keep_alpha(palette.background_color, style.shadow_color)
	return true

func _apply_tab_style(style: StyleBoxFlat, name: String, palette: ThemePalette) -> bool:
	if name.contains("error"):
		style.bg_color = _keep_alpha(_mix(palette.destructive_button_color, palette.background_color, 0.55), style.bg_color)
		style.border_color = _keep_alpha(palette.destructive_button_color, style.border_color)
	elif name.contains("selected"):
		style.bg_color = _keep_alpha(_mix(palette.highlight_color, palette.panel_color, 0.25), style.bg_color)
		style.border_color = _keep_alpha(palette.highlight_color, style.border_color)
	elif name.contains("note"):
		style.bg_color = _keep_alpha(_mix(palette.panel_color, palette.background_color, 0.36), style.bg_color)
		style.border_color = _keep_alpha(_mix(palette.highlight_color, palette.panel_color, 0.5), style.border_color)
	else:
		style.bg_color = _keep_alpha(palette.panel_color, style.bg_color)
		style.border_color = _keep_alpha(_mix(palette.muted_text_color, palette.panel_color, 0.62), style.border_color)
	style.shadow_color = _keep_alpha(palette.background_color, style.shadow_color)
	return true

func _keep_alpha(color: Color, original: Color) -> Color:
	color.a = original.a
	return color

func _readable_text(background: Color) -> Color:
	# Compare contrast with white and near-black for arbitrary user-selected accents.
	var linear := background.srgb_to_linear()
	var luminance := 0.2126 * linear.r + 0.7152 * linear.g + 0.0722 * linear.b
	return Color(0.035, 0.04, 0.07) if luminance > 0.18 else Color.WHITE

func _ink_color(palette: ThemePalette) -> Color:
	return _mix(palette.background_color, Color.BLACK, 0.22)

func _adjust_lightness(color: Color, difference: float) -> Color:
	if color.v < 0.5:
		return color.lightened(difference)
	return color.darkened(difference)

func _mix(a: Color, b: Color, amount: float) -> Color:
	return a.lerp(b, clampf(amount, 0.0, 1.0))









