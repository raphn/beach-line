extends SceneTree

# Run with isolated Godot user data: this exercises the real language selector.
func _initialize() -> void:
	call_deferred("run_check")

func run_check() -> void:
	var scene := (load("res://Interface/Interface.tscn") as PackedScene).instantiate()
	root.add_child(scene)
	await process_frame
	var settings := scene.find_child("AppSettingsDialog", true, false) as Window
	if settings == null:
		fail("Settings dialog was not created")
		return
	var locale := settings.find_child("Locale", true, false) as OptionButton
	var initial := locale.selected
	locale.item_selected.emit(1)
	await process_frame
	if not TranslationServer.get_locale().begins_with("pt") or settings.title != "Configurações":
		fail("Selecting Brazilian Portuguese did not refresh the settings title")
		return
	if TranslationServer.translate("Delete this measure") != "Excluir este compasso":
		fail("Scene tooltip translation is missing")
		return
	if TranslationServer.translate("Set transition rule") != "Definir regra de transição":
		fail("Measure version controls are not translated")
		return
	if TranslationServer.translate("{0} · M{1}").format(["A", 2]) != "A · M2":
		fail("Measure header context is not translated")
		return
	locale.item_selected.emit(0)
	await process_frame
	if TranslationServer.get_locale() != "en" or settings.title != "Settings":
		fail("Switching back to English did not refresh the settings title")
		return
	locale.item_selected.emit(initial)
	print("PASS language switching and scene catalog")
	scene.queue_free()
	quit(0)

func fail(message: String) -> void:
	push_error(message)
	quit(1)
