extends Button

signal color_selected(color: Color)

@export var picker_color: Color = Color.WHITE:
	set(value):
		picker_color = value
		_update_swatch()

@onready var _swatch: ColorRect = $ColorRect


func _ready() -> void:
	_update_swatch()
	pressed.connect(_on_pressed)


func _update_swatch() -> void:
	tooltip_text = tr("Track color")
	if is_instance_valid(_swatch):
		_swatch.color = picker_color


func _on_pressed() -> void:
	color_selected.emit(picker_color)
