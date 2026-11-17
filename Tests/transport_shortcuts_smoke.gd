extends SceneTree

func _initialize() -> void:
	call_deferred("run_check")

func run_check() -> void:
	var scene := (load("res://Interface/Interface.tscn") as PackedScene).instantiate()
	root.add_child(scene)
	await process_frame
	var play_button := scene.get_node("%PlayPauseButton") as Button
	var space := InputEventKey.new()
	space.keycode = KEY_SPACE
	space.pressed = true
	var control_space := InputEventKey.new()
	control_space.keycode = KEY_SPACE
	control_space.ctrl_pressed = true
	control_space.pressed = true
	if not space.is_action_pressed("play", false, true) or not space.is_action_pressed("stop", false, true) or not control_space.is_action_pressed("play_from_start", false, true):
		fail("The transport action bindings do not match their keys")
		return
	var q := InputEventKey.new()
	q.keycode = KEY_Q
	q.pressed = true
	var w := InputEventKey.new()
	w.keycode = KEY_W
	w.pressed = true
	var e := InputEventKey.new()
	e.keycode = KEY_E
	e.pressed = true
	if not q.is_action_pressed("repeat_score", false, true) or not w.is_action_pressed("repeat_measure", false, true) or not e.is_action_pressed("repeat_sections", false, true):
		fail("The repeat scope actions do not map to Q/W/E")
		return
	var loop_all := scene.get_node("%LoopAllButton") as Button
	var loop_measure := scene.get_node("%LoopMeasureButton") as Button
	var loop_sections := scene.get_node("%LoopSectionsButton") as Button
	Input.parse_input_event(w)
	await process_frame
	if not loop_measure.button_pressed:
		fail("W did not select measure repeat")
		return
	Input.parse_input_event(w)
	await process_frame
	if not loop_measure.button_pressed:
		fail("W toggled measure repeat off instead of selecting it")
		return
	Input.parse_input_event(e)
	await process_frame
	if not loop_sections.button_pressed:
		fail("E did not select section repeat")
		return
	Input.parse_input_event(q)
	await process_frame
	if not loop_all.button_pressed:
		fail("Q did not select full-score repeat")
		return
	Input.parse_input_event(space)
	await process_frame
	if not play_button.button_pressed:
		fail("Space did not start playback")
		return
	Input.parse_input_event(control_space)
	await process_frame
	if not play_button.button_pressed:
		fail("Ctrl+Space did not keep playback running")
		return
	Input.parse_input_event(space)
	await process_frame
	if play_button.button_pressed:
		fail("Space did not stop playback")
		return
	Input.parse_input_event(control_space)
	await process_frame
	if not play_button.button_pressed:
		fail("Ctrl+Space did not start playback when stopped")
		return
	play_button.button_pressed = false
	await process_frame
	Input.parse_input_event(control_space)
	await process_frame
	if not play_button.button_pressed:
		fail("Ctrl+Space did not restart paused playback")
		return
	var echo := space.duplicate() as InputEventKey
	echo.echo = true
	Input.parse_input_event(echo)
	await process_frame
	if not play_button.button_pressed:
		fail("A repeated key event changed playback")
		return
	var editor := LineEdit.new()
	scene.add_child(editor)
	editor.grab_focus()
	Input.parse_input_event(space)
	await process_frame
	if not play_button.button_pressed:
		fail("Space in a text field stopped playback")
		return
	editor.release_focus()
	play_button.button_pressed = false
	var arrangement := scene.get_node("%SectionsArrangement")
	var toolbar := arrangement.get_node("VBoxContainer/BoxContainer")
	var menu := toolbar.get_node("SelectArrangementMenuButton") as MenuButton
	menu.get_popup().id_pressed.emit(2147483647)
	var add := arrangement.get_node("VBoxContainer/HBoxContainer/ScrollContainer/SectionsOrder/BoxContainer/AddSectionOptionButton") as OptionButton
	add.item_selected.emit(1)
	var sections := scene.get_node("%Sections")
	var section_buttons := sections.get_node("VBoxContainer/HBoxContainer2/ScrollContainer/SectionButtons") as HBoxContainer
	var arrangement_section_name := section_buttons.get_child(0).name
	var add_section := sections.get_node("VBoxContainer/HBoxContainer2/AddButton") as Button
	add_section.pressed.emit()
	await process_frame
	if (section_buttons.get_node(NodePath(arrangement_section_name)) as Button).button_pressed:
		fail("The setup did not hide the arrangement section before playback")
		return
	var player := toolbar.get_node("PanelContainer/BoxContainer")
	var arrangement_play := player.get_node("PlayArrangementButton") as Button
	var shortcuts := player.get_node("PrimaryPlayerEnabledButton") as Button
	for route_to_arrangement in [false, true]:
		shortcuts.button_pressed = route_to_arrangement
		arrangement_play.button_pressed = true
		await process_frame
		if not arrangement_play.button_pressed:
			fail("Arrangement playback did not start")
			return
		if not (section_buttons.get_node(NodePath(arrangement_section_name)) as Button).button_pressed:
			fail("Arrangement playback did not reveal its referenced section before starting")
			return
		Input.parse_input_event(space)
		await process_frame
		if arrangement_play.button_pressed or play_button.button_pressed:
			fail("Space did not stop both players with arrangement shortcuts %s" % route_to_arrangement)
			return
	print("PASS transport shortcuts, arrangement routing, action mapping, key repeat and text input")
	scene.queue_free()
	quit(0)

func fail(message: String) -> void:
	push_error(message)
	quit(1)
