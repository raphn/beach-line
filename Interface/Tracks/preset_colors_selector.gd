extends Window

signal color_selected(color: Color)


func _ready() -> void:
	for picker in find_children("PresetColorPicker*", "Button", true, false):
		picker.color_selected.connect(_on_picker_color_selected)
	close_requested.connect(hide)


func _unhandled_key_input(event: InputEvent) -> void:
	if event is InputEventKey and event.pressed and event.keycode == KEY_ESCAPE:
		hide()
		get_viewport().set_input_as_handled()


func _on_picker_color_selected(color: Color) -> void:
	color_selected.emit(color)
	hide()
