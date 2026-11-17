using Godot;
using MegaCrit.Sts2.Core.Modding;

namespace Stagehand.Sts2;

[ModInitializer(nameof(Init))]
public static class Entry
{
    public static void Init()
    {
        var token = OS.GetEnvironment("STAGEHAND_TOKEN");
        if (string.IsNullOrEmpty(token))
            return;

        GD.Print("STAGEHAND_USER_DIR ", OS.GetUserDataDir());
        var tree = (SceneTree)Engine.GetMainLoop();
        tree.Connect(SceneTree.SignalName.ProcessFrame,
            Callable.From(() => Attach(tree, token)),
            (uint)GodotObject.ConnectFlags.OneShot);
    }

    private static void Attach(SceneTree tree, string token)
    {
        Node? bridge = null;
        try
        {
            var script = GD.Load<GDScript>("res://addons/stagehand/bridge.gd");
            bridge = script.New().As<Node>();
            bridge.Name = "Stagehand";
            tree.Root.AddChild(bridge);
            var error = (Error)bridge.Call("enable_builtins", new string[]
            {
                "Runtime", "SceneTree", "Object", "Screen", "Input", "Script", "PropertyRecorder"
            }).AsInt32();
            if (error != Error.Ok)
                throw new InvalidOperationException($"Stagehand builtins: {error}");

            error = (Error)bridge.Call("start", token, 0).AsInt32();
            if (error != Error.Ok)
                throw new InvalidOperationException($"Stagehand start: {error}");

            Sts2Host.Install(bridge, tree);
            GD.Print("STAGEHAND_READY ", bridge.Call("get_port").AsInt32());
        }
        catch (Exception error)
        {
            if (bridge != null && GodotObject.IsInstanceValid(bridge))
            {
                bridge.Call("stop");
                bridge.QueueFree();
            }
            GD.PrintErr("Stagehand initialization failed: ", error);
        }
    }
}
