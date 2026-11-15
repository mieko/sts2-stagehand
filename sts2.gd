extends Node
## Adapter-local callbacks expose plain managed game models to the generic bridge.

var read_callback: Callable
var request_callback: Callable
var inspect_callback: Callable
var select_card_callback: Callable
var frame_callback: Callable
var _bridge: Node
var _handle := ""


func configure(bridge: Node) -> String:
	_bridge = bridge
	_handle = bridge.register_object(
		self,
		"Sts2",
		"Sts2",
		{ "request": request, "state": state, "inspect": inspect_state, "select_card": select_card },
		observe,
		["changed"],
	)
	return _handle


func observe() -> Variant:
	return _decode(read_callback.call())


func state(_params: Dictionary) -> Variant:
	return observe()


func request(params: Dictionary) -> Variant:
	return _decode(request_callback.call(JSON.stringify(params)))


func select_card(params: Dictionary) -> Variant:
	return _decode(select_card_callback.call(JSON.stringify(params)))


func inspect_state(params: Dictionary) -> Variant:
	return _decode(inspect_callback.call(JSON.stringify(params)))


func _decode(text: String) -> Variant:
	var value: Variant = JSON.parse_string(text)
	if value is Dictionary and value.has("error"):
		return _bridge.fail(-32040, str(value.error))
	return value


func _process(_delta: float) -> void:
	if frame_callback.is_valid() and not _handle.is_empty():
		var revision: String = frame_callback.call()
		if not revision.is_empty():
			_bridge.publish(_handle, "changed", { "revision": revision })
