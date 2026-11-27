extends SceneTree

const KNOB_CONTROLS := "EffectPedalKnob"
const PEDAL_ART := "res://Interface/EffectAndPedals/Graph/"

func _initialize() -> void:
	call_deferred("run_check")

func run_check() -> void:
	var scene := (load("res://Interface/Interface.tscn") as PackedScene).instantiate()
	root.add_child(scene)
	scene.get_node("%AddMidiTrackButton").pressed.emit()
	var settings := scene.get_node("TrackSettingsDialog") as ConfirmationDialog
	settings.confirmed.emit()
	await process_frame
	var rows := scene.get_node("%Tracks").get_children().filter(func(node): return str(node.name).begins_with("Track_"))
	var shelf := (rows.back() as Node).get_node("VBoxContainer/TrackPedals")
	var menu := (shelf.get_node("%AddPedalButton") as MenuButton).get_popup()
	var pedals := shelf.get_node("%Pedals")
	if menu.item_count != 3:
		fail("The add menu must list all three catalog effects.")
		return

	var types := ["overdrive", "chorus", "delay"]
	var first_defaults := [0.40, 0.35, 0.35]
	for i in types.size():
		menu.id_pressed.emit(menu.get_item_id(i))
		var pedal := pedals.get_child(i)
		var artwork := pedal.get_node("TextureRect") as TextureRect
		var controls := pedal.get_node("BoxContainer/Knobs")
		if artwork.texture == null or artwork.texture.resource_path != PEDAL_ART + types[i] + ".jpg":
			fail("Incorrect artwork for " + types[i])
			return
		if controls.get_child_count() != 3 or not controls.get_child(0) is PanelContainer:
			fail("Missing PanelContainer knobs for " + types[i])
			return
		var slider := controls.get_child(0).get_node(KNOB_CONTROLS + "/ValueSlider") as HSlider
		if not is_equal_approx(slider.value, first_defaults[i]):
			fail("Incorrect default parameter for " + types[i])
			return

	var overdrive := pedals.get_child(0)
	var automation := overdrive.get_node("BoxContainer/Knobs").get_child(0).get_node(KNOB_CONTROLS + "/Automation") as Button
	automation.pressed.emit()
	var dialog := shelf.get_node("PedalAutomationDialog") as ConfirmationDialog
	dialog.get_node("%AddPoint").pressed.emit()
	dialog.confirmed.emit()
	await process_frame

	var presets := overdrive.get_node("BoxContainer/Preset") as OptionButton
	if presets.item_count != 6 or presets.get_item_text(3) != "Blues crunch":
		fail("The original overdrive presets are missing.")
		return
	presets.item_selected.emit(3)
	overdrive = pedals.get_child(0)
	var drive := overdrive.get_node("BoxContainer/Knobs").get_child(0)
	var drive_slider := drive.get_node(KNOB_CONTROLS + "/ValueSlider") as HSlider
	automation = drive.get_node(KNOB_CONTROLS + "/Automation") as Button
	if not is_equal_approx(drive_slider.value, 0.35) or automation.text != "A•":
		fail("Applying the preset must update the knob and preserve its keyframe.")
		return
	automation.pressed.emit()
	dialog = shelf.get_node("PedalAutomationDialog") as ConfirmationDialog
	var points := dialog.get_node("%Rows")
	if points.get_child_count() != 1 or not is_equal_approx((points.get_child(0).get_child(1) as SpinBox).value, 40.0):
		fail("Applying an overdrive preset changed the existing automation value.")
		return
	dialog.canceled.emit()

	print("PASS pedal catalog, artwork, PanelContainer knobs, defaults, and presets preserving automation")
	scene.queue_free()
	await process_frame
	quit()

func fail(message: String) -> void:
	push_error(message)
	quit(1)
