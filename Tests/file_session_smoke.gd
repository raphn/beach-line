extends SceneTree

func _initialize() -> void:
	call_deferred("run_check")

func run_check() -> void:
	var packed := load("res://Interface/Interface.tscn") as PackedScene
	var scene := packed.instantiate()
	root.add_child(scene)
	await process_frame
	var base := "MainContainer/EditingContainers/PerscussionLine/ScrollContainer/InfoBeatsSplitter/Infos/PercussionControls/InfoControllers/"
	var selector := scene.get_node(base + "SectionSelector") as OptionButton
	if selector.item_count != 2:
		fail("Expected the two-section example")
		return
	(scene.get_node(base + "DeleteSectionButton") as Button).pressed.emit()
	(scene.get_node("DeleteSectionConfirmation") as ConfirmationDialog).confirmed.emit()
	await create_timer(0.8).timeout
	scene.queue_free()
	await process_frame
	scene = packed.instantiate()
	root.add_child(scene)
	await process_frame
	selector = scene.get_node(base + "SectionSelector") as OptionButton
	if selector.item_count != 1:
		fail("Deleted section returned after reopening")
		return
	var menu := scene.get_node("MainContainer/ToolsPanel/VBoxContainer/MenuBar/File") as PopupMenu
	if menu.item_count != 5:
		fail("The File menu is missing its actions")
		return
	var save_key := InputEventKey.new()
	save_key.keycode = KEY_S
	save_key.ctrl_pressed = true
	save_key.pressed = true
	if not save_key.is_action_pressed("saved", false, true):
		fail("Ctrl+S is not bound to saved")
		return
	Input.parse_input_event(save_key)
	await process_frame
	var save_dialog: FileDialog
	for child in scene.get_children():
		if child is FileDialog and child.title == "Save score":
			save_dialog = child
	if save_dialog == null or not save_dialog.visible:
		fail("Ctrl+S did not open Save As for the unsaved example")
		return
	var path := ProjectSettings.globalize_path("user://smoke_saved.otab")
	save_dialog.file_selected.emit(path)
	save_dialog.hide()
	await process_frame
	if not FileAccess.file_exists(path):
		fail("Save As did not write the score")
		return
	(scene.get_node(base + "AddSectionButton") as Button).pressed.emit()
	await process_frame
	menu.id_pressed.emit(0)
	await process_frame
	var discard: ConfirmationDialog
	var open_dialog: FileDialog
	for child in scene.get_children():
		if child is ConfirmationDialog and child.title == "Discard unsaved changes?":
			discard = child
		if child is FileDialog and child.title == "Open score":
			open_dialog = child
	if discard == null or not discard.visible or open_dialog == null:
		fail("Opening a score did not confirm unsaved edits (discard=%s, visible=%s, open=%s)" % [discard != null, discard.visible if discard else false, open_dialog != null])
		return
	discard.confirmed.emit()
	discard.hide()
	await process_frame
	open_dialog.file_selected.emit(path)
	open_dialog.hide()
	await process_frame
	if selector.item_count != 1:
		fail("Opening the saved score did not restore its sections")
		return
	print("PASS session restoration, file menu, Ctrl+S save and reopening")
	scene.queue_free()
	quit(0)

func fail(message: String) -> void:
	push_error(message)
	quit(1)
