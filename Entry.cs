using Godot;
using MegaCrit.Sts2.Core.Modding;

namespace Stagehand.Sts2;

[ModInitializer(nameof(Init))]
public static class Entry
{
    public static void Init()
    {
        var token = OS.GetEnvironment("STAGEHAND_TOKEN");
        if (string.IsNullOrEmpty(token)) return;
        GD.Print("STAGEHAND_USER_DIR ", OS.GetUserDataDir());
        var tree = (SceneTree)Engine.GetMainLoop();
        tree.Connect(SceneTree.SignalName.ProcessFrame,
            Callable.From(() => Attach(tree, token)),
            (uint)GodotObject.ConnectFlags.OneShot);
    }

    private static void Attach(SceneTree tree, string token)
    {
        try
        {
            var script = GD.Load<GDScript>("res://addons/stagehand/bridge.gd");
            var bridge = script.New().As<Node>();
            bridge.Name = "Stagehand";
            tree.Root.AddChild(bridge);
            bridge.Call("enable_builtins");
            var error = (Error)bridge.Call("start", token, 0).AsInt32();
            if (error != Error.Ok)
            {
                bridge.QueueFree();
                throw new InvalidOperationException($"Stagehand start: {error}");
            }
            GD.Print("STAGEHAND_READY ", bridge.Call("get_port").AsInt32());
            Sts2Host.Install(bridge, tree);
        }
        catch (Exception error)
        {
            GD.PrintErr("Stagehand initialization failed: ", error);
        }
    }
}
