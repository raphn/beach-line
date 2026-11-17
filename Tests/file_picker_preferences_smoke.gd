extends SceneTree

func _initialize() -> void:
	call_deferred("run_check")

func normalized(path: String) -> String:
	return path.replace("\\", "/").trim_suffix("/").to_lower()

func run_check() -> void:
	var folder := ProjectSettings.globalize_path("user://favorite-folder")
	DirAccess.make_dir_recursive_absolute(folder)
	var scene := (load("res://Interface/Interface.tscn") as PackedScene).instantiate()
	root.add_child(scene)
	await process_frame
	var menu := scene.get_node("MainContainer/ToolsPanel/VBoxContainer/MenuBar/File") as PopupMenu
	var open_dialog: FileDialog
	var save_dialog: FileDialog
	for child in scene.get_children():
		if child is FileDialog and child.title == "Open score": open_dialog = child
		if child is FileDialog and child.title == "Save score": save_dialog = child
	if open_dialog == null or save_dialog == null or not open_dialog.favorites_enabled or not save_dialog.favorites_enabled:
		fail("File dialogs or their favorites controls are missing")
		return
	var read_mode := "read" in OS.get_cmdline_user_args()
	if read_mode:
		var favorites := FileDialog.get_favorite_list()
		if favorites.size() != 1 or normalized(favorites[0]) != normalized(folder):
			fail("The favorite folder was not restored")
			return
		menu.id_pressed.emit(2)
		await process_frame
		if normalized(save_dialog.current_dir) != normalized(folder):
			fail("Save As did not restore the last folder after restart")
			return
		save_dialog.hide()
	else:
		menu.id_pressed.emit(0)
		await process_frame
		open_dialog.current_dir = folder
		FileDialog.set_favorite_list(PackedStringArray([folder]))
		open_dialog.hide()
		await process_frame
		menu.id_pressed.emit(2)
		await process_frame
		if normalized(save_dialog.current_dir) != normalized(folder):
			fail("Save As did not share the folder visited in Open")
			return
		if FileDialog.get_favorite_list().size() != 1:
			fail("Open and Save As did not share favorites")
			return
		save_dialog.hide()
	print("PASS file picker folder and favorites " + ("restore" if read_mode else "save"))
	scene.queue_free()
	quit(0)

func fail(message: String) -> void:
	push_error(message)
	quit(1)
