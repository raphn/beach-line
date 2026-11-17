extends SceneTree

const EDITOR_ROOT := "MainContainer/EditingContainers/PerscussionLine/ScrollContainer/InfoBeatsSplitter/"
const INFO_ROOT := EDITOR_ROOT + "Infos/TrackInfoRows"
const BEATS_ROOT := EDITOR_ROOT + "BeatsScrollContainer/Beats/TrackBeatsEditors"
const FEEDBACK_PATH := EDITOR_ROOT + "BeatsScrollContainer/Beats/BeatsGuideHeaders/SectionVisualFeedback"

func _initialize() -> void:
	call_deferred("run_check")

func run_check() -> void:
	var scene := (load("res://Interface/Interface.tscn") as PackedScene).instantiate()
	root.add_child(scene)
	await frames(3)
	if not await _open_isolated_example(scene):
		fail("Could not load the isolated drum editing example")
		return
	await frames(3)
	var lanes := scene.get_node(BEATS_ROOT) as VBoxContainer
	var feedback := scene.get_node(FEEDBACK_PATH) as Control
	if lanes.get_child_count() < 2:
		fail("The example score needs at least two drum lanes")
		return
	for lane in lanes.get_children():
		if lane is Control and (feedback.global_position.x > lane.global_position.x + 1.0 or feedback.global_position.x + feedback.size.x < lane.global_position.x + lane.size.x - 1.0):
			fail("Section feedback width does not encompass a beat row")
			return

	var first_lane := lanes.get_child(0) as HBoxContainer
	var first_measure := _first_measure(first_lane)
	var headers := feedback.get_node("VBoxContainer/MeasureHeaders") as HBoxContainer
	var first_header := headers.get_child(0) as Control
	if abs(first_header.size.x - first_measure.size.x) > 1.0 or abs(first_header.global_position.x - first_measure.global_position.x) > 1.0:
		fail("Measure header and beat group are not aligned")
		return
	var first_beat := _find_named_prefix(first_measure, "M1Beat")
	if first_beat == null:
		fail("The first measure has no editable beat")
		return
	var hit := first_beat.get_node("VBoxContainer/HBoxContainer/ControlSection/OnOffButton") as Button
	var original_hit := hit.button_pressed
	hit.button_pressed = not original_hit
	await frames(3)
	first_measure = _first_measure(lanes.get_child(0) as HBoxContainer)
	hit = _find_named_prefix(first_measure, "M1Beat").get_node("VBoxContainer/HBoxContainer/ControlSection/OnOffButton") as Button
	if hit.button_pressed == original_hit:
		fail("The beat on/off control did not update its source")
		return
	hit.button_pressed = original_hit
	await frames(3)
	first_measure = _first_measure(lanes.get_child(0) as HBoxContainer)
	first_beat = _find_named_prefix(first_measure, "M1Beat")
	var dotted := first_beat.get_node("VBoxContainer/HBoxContainer/ControlSection/HBoxContainer/DottetNoteButton") as Button
	dotted.button_pressed = true
	await frames(3)
	first_measure = _first_measure(lanes.get_child(0) as HBoxContainer)
	first_beat = _find_named_prefix(first_measure, "M1Beat")
	dotted = first_beat.get_node("VBoxContainer/HBoxContainer/ControlSection/HBoxContainer/DottetNoteButton") as Button
	var dotted_label := first_beat.get_node("VBoxContainer/BeatTempoContainer/BeatTimeValueFeedback") as Label
	if not dotted.button_pressed or dotted_label.text != "4." or abs(first_beat.size_flags_stretch_ratio - 0.375) > 0.0001:
		fail("The dotted quarter did not update its button, label and duration")
		return
	var validation := headers.get_child(0).get_node("HeaderContent/ValidationInfo") as RichTextLabel
	if not validation.text.contains("M1") or not validation.text.contains("Overfull"):
		fail("A dotted quarter in the complete bar was not marked overfull")
		return
	dotted.button_pressed = false
	await frames(3)
	first_measure = _first_measure(lanes.get_child(0) as HBoxContainer)
	first_beat = _find_named_prefix(first_measure, "M1Beat")
	if abs(first_beat.size_flags_stretch_ratio - 0.25) > 0.0001:
		fail("Turning off the dot did not restore the quarter-note duration")
		return
	var other_lane_snapshot := _lane_snapshot(lanes.get_child(1))

	# A quarter note shortened to an eighth should occupy about half the width.
	var plus := first_beat.get_node("VBoxContainer/BeatTempoContainer/BeatTimeValuePlusButton") as Button
	plus.pressed.emit()
	await frames(3)
	first_lane = lanes.get_child(0) as HBoxContainer
	first_measure = _first_measure(first_lane)
	var first_width := _find_named_prefix(first_measure, "M1Beat").size.x
	var second_beat := _find_named_prefix_after(first_measure, "M1Beat", 1)
	if second_beat == null:
		fail("The first measure needs at least two beats for the duration width check")
		return
	var second_width := second_beat.size.x
	if first_width < 76 or second_width < 76 or abs(first_width * 2.0 - second_width) > 2.0:
		fail("Beat widths do not reflect duration or the 76 px minimum (%.1f, %.1f)" % [first_width, second_width])
		return
	# Restore the quarter note so the following deletion checks have a 4/4 bar.
	var minus := _find_named_prefix(first_measure, "M1Beat").get_node("VBoxContainer/BeatTempoContainer/BeatTimeValueMinusButton") as Button
	minus.pressed.emit()
	await frames(3)

	# Delete one beat; its replacement must be visible and silent, and other lanes stay intact.
	first_lane = lanes.get_child(0) as HBoxContainer
	first_measure = _first_measure(first_lane)
	first_beat = _find_named_prefix(first_measure, "M1Beat")
	(first_beat.get_node("VBoxContainer/HBoxContainer/ControlSection/HBoxContainer/RemoveThisBeatButton") as Button).pressed.emit()
	await frames(3)
	first_lane = lanes.get_child(0) as HBoxContainer
	first_measure = _first_measure(first_lane)
	var placeholder := first_measure.get_node_or_null("M1Placeholder") as Control
	if placeholder == null or not placeholder.visible:
		fail("Deleting a beat did not create a visible M1Placeholder")
		return
	if not _placeholder_is_silent(placeholder):
		fail("The incomplete-measure placeholder exposes active sound controls")
		return
	if not _same_snapshot(other_lane_snapshot, _lane_snapshot(lanes.get_child(1))):
		fail("Deleting a beat changed another track")
		return
	(placeholder.get_node("RealizeBeatButton") as Button).pressed.emit()
	await frames(3)
	first_measure = _first_measure(lanes.get_child(0) as HBoxContainer)
	if _find_named_prefix(first_measure, "M1Beat") == null or first_measure.has_node("M1Placeholder"):
		fail("Realizing a quarter-note gap did not restore a definitive beat")
		return

	# Three removed quarter notes leave a 3/4 gap. Realization chooses a half note,
	# leaving the final quarter note represented by a placeholder.
	for index in range(3):
		first_measure = _first_measure(lanes.get_child(0) as HBoxContainer)
		var removable := _find_named_prefix(first_measure, "M1Beat")
		if removable == null:
			fail("The first measure ran out of beats during the 3/4 gap check")
			return
		(removable.get_node("VBoxContainer/HBoxContainer/ControlSection/HBoxContainer/RemoveThisBeatButton") as Button).pressed.emit()
		await frames(3)
	first_measure = _first_measure(lanes.get_child(0) as HBoxContainer)
	placeholder = first_measure.get_node_or_null("M1Placeholder") as Control
	if placeholder == null or not placeholder.visible or not _placeholder_is_silent(placeholder):
		fail("Deleting three beats did not leave a visible silent 3/4 placeholder")
		return
	(placeholder.get_node("RealizeBeatButton") as Button).pressed.emit()
	await frames(3)
	first_measure = _first_measure(lanes.get_child(0) as HBoxContainer)
	var realized := _find_named_prefix_after(first_measure, "M1Beat", 1)
	placeholder = first_measure.get_node_or_null("M1Placeholder") as Control
	var realized_rhythm := realized.get_node_or_null("VBoxContainer/BeatTempoContainer/BeatTimeValueFeedback") as Label if realized != null else null
	if realized == null or realized_rhythm == null or realized_rhythm.text != "2":
		fail("Realizing a 3/4 gap did not create the largest fitting half note")
		return
	if placeholder == null or not placeholder.visible or not _placeholder_is_silent(placeholder):
		fail("Realizing the half note did not preserve the remaining quarter-note placeholder")
		return

	# The section header and each lane must cover and align both measure columns.
	headers = feedback.get_node("VBoxContainer/MeasureHeaders") as HBoxContainer
	(headers.get_child(0).get_node("HeaderContent/AddMeasureButton") as Button).pressed.emit()
	await frames(4)
	headers = feedback.get_node("VBoxContainer/MeasureHeaders") as HBoxContainer
	if headers.get_child_count() != 2:
		fail("Adding a second measure did not update the section header")
		return
	for lane in lanes.get_children():
		if lane.get_child_count() != 2:
			fail("A track lane is missing the second measure")
			return
		for index in range(2):
			var header := headers.get_child(index) as Control
			var group := lane.get_child(index) as Control
			if abs(header.global_position.x - group.global_position.x) > 1.0 or abs(header.size.x - group.size.x) > 1.0:
				fail("A measure header no longer aligns with its beat group")
				return
		if feedback.global_position.x + feedback.size.x < lane.global_position.x + lane.size.x - 1.0:
			fail("Section feedback does not cover both measures")
			return
	var repeated_lane := lanes.get_child(1) as HBoxContainer
	var repeated_group := repeated_lane.get_child(1) as HBoxContainer
	var repeated_beat := repeated_group.get_node_or_null("M2Beat1") as Control
	if repeated_beat == null:
		fail("Expected a linked repeated beat in the second measure")
		return
	(repeated_beat.get_node("VBoxContainer/HBoxContainer/ControlSection/HBoxContainer/RemoveThisBeatButton") as Button).pressed.emit()
	await frames(4)
	repeated_lane = lanes.get_child(1) as HBoxContainer
	if not repeated_lane.get_child(0).has_node("M1Placeholder") or not repeated_lane.get_child(1).has_node("M2Placeholder"):
		fail("Removing a repeated beat did not update its source and linked repetition")
		return
	repeated_group = repeated_lane.get_child(1) as HBoxContainer
	repeated_beat = repeated_group.get_node("M2Beat1") as Control
	(repeated_beat.get_node("VBoxContainer/HBoxContainer/ControlSection/HBoxContainer/DottetNoteButton") as Button).button_pressed = true
	await frames(4)
	repeated_lane = lanes.get_child(1) as HBoxContainer
	var source_dot := repeated_lane.get_child(0).get_node("M1Beat1/VBoxContainer/HBoxContainer/ControlSection/HBoxContainer/DottetNoteButton") as Button
	var repeated_dot := repeated_lane.get_child(1).get_node("M2Beat1/VBoxContainer/HBoxContainer/ControlSection/HBoxContainer/DottetNoteButton") as Button
	if not source_dot.button_pressed or not repeated_dot.button_pressed:
		fail("Dotted note changes did not update the source and linked repetition")
		return

	# Exercise another viewport layout pass after resizing.
	root.size = Vector2i(1440, 900)
	await frames(4)
	first_measure = _first_measure(lanes.get_child(0) as HBoxContainer)
	if feedback.global_position.x + feedback.size.x < (lanes.get_child(0) as Control).global_position.x + (lanes.get_child(0) as Control).size.x - 1.0:
		fail("Section feedback no longer covers the track after resize")
		return
	for child in first_measure.get_children():
		if child is Control and child.visible and child.size.x < 76:
			fail("A beat or placeholder fell below the 76 px minimum after resize")
			return

	print("PASS beat deletion placeholders, realization, lane isolation, duration widths and resize")
	scene.queue_free()
	quit(0)

func _placeholder_is_silent(placeholder: Control) -> bool:
	var realize := placeholder.get_node_or_null("RealizeBeatButton") as Button
	var sound_toggle := placeholder.get_node_or_null("VBoxContainer/HBoxContainer/ControlSection/OnOffButton") as Button
	return realize != null and realize.visible and sound_toggle != null and (not sound_toggle.is_visible_in_tree() or sound_toggle.disabled)

func _first_measure(lane: HBoxContainer) -> HBoxContainer:
	return lane.get_child(0) as HBoxContainer if lane.get_child_count() > 0 else null

func _open_isolated_example(scene: Node) -> bool:
	var menu := scene.get_node_or_null("MainContainer/ToolsPanel/VBoxContainer/MenuBar/File") as PopupMenu
	if menu == null:
		return false
	menu.id_pressed.emit(0)
	await process_frame
	var open_dialog: FileDialog
	var discard_dialog: ConfirmationDialog
	for child in scene.get_children():
		if child is FileDialog and child.title == "Open score":
			open_dialog = child as FileDialog
		elif child is ConfirmationDialog and child.title == "Open another score?":
			discard_dialog = child as ConfirmationDialog
	if discard_dialog != null and discard_dialog.visible:
		discard_dialog.confirmed.emit()
		discard_dialog.hide()
		await frames(2)
	if open_dialog == null:
		for child in scene.get_children():
			if child is FileDialog and child.title == "Open score":
				open_dialog = child as FileDialog
	if open_dialog == null:
		return false
	var example_path := ProjectSettings.globalize_path("res://_project/examples/drum_editing_demo.otab")
	open_dialog.file_selected.emit(example_path)
	open_dialog.hide()
	return true

func _find_named_prefix(parent: Node, prefix: String) -> Control:
	for child in parent.get_children():
		if child is Control and String(child.name).begins_with(prefix):
			return child as Control
	return null

func _find_named_prefix_after(parent: Node, prefix: String, skip: int) -> Control:
	var matches: Array[Control] = []
	for child in parent.get_children():
		if child is Control and String(child.name).begins_with(prefix):
			matches.append(child as Control)
	return matches[skip] if skip < matches.size() else null

func _lane_snapshot(lane: Node) -> Array[String]:
	var snapshot: Array[String] = []
	for measure in lane.get_children():
		for cell in measure.get_children():
			if cell is Control:
				var toggle := cell.get_node_or_null("VBoxContainer/HBoxContainer/ControlSection/OnOffButton") as Button
				snapshot.append("%s:%s" % [cell.name, toggle.button_pressed if toggle != null else "placeholder"])
	return snapshot

func _same_snapshot(left: Array[String], right: Array[String]) -> bool:
	if left.size() != right.size():
		return false
	for index in range(left.size()):
		if left[index] != right[index]:
			return false
	return true

func frames(count: int) -> void:
	for index in range(count):
		await process_frame

func fail(message: String) -> void:
	push_error(message)
	quit(1)
