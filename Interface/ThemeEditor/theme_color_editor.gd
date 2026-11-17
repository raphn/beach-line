@tool
extends Node

## Edits the canonical shared theme palette in the Godot editor.
## StyleBox resources are mutated in place and saved once per resource identity.

const THEME_PATH := "res://Interface/Themes/main.tres"
const APPLY_DELAY_SECONDS := 0.75

@export_group("Surfaces and text")
@export var BackgroundColor: Color = Color("090d1d"):
	set(value):
		BackgroundColor = value
		_schedule_apply(&"surface")
@export var PanelColor: Color = Color("151a30"):
	set(value):
		PanelColor = value
		_schedule_apply(&"surface")
@export var TextColor: Color = Color("e8edf9"):
	set(value):
		TextColor = value
		_schedule_apply(&"text")
@export var MutedTextColor: Color = Color("9da8c3"):
	set(value):
		MutedTextColor = value
		_schedule_apply(&"muted_text")
@export var HighlightColor: Color = Color("a99bff"):
	set(value):
		HighlightColor = value
		_schedule_apply(&"highlight")
@export var DestructiveButtonColor: Color = Color("ff5b73"):
	set(value):
		DestructiveButtonColor = value
		_schedule_apply(&"destructive")
@export var MuteButtonColor: Color = Color("ffc857"):
	set(value):
		MuteButtonColor = value
		_schedule_apply(&"mute")
@export var SoloButtonColor: Color = Color("37d9f5"):
	set(value):
		SoloButtonColor = value
		_schedule_apply(&"solo")
@export var IntensityColor: Color = Color("37d9f5"):
	set(value):
		IntensityColor = value
		_schedule_apply(&"intensity")

@export_group("State shaping")
@export_range(0.0, 0.5, 0.01) var ButtonHighlightDifference: float = 0.12:
	set(value):
		ButtonHighlightDifference = value
		_schedule_apply(&"button_difference")

var _ready_for_edits := false
var _apply_timer: Timer
var _dirty_groups: Array[StringName] = []

func _ready() -> void:
	if Engine.is_editor_hint():
		_apply_timer = Timer.new()
		_apply_timer.name = "PaletteApplyDebounceTimer"
		_apply_timer.one_shot = true
		_apply_timer.wait_time = APPLY_DELAY_SECONDS
		_apply_timer.timeout.connect(_apply_scheduled)
		add_child(_apply_timer)
	_ready_for_edits = true

func _exit_tree() -> void:
	if _apply_timer != null:
		_apply_timer.stop()
	if Engine.is_editor_hint() and not _dirty_groups.is_empty():
		_apply_palette_groups(_dirty_groups)
		_dirty_groups.clear()
	_apply_timer = null
	_ready_for_edits = false

func _schedule_apply(group: StringName) -> void:
	if not _ready_for_edits or not is_inside_tree() or not Engine.is_editor_hint():
		return
	if not _dirty_groups.has(group):
		_dirty_groups.append(group)
	if _apply_timer == null:
		return
	# Timer.start() restarts the countdown, so edits are applied only after the
	# inspector has stopped changing values for the full debounce interval.
	_apply_timer.start(APPLY_DELAY_SECONDS)

func _apply_scheduled() -> void:
	if is_inside_tree() and Engine.is_editor_hint() and not _dirty_groups.is_empty():
		_apply_palette_groups(_dirty_groups)
		_dirty_groups.clear()

## Public entry point for a headless editor test or an explicit editor action.
## It intentionally does nothing in a running game.
func apply_palette() -> void:
	if not Engine.is_editor_hint():
		return
	_apply_palette_groups([&"surface", &"text", &"muted_text", &"highlight", &"destructive", &"mute", &"solo", &"intensity", &"button_difference"])

func _apply_palette_groups(groups: Array[StringName]) -> void:
	var theme := load(THEME_PATH) as Theme
	if theme == null:
		push_error("Theme color editor could not load %s" % THEME_PATH)
		return

	_apply_theme_colors(theme, groups)
	var seen_resources: Dictionary = {}
	for type_name in theme.get_type_list():
		for style_name in theme.get_stylebox_list(type_name):
			var style := theme.get_stylebox(style_name, type_name)
			if style == null:
				continue
			var instance_id := style.get_instance_id()
			if seen_resources.has(instance_id):
				continue
			seen_resources[instance_id] = true
			if _style_path_is_affected(style.resource_path.to_lower(), groups) and _apply_stylebox(style):
				var resource_path := style.resource_path
				if not resource_path.is_empty() and not resource_path.contains("::"):
					var error := ResourceSaver.save(style, resource_path)
					if error != OK:
						push_error("Could not save theme style %s (error %d)" % [resource_path, error])

	var theme_error := ResourceSaver.save(theme, THEME_PATH)
	if theme_error != OK:
		push_error("Could not save theme %s (error %d)" % [THEME_PATH, theme_error])
	else:
		print("Theme palette saved to %s" % THEME_PATH)

func _style_path_is_affected(path: String, groups: Array[StringName]) -> bool:
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
		if key in ["selected_text"]:
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

func _apply_theme_colors(theme: Theme, groups: Array[StringName]) -> void:
	for type_name in theme.get_type_list():
		for color_name in theme.get_color_list(type_name):
			var key := String(color_name).to_lower()
			var type_key := String(type_name).to_lower()
			if not groups.has(_theme_color_group(type_key, key)):
				continue
			var color := theme.get_color(color_name, type_name)
			var replacement := _mapped_theme_color(type_key, key, color)
			replacement = _keep_alpha(replacement, color)
			if replacement != color:
				theme.set_color(color_name, type_name, replacement)

func _mapped_theme_color(type_key: String, key: String, original: Color) -> Color:
	if (key.contains("font") or key.contains("icon")) and key.contains("pressed"):
		if type_key == "appmutebutton": return _readable_text(MuteButtonColor)
		if type_key == "appsolobutton": return _readable_text(SoloButtonColor)
		if type_key in ["appdestructivebutton", "beatremovebutton"]: return _readable_text(DestructiveButtonColor)
		if type_key == "beatintensitybutton": return _readable_text(IntensityColor)
		if type_key.ends_with("button") or type_key in ["fret", "handfret", "markfret", "blackkey", "whitekey"]: return _readable_text(HighlightColor)
	if type_key in ["blackkey", "whitekey"] and (key.contains("font") or key.contains("icon")):
		if key.contains("disabled"):
			return MutedTextColor
		if key.contains("pressed"):
			return _ink_color()
		return _ink_color() if type_key == "whitekey" else TextColor
	if type_key == "tabmeasureview":
		match key:
			"background": return BackgroundColor
			"entry_background", "note", "subdivision": return _mix(PanelColor, BackgroundColor, 0.36)
			"entry_border", "note_selected", "note_selected_border": return HighlightColor
			"grid", "string_line", "bar_line", "timeline": return _mix(MutedTextColor, BackgroundColor, 0.48)
			"hint", "beat_text": return MutedTextColor
			"continuation": return _mix(HighlightColor, SoloButtonColor, 0.35)
			"string_text", "text": return TextColor
			"error": return DestructiveButtonColor
			"caret": return SoloButtonColor
			"selected_text": return _readable_text(HighlightColor)
	if type_key == "appstatus":
		if key.contains("muted"):
			return MutedTextColor
		if key.contains("warning"):
			return MuteButtonColor
		if key.contains("error") or key.contains("danger"):
			return DestructiveButtonColor
		return SoloButtonColor
	if type_key == "appsuccesslabel":
		return SoloButtonColor
	if key.contains("font") or key == "text" or key.ends_with("_text") or key == "title_color":
		if key.contains("disabled") or key.contains("placeholder") or key.contains("uneditable"):
			return MutedTextColor
		if key.contains("selected") and type_key != "tabmeasureview":
			return _ink_color()
		if key.contains("hover_pressed") or key.contains("pressed"):
			return _ink_color()
		return TextColor
	if key.contains("caret"):
		return SoloButtonColor
	if key.contains("selection"):
		return _mix(HighlightColor, PanelColor, 0.42)
	if type_key == "spinbox" and key.contains("icon"):
		return MutedTextColor if key.contains("disabled") else TextColor
	if key.contains("disabled"):
		return MutedTextColor
	return original

func _apply_stylebox(style: StyleBox) -> bool:
	var path := style.resource_path.to_lower()
	if style is StyleBoxTexture:
		return _apply_texture_style(style as StyleBoxTexture, path)
	if style is StyleBoxLine and path.contains("/inputs/"):
		var line := style as StyleBoxLine
		line.color = _keep_alpha(MutedTextColor, line.color)
		return true
	if not style is StyleBoxFlat:
		return false
	var flat := style as StyleBoxFlat
	var file_name := path.get_file().get_basename()
	var is_button := path.contains("/buttons/") or path.contains("/instruments/")
	var is_panel := path.contains("/panels/")
	var is_input := path.contains("/inputs/")
	var is_tab := file_name.begins_with("tab_") or file_name in ["tab_panel", "tab_entry", "tab_note", "tab_note_selected", "tab_note_error"]
	var primary := PanelColor
	var accent := HighlightColor
	if path.contains("/destructive/") or path.contains("/beatremove/"):
		accent = DestructiveButtonColor
	elif path.contains("/solo/"):
		accent = SoloButtonColor
	elif path.contains("/mute/"):
		accent = MuteButtonColor
	elif path.contains("/volume/"):
		accent = IntensityColor
	elif file_name.contains("solo"):
		accent = SoloButtonColor
	elif file_name.contains("mute"):
		accent = MuteButtonColor
	elif file_name.contains("realize"):
		accent = IntensityColor
	elif path.contains("/whitekey/"):
		accent = HighlightColor
		primary = Color("d9deea")
	elif path.contains("/blackkey/"):
		accent = HighlightColor
		primary = Color("20253a")

	if is_tab:
		return _apply_tab_style(flat, file_name)
	if is_button:
		return _apply_button_style(flat, file_name, primary, accent, path.contains("/whitekey/"), path.contains("/blackkey/"))
	if is_input:
		return _apply_input_style(flat, file_name)
	if is_panel:
		return _apply_panel_style(flat, file_name)
	return false

func _apply_texture_style(style: StyleBoxTexture, path: String) -> bool:
	if not path.ends_with("/panels/background.tres"):
		return false
	var gradient_texture := style.texture as GradientTexture2D
	if gradient_texture == null or gradient_texture.gradient == null:
		return false
	var gradient := gradient_texture.gradient
	for point in range(gradient.get_point_count()):
		var amount := gradient.get_offset(point)
		gradient.set_color(point, _mix(BackgroundColor, _ink_color(), amount * 0.32))
	return true

func _apply_button_style(style: StyleBoxFlat, state: String, base: Color, accent: Color, is_white_key := false, is_black_key := false) -> bool:
	var color := base
	match state:
		"normal": color = base if is_white_key or is_black_key else _mix(PanelColor, BackgroundColor, 0.22)
		"hover": color = _adjust_lightness(base, ButtonHighlightDifference)
		"pressed", "hover_pressed": color = accent
		"disabled": color = _mix(base, PanelColor, 0.35) if is_white_key or is_black_key else _mix(PanelColor, BackgroundColor, 0.35)
		"focus": color = _adjust_lightness(base, ButtonHighlightDifference * 0.7)
		_: return false
	if is_black_key and state == "normal":
		color = base
	style.bg_color = _keep_alpha(color, style.bg_color)
	style.border_color = _keep_alpha(_mix(accent, color, 0.45), style.border_color)
	style.shadow_color = _keep_alpha(_mix(BackgroundColor, color, 0.35), style.shadow_color)
	return true

func _apply_panel_style(style: StyleBoxFlat, name: String) -> bool:
	var fill := PanelColor
	if name.contains("background"):
		fill = BackgroundColor
	elif name.contains("header") or name.contains("title"):
		fill = _mix(PanelColor, HighlightColor, 0.16)
	elif name.contains("popup") or name.contains("window"):
		fill = _mix(PanelColor, BackgroundColor, 0.12)
	elif name.contains("active"):
		fill = _mix(PanelColor, HighlightColor, 0.25)
	style.bg_color = _keep_alpha(fill, style.bg_color)
	var border_mix := 0.68
	if name.contains("focus"):
		border_mix = 0.22
	style.border_color = _keep_alpha(_mix(HighlightColor, fill, border_mix), style.border_color)
	style.shadow_color = _keep_alpha(_mix(BackgroundColor, fill, 0.3), style.shadow_color)
	return true

func _apply_input_style(style: StyleBoxFlat, name: String) -> bool:
	var fill := _mix(BackgroundColor, PanelColor, 0.28)
	var border := _mix(MutedTextColor, fill, 0.7)
	if name.contains("grabber") or name.contains("handle") or name.contains("slider_fill") or name.contains("slider_active"):
		fill = _mix(HighlightColor, PanelColor, 0.32)
		border = HighlightColor
	elif name.contains("selected") or name.contains("focus"):
		border = HighlightColor
	elif name.contains("readonly") or name.contains("disabled"):
		fill = _mix(PanelColor, BackgroundColor, 0.3)
		border = _mix(MutedTextColor, fill, 0.72)
	style.bg_color = _keep_alpha(fill, style.bg_color)
	style.border_color = _keep_alpha(border, style.border_color)
	style.shadow_color = _keep_alpha(BackgroundColor, style.shadow_color)
	return true

func _apply_tab_style(style: StyleBoxFlat, name: String) -> bool:
	if name.contains("error"):
		style.bg_color = _keep_alpha(_mix(DestructiveButtonColor, BackgroundColor, 0.55), style.bg_color)
		style.border_color = _keep_alpha(DestructiveButtonColor, style.border_color)
	elif name.contains("selected"):
		style.bg_color = _keep_alpha(_mix(HighlightColor, PanelColor, 0.25), style.bg_color)
		style.border_color = _keep_alpha(HighlightColor, style.border_color)
	elif name.contains("note"):
		style.bg_color = _keep_alpha(_mix(PanelColor, BackgroundColor, 0.36), style.bg_color)
		style.border_color = _keep_alpha(_mix(HighlightColor, PanelColor, 0.5), style.border_color)
	else:
		style.bg_color = _keep_alpha(PanelColor, style.bg_color)
		style.border_color = _keep_alpha(_mix(MutedTextColor, PanelColor, 0.62), style.border_color)
	style.shadow_color = _keep_alpha(BackgroundColor, style.shadow_color)
	return true

func _keep_alpha(color: Color, original: Color) -> Color:
	color.a = original.a
	return color

func _readable_text(background: Color) -> Color:
	# Compare contrast with white and near-black for arbitrary user-selected accents.
	var linear := background.srgb_to_linear()
	var luminance := 0.2126 * linear.r + 0.7152 * linear.g + 0.0722 * linear.b
	return Color(0.035, 0.04, 0.07) if luminance > 0.18 else Color.WHITE

func _ink_color() -> Color:
	return _mix(BackgroundColor, Color.BLACK, 0.22)

func _adjust_lightness(color: Color, difference: float) -> Color:
	if color.v < 0.5:
		return color.lightened(difference)
	return color.darkened(difference)

func _mix(a: Color, b: Color, amount: float) -> Color:
	return a.lerp(b, clampf(amount, 0.0, 1.0))
