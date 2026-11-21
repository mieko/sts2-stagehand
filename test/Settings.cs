using Stagehand.Sts2;

internal static class SettingsTest
{
    private static void Main()
    {
        var directory = Path.Combine(Path.GetTempPath(), "stagehand-settings-" + Guid.NewGuid());
        Directory.CreateDirectory(directory);
        try
        {
            CheckFiles(Path.Combine(directory, "settings.json"));
            CheckSave(Path.Combine(directory, "settings.json"));
            var tokens = Enumerable.Range(0, 10).Select(_ => ConnectionSettings.NewToken()).ToArray();
            Check(tokens.Distinct().Count() == 10, "independent launch tokens");
            Check(tokens.All(token => token.Length == 20 && token.All(char.IsAsciiLetterOrDigit)), "copyable ASCII tokens");
            Console.WriteLine("Settings: file validation, applied settings persistence and tokens passed");
        }
        finally { Directory.Delete(directory, true); }
    }

    private static void CheckFiles(string file)
    {
        var fresh = ConnectionSettings.Load(file);
        Check(fresh.Enabled && fresh.Address == "localhost" && fresh.Port == 27120
              && !fresh.DeveloperMode && !fresh.RequireAccessToken && fresh.AccessToken == null,
            "fresh install enables tokenless semantic access on dual loopback");
        File.WriteAllText(file, "{\"port\":0}");
        Check(!ConnectionSettings.Load(file).RequireAccessToken, "a local config can rely on the tokenless default");
        File.WriteAllText(file, "{\"accessToken\":\"my-key\"}");
        Check(ConnectionSettings.Load(file).RequireAccessToken, "a supplied key opts into access tokens");
        File.WriteAllText(file, "{\"accessToken\":\"saved-key\",\"requireAccessToken\":false}");
        var inactive = ConnectionSettings.Load(file);
        Check(!inactive.RequireAccessToken && inactive.AccessToken == "saved-key", "explicit authentication choice wins");
        File.WriteAllText(file, "{\"developerMode\":true,\"port\":0}");
        Check(ConnectionSettings.Load(file).RequireAccessToken, "a developer config with no auth setting still requires a token");
        File.WriteAllText(file, "{\"developerMode\":true,\"requireAccessToken\":false}");
        Check(!ConnectionSettings.Load(file).RequireAccessToken, "a developer config can still explicitly choose tokenless loopback");
        File.WriteAllText(file, "{\"allowRemoteConnections\":true}");
        var remote = ConnectionSettings.Load(file);
        Check(remote.Address == "*" && remote.RequireAccessToken, "a remote config with no auth setting still requires a token");
        File.WriteAllText(file, "{\"allowRemoteConnections\":true,\"requireAccessToken\":false}");
        var rejectedRemote = false;
        try { ConnectionSettings.Load(file); }
        catch (InvalidDataException) { rejectedRemote = true; }
        Check(rejectedRemote, "a remote config cannot explicitly choose tokenless access");
        File.WriteAllText(file, "{\"port\":0,\"listenAddress\":\"::1\",\"enabled\":false}");
        var settings = ConnectionSettings.Load(file);
        Check(settings.Port == 0 && settings.Address == "::1" && !settings.Enabled && settings.RequireAccessToken,
            "an explicit IP config with no auth setting still requires a token");
        Check((settings with { AllowRemoteConnections = true }).Address == "::1", "explicit address wins");
        (new ConnectionSettings { RequireAccessToken = false }).Validate();
        (new ConnectionSettings { ListenAddress = "127.0.0.1", RequireAccessToken = true }).Validate();
        (new ConnectionSettings { ListenAddress = "192.0.2.42", AllowRemoteConnections = true,
            RequireAccessToken = true }).Validate();
        foreach (var bad in new[] { "null", "{", "{\"port\":65536}", "{\"port\":-1}", "{\"listenAddress\":\"\"}", "{\"enabld\":false}" })
        {
            File.WriteAllText(file, bad);
            var rejected = false;
            try { ConnectionSettings.Load(file); }
            catch (Exception error) when (error is InvalidDataException or System.Text.Json.JsonException) { rejected = true; }
            Check(rejected, "invalid config rejected: " + bad);
        }

        foreach (var (invalid, expected) in new[]
        {
            (new ConnectionSettings { AllowRemoteConnections = true, RequireAccessToken = false },
                "Remote connections require an access token"),
            (new ConnectionSettings { ListenAddress = "127.0.0.1", RequireAccessToken = false },
                "Tokenless connections require the localhost listen address"),
            (new ConnectionSettings { ListenAddress = "*", RequireAccessToken = true },
                "Allow Remote Connections to use a non-loopback listen address"),
            (new ConnectionSettings { ListenAddress = "example.test" },
                "Listen address must be localhost, *, or an IP address without an interface scope"),
        })
        {
            var message = "";
            try { invalid.Validate(); }
            catch (InvalidDataException error) { message = error.Message; }
            Check(message == expected, "explicit connection settings error: " + expected);
        }
    }

    private static void CheckSave(string file)
    {
        var defaults = new ConnectionSettings { ListenAddress = "::1", Port = 0, RequireAccessToken = true };
        var draft = defaults with { Enabled = false, DeveloperMode = true, AccessToken = "shared test key" };
        draft.Save(file);
        Check(ConnectionSettings.Load(file) == draft, "saved toggles, token and advanced address/port round-trip");
        if (!OperatingSystem.IsWindows())
            Check(File.GetUnixFileMode(file) == (UnixFileMode.UserRead | UnixFileMode.UserWrite),
                "settings containing a token are owner-only");

        try
        {
            (draft with { Port = -1 }).Save(file);
            throw new Exception("Invalid settings were accepted");
        }
        catch (InvalidDataException)
        {
            Check(ConnectionSettings.Load(file) == draft, "rejected settings preserve the saved configuration");
        }
    }

    private static void Check(bool condition, string message)
    {
        if (!condition)
            throw new Exception(message);
    }
}
