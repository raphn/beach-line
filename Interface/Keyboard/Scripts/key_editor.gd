@tool
extends Node
class_name KeyboardKeyMultiEditor

@export var h_box_gap := 0
@export var h_size := 16.0
@export var wv_size := 64.0
@export var bv_size := 40.0

@export_category("Local")
@export var root : Control
@export var whites : Array[Button]
@export var blacks : Array[Button]
@export var hboxs : Array[HBoxContainer]

@export_category("Controls")
@export_tool_button("Refresh Keys") var editor_refresh_keys: Callable:
	get:
		return __refresh_keys


func __refresh_keys() -> void:
	whites.clear()
	blacks.clear()
	hboxs.clear()
	
	_search_append_all_buttons(root)
	notify_property_list_changed()
	
	
	for k in whites:
		_setup_button(k, "WhiteKey", Vector2(h_size, wv_size))
	for k in blacks:
		_setup_button(k, "BlackKey", Vector2(h_size, bv_size))
	
	for h in hboxs:
		h.set("theme_override_constants/separation", h_box_gap)


func _setup_button(btn:Button, theme:String, sz:Vector2) -> void:
	btn.theme_type_variation = theme
	btn.custom_minimum_size = sz
	btn.toggle_mode = true

func _search_append_all_buttons(from:Node) -> void:
	var children := from.get_children()
	if not children.is_empty():
		for c in children:
			_search_append_all_buttons(c)
	
	if from is Button:
		if str(from.get_parent().name) == "Whites":
			whites.append(from)
		else:
			blacks.append(from)
	
	if from is HBoxContainer:
		hboxs.append(from)
