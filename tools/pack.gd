extends SceneTree
## Resource-only pack: no replacement project settings or game assets.


func _init() -> void:
	var args := OS.get_cmdline_user_args()
	if args.size() != 3:
		push_error("Expected addon source directory, prepared files directory and output PCK")
		quit(1)
		return
	var packer := PCKPacker.new()
	var result := packer.pck_start(args[2])
	if result == OK:
		for file: String in DirAccess.get_files_at(args[0]):
			if file.ends_with(".gd"):
				result = packer.add_file(
					"res://RuntimeControl/stagehand/" + file,
					args[0].path_join(file),
				)
				if result != OK:
					break
	var files := {
		"sts2.gd": get_script().resource_path.get_base_dir().path_join("../mod/sts2.gd"),
		"client.mjs": args[1].path_join("client.mjs"),
		"api.md": args[1].path_join("docs/stagehand.md"),
		"game.md": args[1].path_join("docs/api.md"),
	}
	for name: String in files:
		if result != OK:
			break
		result = packer.add_file("res://RuntimeControl/" + name, files[name])
	if result == OK:
		result = packer.flush()
	print("Stagehand resource pack: ", error_string(result))
	quit(0 if result == OK else 1)
