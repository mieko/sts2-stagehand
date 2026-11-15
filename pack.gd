extends SceneTree
## Resource-only pack: no replacement project settings or game assets.


func _init() -> void:
	var args := OS.get_cmdline_user_args()
	if args.size() != 2:
		push_error("Expected addon source directory and output PCK")
		quit(1)
		return
	var packer := PCKPacker.new()
	var result := packer.pck_start(args[1])
	if result == OK:
		for file: String in DirAccess.get_files_at(args[0]):
			if file.ends_with(".gd"):
				result = packer.add_file("res://addons/stagehand/" + file, args[0].path_join(file))
				if result != OK:
					break
	if result == OK:
		result = packer.add_file(
			"res://stagehand_sts2/sts2.gd",
			get_script().resource_path.get_base_dir().path_join("sts2.gd"),
		)
	if result == OK:
		result = packer.flush()
	print("Stagehand resource pack: ", error_string(result))
	quit(0 if result == OK else 1)
