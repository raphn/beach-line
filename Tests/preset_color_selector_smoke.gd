extends SceneTree

var observed: Array[Color] = []

func _initialize() -> void:
	call_deferred("_check")

func _check() -> void:
	var scene := load("res://Interface/Tracks/PresetColorsSelector.tscn") as PackedScene
	if scene == null:
		push_error("Could not load preset colors selector")
		quit(1)
		return
	var selector := scene.instantiate() as Window
	root.add_child(selector)
	await process_frame
	var pickers := selector.find_children("PresetColorPicker*", "Button", true, false)
	if pickers.size() != 16:
		push_error("Expected 16 color swatches")
		quit(1)
		return
	var colors: Array[Color] = []
	selector.connect("color_selected", func(color: Color) -> void: observed.append(color))
	for item in pickers:
		var picker := item as Button
		var color: Color = picker.get("picker_color")
		var swatch := picker.get_node("ColorRect") as ColorRect
		if swatch.color != color or colors.has(color):
			push_error("Swatches must display distinct configured colors")
			quit(1)
			return
		colors.append(color)
		selector.show()
		picker.pressed.emit()
		if observed.is_empty() or observed.back() != color or selector.visible:
			push_error("Swatch selection did not emit its color and close")
			quit(1)
			return
	if not colors.has(Color.WHITE) or observed.size() != 16:
		push_error("White preset missing or selection emitted more than once")
		quit(1)
		return
	selector.free()
	print("Preset selector: 16 distinct visible colors, including white, emit once and close.")
	quit(0)
