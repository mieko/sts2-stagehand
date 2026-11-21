using Godot;
using MegaCrit.Sts2.Core.Modding;
using System.Reflection;
using System.Text.Json;

namespace Stagehand.Sts2;

[ModInitializer(nameof(Init))]
public static class Entry
{
    private static readonly string LaunchToken = ConnectionSettings.NewToken();
    private static readonly string[] DeveloperBuiltins =
        ["Runtime", "SceneTree", "Object", "Screen", "Input", "Script", "PropertyRecorder"];

    private static ConnectionSettings _settings = new();
    private static Session? _session;
    private static string? _failure;

    private static string DataDirectory => System.IO.Path.Combine(OS.GetUserDataDir(), "RuntimeControl");
    private static string SettingsFile => System.IO.Path.Combine(DataDirectory, "settings.json");
    private static string ConnectionFile => System.IO.Path.Combine(DataDirectory, "connection.json");
    public static object? Information => _session is { } session ? new
    {
        Version = typeof(Entry).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion,
        DeveloperMode = session.Settings.DeveloperMode,
        session.Details.Resources
    } : null;

    private sealed record Session(Node Bridge, Sts2Host Host, ConnectionSettings Settings, int Port, ConnectionDetails Details);

    public static void Init()
    {
        var tree = (SceneTree)Engine.GetMainLoop();
        StagehandSettingsPanel.Install(tree, () => _settings, settings => Apply(tree, settings),
            DescribeConnection, CopyConnectionDetails);
        tree.Connect(SceneTree.SignalName.ProcessFrame, Callable.From(() => Initialize(tree)),
            (uint)GodotObject.ConnectFlags.OneShot);
    }

    private static void Initialize(SceneTree tree)
    {
        try
        {
            System.IO.Directory.CreateDirectory(DataDirectory);
            System.IO.File.Delete(ConnectionFile);
            _settings = ConnectionSettings.Load(SettingsFile);
            var effective = EffectiveSettings(_settings, out _);
            if (effective.Enabled)
                ChangeSession(tree, effective);
        }
        catch (Exception error)
        {
            _failure = error.GetBaseException().Message;
            GD.PushWarning("Stagehand initialization failed: " + _failure);
        }
    }

    private static string Apply(SceneTree tree, ConnectionSettings settings)
    {
        try
        {
            settings.Validate();
            var effective = EffectiveSettings(settings, out var overridden);
            var unchanged = _session?.Settings == effective || (_session == null && !effective.Enabled);
            if (unchanged)
            {
                if (settings != _settings)
                    settings.Save(SettingsFile);
            }
            else
            {
                if (_session?.Host.ActionInProgress == true)
                    return "Finish the current gameplay action before applying changes.";

                ChangeSession(tree, effective, settings);
            }

            _settings = settings;
            _failure = null;
            return overridden ? "Settings saved. Launch overrides are still active." : "Settings applied.";
        }
        catch (Exception error)
        {
            return "Could not apply settings: " + error.Message;
        }
    }

    private static void ChangeSession(SceneTree tree, ConnectionSettings effective, ConnectionSettings? save = null)
    {
        var previous = _session;
        Session? next = null;
        previous?.Bridge.Call("stop");
        try
        {
            if (effective.Enabled)
                next = Start(tree, effective);

            PublishConnection(next);
            save?.Save(SettingsFile);
        }
        catch
        {
            Close(next?.Bridge);
            Restore(previous);
            throw;
        }

        _session = next;
        previous?.Bridge.QueueFree();
        if (next != null)
        {
            GD.Print("STAGEHAND_READY ", next.Port);
            GD.Print("STAGEHAND_CONNECTION ", ConnectionFile);
        }
    }

    private static void Restore(Session? previous)
    {
        try
        {
            if (previous != null)
                Listen(previous.Bridge, previous.Settings.Address, previous.Port, previous.Details.Token);

            PublishConnection(previous);
        }
        catch (Exception error)
        {
            Close(previous?.Bridge);
            _session = null;
            _failure = error.GetBaseException().Message;
            GD.PushWarning("Could not restore the previous Stagehand connection: " + _failure);
            throw new InvalidOperationException("The previous Stagehand connection could not be restored.", error);
        }
    }

    private static Session Start(SceneTree tree, ConnectionSettings settings)
    {
        Node? bridge = null;
        try
        {
            bridge = GD.Load<GDScript>("res://RuntimeControl/stagehand/bridge.gd").New().As<Node>();
            bridge.Name = "Stagehand";
            bridge.Set("app_name", "Slay the Spire 2");
            tree.Root.AddChild(bridge);
            Check((Error)bridge.Call("serve_client", "res://RuntimeControl/game.md",
                "res://RuntimeControl/client.mjs", "res://RuntimeControl/api.md").AsInt32(), "discovery assets");
            Check((Error)bridge.Call("enable_builtins", settings.DeveloperMode
                ? DeveloperBuiltins : Array.Empty<string>()).AsInt32(), "builtins");
            var host = Sts2Host.Install(bridge, tree);
            var token = settings.RequireAccessToken
                ? string.IsNullOrEmpty(settings.AccessToken) ? LaunchToken : settings.AccessToken
                : null;

            Listen(bridge, settings.Address, settings.Port, token);

            var port = bridge.Call("get_port").AsInt32();
            var url = ConnectionUrl(settings.Address, port);
            var details = new ConnectionDetails(url, settings.RequireAccessToken, token,
                new Resources(url + "/api.md", url + "/game.md", url + "/client.mjs"));
            return new Session(bridge, host, settings, port, details);
        }
        catch
        {
            Close(bridge);
            throw;
        }
    }

    private static void Listen(Node bridge, string address, int port, string? token)
    {
        var result = token == null ? bridge.Call("start_local", port) : bridge.Call("start", token, port, address);
        Check((Error)result.AsInt32(), "listener");
    }

    private static void Close(Node? bridge)
    {
        if (bridge == null || !GodotObject.IsInstanceValid(bridge))
            return;

        bridge.Call("stop");
        bridge.QueueFree();
    }

    private static ConnectionSettings EffectiveSettings(ConnectionSettings result, out bool overridden)
    {
        var saved = result;
        if (OS.HasEnvironment("STAGEHAND_PORT"))
            result = result with
            {
                Port = int.Parse(OS.GetEnvironment("STAGEHAND_PORT"))
            };

        if (OS.HasEnvironment("STAGEHAND_LISTEN"))
            result = result with
            {
                ListenAddress = OS.GetEnvironment("STAGEHAND_LISTEN")
            };

        if (OS.HasEnvironment("STAGEHAND_DEVELOPER"))
            result = result with
            {
                DeveloperMode = bool.Parse(OS.GetEnvironment("STAGEHAND_DEVELOPER"))
            };

        if (OS.HasEnvironment("STAGEHAND_TOKEN"))
        {
            var token = OS.GetEnvironment("STAGEHAND_TOKEN");
            result = result with { RequireAccessToken = token.Length > 0, AccessToken = token };
        }

        overridden = result != saved;
        result.Validate();
        return result.RequireAccessToken && !string.IsNullOrEmpty(result.AccessToken)
            ? result : result with { AccessToken = null };
    }

    private sealed record Resources(string ApiDocs, string GameDocs, string JsEntrypoint);
    private sealed record ConnectionDetails(string Url, bool RequireAccessToken, string? Token, Resources Resources)
    {
        public string ForClipboard()
        {
            if (Token == null)
                return Url;

            return "Connection information for Slay the Spire 2 over Stagehand:\n" +
                $"Stagehand Address: {Url}\n" +
                $"JS Client: {Resources.JsEntrypoint}\nStagehand API: {Resources.ApiDocs}\n" +
                $"Slay the Spire 2 API: {Resources.GameDocs}\nAccess Token: {Token}";
        }
    }

    private static string ConnectionUrl(string address, int port)
    {
        var host = address switch
        {
            "localhost" or "*" => "localhost",
            "0.0.0.0" => "127.0.0.1",
            "::" => "::1",
            _ => address
        };
        return $"http://{(host.Contains(':') ? $"[{host}]" : host)}:{port}";
    }

    private static void PublishConnection(Session? session)
    {
        if (session == null)
        {
            System.IO.File.Delete(ConnectionFile);
            return;
        }

        var details = session.Details;
        var options = new FileStreamOptions { Mode = FileMode.Create, Access = System.IO.FileAccess.Write };
        if (!OperatingSystem.IsWindows())
            options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;

        using var stream = new StreamWriter(System.IO.File.Open(ConnectionFile, options));
        stream.Write(JsonSerializer.Serialize(details, new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        }));
    }

    private static (string Summary, bool Available) DescribeConnection()
    {
        if (_session is not { } session)
            return (_failure == null ? "Stagehand is disabled." : "Could not start Stagehand: " + _failure, false);

        var auth = session.Details.RequireAccessToken ? "Access token required" : "No access token required";
        var summary = $"{session.Details.Url} · {auth}";
        var saved = _settings.RequireAccessToken && !string.IsNullOrEmpty(_settings.AccessToken)
            ? _settings : _settings with { AccessToken = null };
        if (session.Settings != saved)
            summary += "\nLaunch overrides active · Developer Mode " + (session.Settings.DeveloperMode ? "on" : "off");
        else if (session.Settings.DeveloperMode)
            summary += " · Developer Mode on";

        return (summary, true);
    }

    private static string CopyConnectionDetails()
    {
        if (_session == null)
            return _failure ?? "Stagehand is disabled.";

        DisplayServer.ClipboardSet(_session.Details.ForClipboard());
        return "Connection details copied.";
    }

    private static void Check(Error error, string operation)
    {
        if (error != Error.Ok)
            throw new InvalidOperationException($"Stagehand {operation}: {error}");
    }
}
