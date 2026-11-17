extends SceneTree

func _initialize() -> void:
	call_deferred("run_check")

func run_check() -> void:
	var scene := (load("res://Interface/Interface.tscn") as PackedScene).instantiate()
	root.add_child(scene)
	await process_frame
	var line := scene.get_node("MainContainer/EditingContainers/PerscussionLine") as FoldableContainer
	if line == null:
		fail("The renamed percussion line node is missing")
		return
	var editing := scene.get_node("MainContainer/EditingContainers") as VSplitContainer
	line.folded = true
	await process_frame
	if not editing.collapsed:
		fail("Folding the percussion line did not collapse the editing split")
		return
	line.folded = false
	await process_frame
	if editing.collapsed:
		fail("Unfolding the percussion line left the editing split collapsed")
		return
	var info := "MainContainer/EditingContainers/PerscussionLine/ScrollContainer/InfoBeatsSplitter/Infos/"
	var controls := scene.get_node(info + "PercussionControls") as Control
	var selector := scene.get_node(info + "PercussionControls/InfoControllers/SectionSelector") as OptionButton
	var delete_button := scene.get_node(info + "PercussionControls/InfoControllers/DeleteSectionButton") as Button
	var master_volume := scene.get_node(info + "PercussionControls/PercussionMasterVolume/PercussionVolumeMaster") as HSlider
	var master_volume_label := scene.get_node(info + "PercussionControls/PercussionMasterVolume/Label") as Label
	if controls == null or master_volume == null or master_volume_label == null:
		fail("A percussion controls or master volume node is missing")
		return
	if master_volume.min_value != -80 or master_volume.max_value != 20 or master_volume.step != 1 or master_volume.value != 0:
		fail("Percussion master volume did not initialize with the expected range and 0 dB value")
		return
	if master_volume_label.text != "Master Vol.: 0 dB":
		fail("Percussion master volume feedback did not initialize in decibels")
		return
	master_volume.value = -18
	if master_volume_label.text != "Master Vol.: -18 dB":
		fail("Percussion master volume feedback did not update in decibels")
		return
	selector.item_selected.emit(1)
	selector.item_selected.emit(0)
	await process_frame
	if master_volume.value != -18 or master_volume_label.text != "Master Vol.: -18 dB":
		fail("Percussion master volume did not persist after section rerendering")
		return
	var dialog := scene.get_node("DeleteSectionConfirmation") as ConfirmationDialog
	if selector.item_count != 2:
		fail("Demo sections were not initialized")
		return
	var track_rows := scene.get_node(info + "TrackInfoRows") as VBoxContainer
	if track_rows.get_child_count() != 3:
		fail("The drum track rows did not all initialize")
		return
	var first_row := track_rows.get_child(0)
	var volume := first_row.get_node_or_null("Row/AudioControls/TrackVolume") as HSlider
	var mute := first_row.get_node_or_null("Row/AudioControls/LowerLayout/MuteButton") as Button
	var solo := first_row.get_node_or_null("Row/AudioControls/LowerLayout/SoloButton") as Button
	var volume_label := first_row.get_node_or_null("Row/AudioControls/LowerLayout/VolumeFeedbackLabel") as Label
	if volume == null or mute == null or solo == null:
		fail("A drum track audio control is missing")
		return
	volume.value = -12
	if volume_label == null or volume_label.text != "-12 dB":
		fail("Track volume feedback did not update in decibels")
		return
	mute.button_pressed = true
	solo.button_pressed = true
	selector.item_selected.emit(1)
	selector.item_selected.emit(0)
	await process_frame
	first_row = track_rows.get_child(0)
	volume = first_row.get_node("Row/AudioControls/TrackVolume") as HSlider
	mute = first_row.get_node("Row/AudioControls/LowerLayout/MuteButton") as Button
	solo = first_row.get_node("Row/AudioControls/LowerLayout/SoloButton") as Button
	if volume.value != -12 or not mute.button_pressed or not solo.button_pressed:
		fail("Track audio controls did not retain their edited values")
		return
	volume_label = first_row.get_node("Row/AudioControls/LowerLayout/VolumeFeedbackLabel") as Label
	if volume_label.text != "-12 dB":
		fail("Track volume feedback did not initialize after rerendering")
		return
	delete_button.pressed.emit()
	if not dialog.visible or selector.item_count != 2:
		fail("Opening the confirmation changed the sections")
		return
	dialog.hide()
	await process_frame
	if selector.item_count != 2:
		fail("Closing the confirmation changed the sections")
		return
	delete_button.pressed.emit()
	dialog.confirmed.emit()
	await process_frame
	if selector.item_count != 1:
		fail("Confirmed section deletion did not update the selector")
		return
	delete_button.pressed.emit()
	dialog.confirmed.emit()
	await process_frame
	var add_measure := scene.get_node(info + "PercussionControls/InfoControllers/AddFirstMeasureButton") as Button
	if selector.item_count != 1 or not add_measure.visible:
		fail("Deleting the last section did not leave an empty default")
		return
	add_measure.pressed.emit()
	await process_frame
	if add_measure.visible:
		fail("The default section did not accept a measure")
		return
	var rows := scene.get_node(info + "TrackInfoRows") as VBoxContainer
	var set_lead := rows.get_child(1).get_node("Row/AudioControls/SectionControls/LeadButton") as Button
	set_lead.pressed.emit()
	await process_frame
	var selected_lead := rows.get_child(1).get_node("Row/AudioControls/SectionControls/LeadButton") as Button
	if not selected_lead.disabled or selected_lead.text != "Lead":
		fail("Selecting a lead track did not update the editor")
		return
	print("PASS section deletion, empty replacement, measure creation and lead selection")
	scene.queue_free()
	quit(0)

func fail(message: String) -> void:
	push_error(message)
	quit(1)
