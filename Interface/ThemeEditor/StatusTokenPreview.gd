@tool
extends HBoxContainer

const STATUS_TOKENS := ["complete", "warning", "error", "muted"]

func _ready() -> void:
	add_theme_constant_override("separation", 12)
	for token in STATUS_TOKENS:
		var sample := Label.new()
		sample.text = "AppStatus / " + token
		add_child(sample)
	_update_colors()

func _notification(what: int) -> void:
	if what == NOTIFICATION_THEME_CHANGED and is_node_ready():
		_update_colors()

func _update_colors() -> void:
	for i in range(mini(get_child_count(), STATUS_TOKENS.size())):
		(get_child(i) as Label).add_theme_color_override("font_color", get_theme_color(STATUS_TOKENS[i], "AppStatus"))
