extends SceneTree

func _initialize() -> void:
	call_deferred("run_check")

func run_check() -> void:
	var scene := (load("res://Interface/Interface.tscn") as PackedScene).instantiate()
	var toolbar := scene.get_node_or_null("%Toolbar") as HFlowContainer
	var audio_feedback := scene.get_node_or_null("%AudioFeedback") as Label
	if toolbar == null or audio_feedback == null or audio_feedback.get_parent() != toolbar or toolbar.get_child(toolbar.get_child_count() - 1) != audio_feedback:
		fail("Audio feedback must be the last child of the toolbar")
		return
	var names := ["MenuBar", "Toolbar", "AddMidiTrackButton", "AddAudioTrackButton", "AudioFeedback", "CentralControls", "PlayPauseButton", "StopButton", "TempoEdit",
		"TapTempoButton", "MetronomeButton", "CountInButton", "CountInEveryLoopButton",
		"LoopAllButton", "LoopMeasureButton", "LoopSectionsButton",
		"Sections", "SectionsArrangement", "ScrollContainer", "Tracks", "KeyboardPopup", "GuitarFretBoardPopup"]
	var wrapper := VBoxContainer.new()
	wrapper.name = "ReparentedBindings"
	scene.add_child(wrapper)
	wrapper.owner = scene
	for node_name in names:
		var node := scene.get_node_or_null("%" + node_name)
		if node == null:
			fail("Missing unique binding: " + node_name)
			return
		node.reparent(wrapper)
		if scene.get_node_or_null("%" + node_name) != node:
			fail("Unique binding did not survive reparenting: " + node_name)
			return
	root.add_child(scene)
	await process_frame
	var play := scene.get_node("%PlayPauseButton") as Button
	var midi_add := scene.get_node("%AddMidiTrackButton") as Button
	var audio_add := scene.get_node("%AddAudioTrackButton") as Button
	var runtime_audio_feedback := scene.get_node("%AudioFeedback") as Label
	if not play.toggle_mode or midi_add.get_signal_connection_list("pressed").is_empty() or audio_add.get_signal_connection_list("pressed").is_empty():
		fail("Presenter did not initialize the reparented controls")
		return
	if scene.get_node("%Toolbar").get_node_or_null("AudioDiagnostics") != null or runtime_audio_feedback.text.is_empty():
		fail("Presenter created a duplicate audio toolbar or moved the scene-owned feedback label")
		return
	print("PASS Interface unique bindings survived reparenting and presenter initialization")
	midi_add.pressed.emit()
	var dialog := scene.get_node_or_null("TrackSettingsDialog") as ConfirmationDialog
	var preview := dialog.get_node_or_null("%InstrumentImagePreview") as TextureRect if dialog else null
	if preview == null or preview.texture == null or dialog.get_ok_button().disabled:
		fail("Add MIDI Track did not initialize the banner preview and settings")
		return
	dialog.confirmed.emit()
	await process_frame
	var rows := scene.get_node("%Tracks").get_children().filter(func(node): return str(node.name).begins_with("Track_"))
	if rows.is_empty():
		fail("Add MIDI Track did not create an editor row")
		return
	var settings := (rows.back() as Node).get_node("VBoxContainer/TrackContainer/HBoxContainer/TrackControls/VBoxContainer/Configs/TrackSettingsButton") as Button
	settings.pressed.emit()
	dialog = scene.get_node_or_null("TrackSettingsDialog") as ConfirmationDialog
	preview = dialog.get_node_or_null("%InstrumentImagePreview") as TextureRect if dialog else null
	if preview == null or preview.texture == null:
		fail("Existing MIDI Track Settings did not initialize its banner preview")
		return
	(dialog.get_node("%ChooseInstrumentImage") as Button).pressed.emit()
	var picker := dialog.get_node("%InstrumentImagePopup") as AcceptDialog
	var images := dialog.get_node("%InstrumentImages") as ItemList
	if not picker.visible or images.item_count == 0 or not images.is_anything_selected():
		fail("Existing MIDI Track Settings did not open a populated banner picker")
		return
	print("PASS Track Settings banner bindings initialize for new and existing MIDI tracks")
	scene.queue_free()
	quit(0)

func fail(message: String) -> void:
	push_error(message)
	quit(1)
