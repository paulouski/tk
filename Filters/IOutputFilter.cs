namespace Tk.Filters;

public interface IOutputFilter
{
    string Apply(string raw, int exitCode);
}

/// <summary>Pass-through: no filtering, output as-is.</summary>
public sealed class PassthroughFilter : IOutputFilter
{
    public string Apply(string raw, int exitCode) => raw;
}
