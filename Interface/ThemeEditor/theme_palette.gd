class_name ThemePalette
extends Resource

## Compact user palette; themes and style resources are generated in memory.
@export_group("Surfaces and text")
@export var background_color: Color = Color(0.099, 0.1012, 0.11)
@export var panel_color: Color = Color(0.19, 0.19, 0.19)
@export var text_color: Color = Color("e8edf9")
@export var muted_text_color: Color = Color("9da8c3")
@export var highlight_color: Color = Color("39c7cb")
@export var destructive_button_color: Color = Color("ff5b73")
@export var mute_button_color: Color = Color("ffc857")
@export var solo_button_color: Color = Color("37d9f5")
@export var intensity_color: Color = Color("37d9f5")

@export_group("Tab notation")
@export var tab_background_color: Color = Color(0.099, 0.1012, 0.11)
@export var tab_grid_color: Color = Color(0.28, 0.3, 0.43)
@export var tab_string_color: Color = Color(0.42, 0.46, 0.6)
@export var tab_text_color: Color = Color("e8edf9")
@export var beat_background_color: Color = Color(0.15724, 0.158032, 0.1612)
@export_range(1, 4, 1) var tab_line_thickness: int = 1

@export_group("State shaping")
@export_range(0.0, 0.5, 0.01) var button_highlight_difference: float = 0.12

func copy() -> ThemePalette:
	var result := ThemePalette.new()
	result.background_color = background_color
	result.panel_color = panel_color
	result.text_color = text_color
	result.muted_text_color = muted_text_color
	result.highlight_color = highlight_color
	result.destructive_button_color = destructive_button_color
	result.mute_button_color = mute_button_color
	result.solo_button_color = solo_button_color
	result.intensity_color = intensity_color
	result.tab_background_color = tab_background_color
	result.tab_grid_color = tab_grid_color
	result.tab_string_color = tab_string_color
	result.tab_text_color = tab_text_color
	result.beat_background_color = beat_background_color
	result.tab_line_thickness = tab_line_thickness
	result.button_highlight_difference = button_highlight_difference
	result.active_groups = active_groups.duplicate()
	return result

@export var active_groups: Array[StringName] = []




