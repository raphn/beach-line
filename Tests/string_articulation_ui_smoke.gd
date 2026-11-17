extends SceneTree

func _initialize() -> void:
	call_deferred("run_check")

func run_check() -> void:
	var interface := (load("res://Interface/Interface.tscn") as PackedScene).instantiate()
	root.add_child(interface)
	await process_frame
	for path in ["res://Interface/Tab/Scenes/NoteVelocityPopup.tscn", "res://Interface/Tab/Scenes/NoteExpressionCurveEditor.tscn"]:
		var packed := load(path) as PackedScene
		if packed == null:
			fail("Could not load " + path)
			return
		var window := packed.instantiate()
		interface.add_child(window)
		await process_frame
		var names := ["HammerOnButton", "PullOffButton", "ClearArticulationButton", "PreviewArticulationButton"] if path.contains("NoteVelocityPopup") else ["Graph", "Snap", "SnapPitch", "StepSegment"]
		for node_name in names:
			if window.find_child(node_name, true, false) == null:
				fail("Missing " + node_name + " in " + path)
				return
		window.queue_free()
	interface.queue_free()
	await process_frame
	print("PASS string articulation and expression editor scenes load through the interface")
	quit(0)

func fail(message: String) -> void:
	push_error(message)
	quit(1)
