using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Stagehand.Sts2;

public sealed record ConnectionSettings
{
    public bool Enabled { get; init; } = true;
    public bool DeveloperMode { get; init; }
    public bool AllowRemoteConnections { get; init; }
    public bool RequireAccessToken { get; init; }
    public string? AccessToken { get; init; }
    public string? ListenAddress { get; init; }
    public int Port { get; init; } = 27120;

    [JsonIgnore]
    public string Address => ListenAddress == null
        ? (AllowRemoteConnections ? "*" : "localhost")
        : IPAddress.TryParse(ListenAddress, out var ip) ? ip.ToString() : ListenAddress;

    public static ConnectionSettings Load(string file)
    {
        if (!File.Exists(file))
            return new ConnectionSettings();

        using var document = JsonDocument.Parse(File.ReadAllText(file));
        var settings = document.RootElement.Deserialize<ConnectionSettings>(JsonOptions)
            ?? throw new InvalidDataException("Settings must be a JSON object");
        var needsDefaultToken = settings.DeveloperMode || settings.AllowRemoteConnections
            || !string.IsNullOrEmpty(settings.AccessToken)
            || (settings.ListenAddress != null && settings.Address != "localhost");
        if (needsDefaultToken && !document.RootElement.TryGetProperty("requireAccessToken", out _))
            settings = settings with { RequireAccessToken = true };

        settings.Validate();
        return settings;
    }

    public void Save(string file)
    {
        Validate();
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        var temporary = file + "." + Path.GetRandomFileName();
        var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write };
        if (!OperatingSystem.IsWindows())
            options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;

        try
        {
            using (var writer = new StreamWriter(File.Open(temporary, options)))
                writer.WriteLine(JsonSerializer.Serialize(this, JsonOptions));

            File.Move(temporary, file, overwrite: true);
        }
        finally { File.Delete(temporary); }
    }

    public void Validate()
    {
        if (Port is < 0 or > 65535)
            throw new InvalidDataException("Port must be in 0..65535 (0 selects an available port)");

        if (ListenAddress != null && ListenAddress is not ("localhost" or "*")
            && (!IPAddress.TryParse(ListenAddress, out var listenIp)
                || (listenIp.AddressFamily == AddressFamily.InterNetworkV6 && listenIp.ScopeId != 0)))
            throw new InvalidDataException("Listen address must be localhost, *, or an IP address without an interface scope");

        if (AllowRemoteConnections && !RequireAccessToken)
            throw new InvalidDataException("Remote connections require an access token");

        if (!RequireAccessToken && Address != "localhost")
            throw new InvalidDataException("Tokenless connections require the localhost listen address");

        if (!AllowRemoteConnections && Address != "localhost"
            && (!IPAddress.TryParse(Address, out var ip) || !IPAddress.IsLoopback(ip)))
            throw new InvalidDataException("Allow Remote Connections to use a non-loopback listen address");

        if (AccessToken?.IndexOfAny(['\r', '\n', '\0']) >= 0)
            throw new InvalidDataException("Access Token must be a single line of text");
    }

    public static string NewToken() => RandomNumberGenerator.GetString(
        "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789", 20);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        IgnoreReadOnlyProperties = true,
        WriteIndented = true
    };
}
