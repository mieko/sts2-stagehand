extends SceneTree
## Check the mounted resources in an empty project before producing install artifacts.


func _initialize() -> void:
	var args := OS.get_cmdline_user_args()
	if args.size() != 2 or not ProjectSettings.load_resource_pack(args[0]):
		fail("Could not load the RuntimeControl resource pack")
		return

	check_bridge.call_deferred(args[1])


func check_bridge(artifacts: String) -> void:
	var script := load("res://RuntimeControl/stagehand/bridge.gd") as GDScript
	var adapter := load("res://RuntimeControl/sts2.gd") as GDScript
	if script == null or not script.can_instantiate() or adapter == null or not adapter.can_instantiate():
		fail("Could not load the packaged bridge and Sts2 adapter")
		return

	var bridge := script.new() as Node
	bridge.set("app_name", "Slay the Spire 2")
	root.add_child(bridge)
	var result: int = bridge.serve_client(
		"res://RuntimeControl/game.md",
		"res://RuntimeControl/client.mjs",
		"res://RuntimeControl/api.md",
	)
	if result != OK:
		fail("Could not load packaged Stagehand discovery assets: " + error_string(result))
		return

	var host := adapter.new() as Node
	bridge.add_child(host)
	if host.configure(bridge).is_empty():
		fail("Could not register the packaged Sts2 adapter")
		return

	result = bridge.start("build-smoke", 0, "localhost")
	if result != OK:
		fail("Packaged Stagehand bridge must support localhost: " + error_string(result))
		return

	var address := "http://127.0.0.1:%d" % bridge.get_port()
	var request := HTTPRequest.new()
	request.timeout = 3.0
	root.add_child(request)
	if request.request(address + "/") != OK:
		fail("Could not request Stagehand discovery")
		return

	var discovery: Array = await request.request_completed
	var page := (discovery[3] as PackedByteArray).get_string_from_utf8()
	if discovery[0] != HTTPRequest.RESULT_SUCCESS or discovery[1] != 200 or "Slay the Spire 2" not in page:
		fail("Discovery did not identify Slay the Spire 2")
		return

	var routes := {
		"/client.mjs": ["client.mjs", "text/javascript"],
		"/api.md": ["docs/stagehand.md", "text/markdown"],
		"/game.md": ["docs/api.md", "text/markdown"],
	}
	for route: String in routes:
		if ("." + route) not in page or request.request(address + route) != OK:
			fail("Could not discover or request " + route)
			return

		var response: Array = await request.request_completed
		var headers := "\n".join(response[2]).to_lower()
		var expected := FileAccess.get_file_as_bytes(artifacts.path_join(routes[route][0]))
		if (
			response[0] != HTTPRequest.RESULT_SUCCESS or response[1] != 200
			or "content-type: " + routes[route][1] not in headers
			or expected.is_empty() or response[3] != expected
		):
			fail("Mounted asset does not match its build artifact: " + route)
			return

	var port: int = bridge.get_port()
	bridge.stop()
	result = bridge.start_local(port)
	if result != OK:
		fail("Could not restart the packaged bridge without a token: " + error_string(result))
		return

	print("Stagehand resource pack: Sts2 registration, discovery assets and listener restart passed")
	quit()


func fail(message: String) -> void:
	push_error(message)
	quit(1)
