@tool
extends Node
class_name GuitarFretboardEditor

@export_category("Local")
@export var root : Control

@export_category("Controls")
@export_tool_button("Refresh Keys") var editor_refresh_frets: Callable:
	get:
		return __refresh_frets


func __refresh_frets() -> void:
	const marks := [3, 5, 7, 9, 12, 15, 17, 19, 21, 24]
	
	var id := 0
	for c in root.get_children():
		if c.name == "Editor":
			continue
		
		var btn := c as Button
		if btn:
			btn.toggle_mode = true
			if id == 0:
				btn.theme_type_variation = "HandFret"
			elif marks.has(id):
				btn.theme_type_variation = "MarkFret"
			else:
				btn.theme_type_variation = "Fret"
			btn.text = ""
			id += 1
