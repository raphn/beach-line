extends SceneTree

const WORKSPACE_SCROLL_PATH := "MainContainer/CentralControls/NonPercussionTracks/ScrollContainer"
const TRACKS_PATH := WORKSPACE_SCROLL_PATH + "/Tracks"
const MIN_MEASURE_WIDTH := 180.0

func _initialize() -> void:
	call_deferred("run_check")

func run_check() -> void:
	root.size = Vector2i(1920, 1080)
	root.content_scale_size = Vector2i(1920, 1080)
	var scene := (load("res://Interface/Interface.tscn") as PackedScene).instantiate()
	root.add_child(scene)
	await frames(6)
	var tracks := scene.get_node(TRACKS_PATH) as VBoxContainer
	var scroll := scene.get_node(WORKSPACE_SCROLL_PATH) as ScrollContainer
	var add_guitar := _button_with_text(tracks, "+ Guitar")
	var add_kit := _button_with_text(tracks, "+ Percussion kit")
	if add_guitar == null or add_kit == null:
		_fail("Track creation controls were not rendered")
		return
	add_guitar.pressed.emit()
	await frames(4)
	var guitar := _track_row(tracks, "Guitar")
	if guitar == null:
		_fail("The guitar track did not render")
		return
	add_kit = _button_with_text(tracks, "+ Percussion kit")
	if add_kit == null:
		_fail("The percussion kit creation control disappeared")
		return
	add_kit.pressed.emit()
	await frames(4)
	var kit := _track_row(tracks, "Percussion kit")
	if kit == null:
		_fail("The percussion kit did not render")
		return

	# Grow the current section to five bars using its real measure-header action.
	# The narrow case stays above the existing toolbar's minimum width.
	for _index in range(4):
		guitar = _track_row(tracks, "Guitar")
		var add_measure := _first_add_measure(guitar)
		if add_measure == null:
			_fail("The guitar section has no add-measure control")
			return
		add_measure.pressed.emit()
		await frames(4)

	guitar = _track_row(tracks, "Guitar")
	kit = _track_row(tracks, "Percussion kit")
	if not _check_track_alignment(guitar, 5) or not _check_track_alignment(kit, 2):
		_fail("Tablature or kit measure headers no longer align with their lane columns")
		return
	if _has_horizontal_overflow(scroll):
		_debug_layout(scroll, tracks, guitar, kit)
		_fail("Five bars should fit in the 1920 px viewport")
		return
	var wide_measure_width := _first_measure_width(guitar)
	if wide_measure_width < MIN_MEASURE_WIDTH:
		_fail("A wide layout rendered a tablature measure below the 180 px minimum")
		return

	# A narrow viewport should preserve usable columns and expose the existing scroll fallback.
	root.size = Vector2i(1280, 800)
	root.content_scale_size = Vector2i(1280, 800)
	await frames(8)
	guitar = _track_row(tracks, "Guitar")
	kit = _track_row(tracks, "Percussion kit")
	if not _check_track_alignment(guitar, 5) or not _check_track_alignment(kit, 2):
		_debug_layout(scroll, tracks, guitar, kit)
		_fail("Measure headers and lane columns diverged after narrowing the viewport")
		return
	if not _has_horizontal_overflow(scroll):
		_debug_layout(scroll, tracks, guitar, kit)
		_fail("The narrow viewport did not expose horizontal scrolling for content that exceeds the available width")
		return
	var narrow_measure_width := _first_measure_width(guitar)
	if narrow_measure_width < MIN_MEASURE_WIDTH or narrow_measure_width >= wide_measure_width:
		_fail("Responsive measure widths did not shrink to the 180 px floor (wide %.1f, narrow %.1f)" % [wide_measure_width, narrow_measure_width])
		return

	# At a smaller logical width, the top toolbar must wrap instead of forcing
	# the entire workspace past the viewport; dense sections still scroll.
	root.size = Vector2i(900, 700)
	root.content_scale_size = Vector2i(900, 700)
	await frames(12)
	guitar = _track_row(tracks, "Guitar")
	kit = _track_row(tracks, "Percussion kit")
	if scroll.size.x > 900.0 or not _has_horizontal_overflow(scroll) or not _check_track_alignment(guitar, 5) or not _check_track_alignment(kit, 2):
		_debug_layout(scroll, tracks, guitar, kit)
		_fail("Small workspace did not wrap and retain aligned scrollable measures")
		return

	# Resizing back must recalculate the fit and retain the aligned header columns.
	root.size = Vector2i(1920, 1080)
	root.content_scale_size = Vector2i(1920, 1080)
	await frames(8)
	guitar = _track_row(tracks, "Guitar")
	kit = _track_row(tracks, "Percussion kit")
	if not _check_track_alignment(guitar, 5) or not _check_track_alignment(kit, 2):
		_fail("Measure alignment was not restored after widening the viewport")
		return
	if _has_horizontal_overflow(scroll):
		_fail("The wide layout did not recover its multi-bar fit after resize")
		return
	print("PASS responsive section fit, overflow fallback, resize and tablature/kit alignment")
	scene.queue_free()
	quit(0)

func _check_track_alignment(row: Control, expected_bars: int) -> bool:
	if row == null:
		return false
	var section := _first_section(row)
	if section == null:
		return false
	var measures := section.get_node_or_null("VBoxContainer/Measures") as HBoxContainer
	if measures == null or measures.get_child_count() != expected_bars:
		return false
	for index in range(expected_bars):
		var measure := measures.get_child(index) as Control
		if measure is not Button:
			var header := measure.get_node_or_null("MeasureHeader") as Control
			var content := measure.get_node_or_null("Content") as Control
			if header == null or content == null or content.get_node_or_null("MeasureTabView") == null:
				return false
			if abs(header.global_position.x - content.global_position.x) > 1.0 or abs(header.size.x - content.size.x) > 1.0:
				return false
		if measure.size.x < MIN_MEASURE_WIDTH - 1.0:
			return false
	return true

func _first_measure_width(row: Control) -> float:
	var section := _first_section(row)
	if section == null:
		return 0.0
	var measures := section.get_node_or_null("VBoxContainer/Measures") as HBoxContainer
	return (measures.get_child(0) as Control).size.x if measures != null and measures.get_child_count() > 0 else 0.0

func _first_section(row: Control) -> Control:
	if row == null:
		return null
	return _find_section_with_measure_rows(row)

func _find_section_with_measure_rows(parent: Node) -> Control:
	for child in parent.get_children():
		if child is Control and child.get_node_or_null("VBoxContainer/Measures") is HBoxContainer:
			return child as Control
		var nested := _find_section_with_measure_rows(child)
		if nested != null:
			return nested
	return null

func _first_add_measure(row: Control) -> Button:
	var section := _first_section(row)
	if section == null:
		return null
	var measures := section.get_node_or_null("VBoxContainer/Measures") as HBoxContainer
	if measures == null or measures.get_child_count() == 0:
		return null
	return measures.get_child(0).get_node_or_null("MeasureHeader/HeaderContent/AddMeasureButton") as Button

func _button_with_text(parent: Node, label: String) -> Button:
	for child in parent.get_children():
		if child is Button and (child as Button).text == label:
			return child as Button
		var nested := _button_with_text(child, label)
		if nested != null:
			return nested
	return null

func _track_row(tracks: VBoxContainer, title: String) -> Control:
	for child in tracks.get_children():
		if child is FoldableContainer and (child as FoldableContainer).title == title:
			return child as Control
	return null

func _has_horizontal_overflow(scroll: ScrollContainer) -> bool:
	var bar := scroll.get_h_scroll_bar()
	return bar.max_value > bar.page + 1.0

func _debug_layout(scroll: ScrollContainer, tracks: VBoxContainer, guitar: Control, kit: Control) -> void:
	var bar := scroll.get_h_scroll_bar()
	var scene := scroll.get_parent().get_parent().get_parent().get_parent() as Control
	for path in [".", "MainContainer", "MainContainer/ToolsPanel", "MainContainer/CentralControls", "MainContainer/CentralControls/NonPercussionTracks", WORKSPACE_SCROLL_PATH, TRACKS_PATH]:
		var node := scene.get_node(path) if path != "." else scene
		if node is Control:
			var control := node as Control
			print("narrow node", path, "min", control.get_combined_minimum_size(), "size", control.size)
	print("window", root.size, "visible rect", root.get_visible_rect(), "scene size", scene.size, "scroll size", scroll.size, "bar max/page", bar.max_value, bar.page, "tracks min/size", tracks.get_combined_minimum_size().x, tracks.size.x)
	for row in [guitar, kit]:
		var section := _first_section(row)
		var measures := section.get_node("VBoxContainer/Measures") as HBoxContainer
		print("row", row.title, "size/min", row.size.x, row.get_combined_minimum_size().x, "section", section.size.x)
		for measure in measures.get_children():
			print(" measure", measure.name, (measure as Control).size.x)

func frames(count: int) -> void:
	for _index in range(count):
		await process_frame

func _fail(message: String) -> void:
	push_error(message)
	quit(1)
