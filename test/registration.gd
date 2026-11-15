extends SceneTree


class Registry extends Node:
	var operations: Dictionary


	func register_object(_target, label, kind, methods, _observer, _events) -> String:
		assert(label == "Sts2" and kind == "Sts2")
		operations = methods
		return "fixture:sts2"


	func fail(code, message) -> Dictionary:
		return { "code": code, "message": message }


var messages: Array = []


func callback(message: String) -> String:
	messages.append(JSON.parse_string(message))
	return JSON.stringify({ "operation": "fixture:operation", "status": "accepted" })


func _initialize() -> void:
	var registry := Registry.new()
	var adapter = load("res://sts2.gd").new()
	adapter.workflow_callback = callback
	assert(adapter.configure(registry) == "fixture:sts2")
	var accepted = registry.operations.operate.call(
		{ "kind": "playCards", "plays": ["Strike"], "pacingMs": 400.0 }
	)
	assert(accepted.operation == "fixture:operation")
	assert(messages[0].command == "start" and messages[0].args.kind == "playCards")
	assert(messages[0].args.pacingMs == 400.0)

	registry.operations.operation.call({ "operation": "fixture:operation" })
	assert(messages[1] == { "command": "status", "operation": "fixture:operation" })
	# Even malformed status parameters cannot select the mutation callback branch.
	registry.operations.operation.call({ "kind": "playCards", "plays": ["Strike"] })
	assert(messages[2] == { "command": "status", "operation": "" })
	adapter.free()
	registry.free()
	print(
		"StS registration: typed discovery, operation acceptance and read-only status routing passed"
	)
	quit()
