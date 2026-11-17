extends SceneTree

func _initialize() -> void:
	call_deferred("run_check")

func run_check() -> void:
	var scene := (load("res://Interface/Interface.tscn") as PackedScene).instantiate()
	root.add_child(scene)
	await process_frame
	var selector := scene.get_node("MainContainer/EditingContainers/PerscussionLine/ScrollContainer/InfoBeatsSplitter/Infos/PercussionControls/InfoControllers/SectionSelector") as OptionButton
	if selector.item_count != 1:
		push_error("A deleted section returned after restarting Godot")
		quit(1)
		return
	print("PASS working session restored after process restart")
	scene.queue_free()
	quit(0)
