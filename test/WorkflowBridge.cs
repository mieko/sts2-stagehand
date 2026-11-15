using System.Diagnostics;
using System.Text.Json.Nodes;
using Stagehand.Sts2;

// Test-only JSON-lines driver. JS supplies fake native ports; the workflow is
// the production managed implementation, not a second JS implementation.
var sessions = new Dictionary<string, WorkflowService>();
var clock = Stopwatch.StartNew();
while (Console.ReadLine() is { } line)
{
    try
    {
        var command = JsonNode.Parse(line)!.AsObject();
        var session = command["session"]?.GetValue<string>() ?? "";
        JsonNode? result;
        switch (command["method"]!.GetValue<string>())
        {
            case "start":
                if (!sessions.TryGetValue(session, out var service))
                    sessions[session] = service = new WorkflowService(
                        () => Call(session, "read", null).AsObject(),
                        args => Call(session, "request", args).AsObject(),
                        choice => Call(session, "activate", choice),
                        owner => Call(session, "browse", JsonValue.Create(owner)),
                        () => clock.ElapsedMilliseconds);
                result = service.Start(command["args"]!.AsObject());
                break;
            case "tick":
                foreach (var active in sessions.Values.Where(s => s.Busy).ToArray())
                    active.Advance();
                result = new JsonObject(sessions.Select(pair => KeyValuePair.Create(pair.Key,
                    JsonSerializerNode(pair.Value.Summary))));
                break;
            case "status":
                result = sessions[session].Status(command["operation"]?.GetValue<string>() ?? "");
                break;
            case "observe":
                result = sessions.TryGetValue(session, out var observed) ? observed.Observe(command["state"]!.AsObject()) : null;
                break;
            default:
                throw new InvalidOperationException("Unknown test command");
        }
        Console.WriteLine(new JsonObject { ["result"] = result }.ToJsonString());
    }
    catch (Exception error)
    {
        Console.WriteLine(new JsonObject { ["error"] = error.GetBaseException().Message }.ToJsonString());
    }
}

static JsonNode? JsonSerializerNode(object? value) => System.Text.Json.JsonSerializer.SerializeToNode(value,
    new System.Text.Json.JsonSerializerOptions { PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase });

static JsonNode Call(string session, string method, JsonNode? args)
{
    Console.WriteLine(new JsonObject { ["call"] = method, ["session"] = session, ["args"] = args?.DeepClone() }.ToJsonString());
    var response = JsonNode.Parse(Console.ReadLine() ?? throw new EndOfStreamException())!;
    if (response["error"] != null)
    {
        if (response["activationRejected"]?.GetValue<bool>() == true)
            throw new WorkflowService.ActivationRejected(response["error"]!.GetValue<string>());
        throw new InvalidOperationException(response["error"]!.GetValue<string>());
    }
    return response["result"]?.DeepClone() ?? new JsonObject();
}
