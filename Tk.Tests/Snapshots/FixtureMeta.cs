using System.Text.Json;
using System.Text.Json.Serialization;

namespace Tk.Tests.Snapshots;

/// <summary>Deserialized shape of a fixture's meta.json — declares which filter to run it
/// through, at which detail level(s), and any filter-specific construction parameters.</summary>
public sealed class FixtureMeta
{
    public string Filter { get; set; } = "";
    public int ExitCode { get; set; }
    public string[] DetailLevels { get; set; } = ["Default"];

    // git-status
    public bool UnityMode { get; set; }
    public bool HasState { get; set; }

    // grep/rg
    public string Command { get; set; } = "grep";
    public string? Pattern { get; set; }

    // log
    public string[] Flags { get; set; } = [];

    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() }
    };

    public static FixtureMeta Load(string path)
    {
        var json = File.ReadAllText(path);
        return JsonSerializer.Deserialize<FixtureMeta>(json, Options)
            ?? throw new InvalidOperationException($"Empty/invalid meta.json at {path}");
    }
}
