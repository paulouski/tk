using Tk.Common;

namespace Tk.Filters;

public interface IOutputFilter
{
    string Apply(string raw, int exitCode);

    /// <summary>Same as <see cref="Apply(string, int)"/>, but classifies every input unit into
    /// <paramref name="ledger"/> (see docs/output-contract.md). Callers that need the
    /// conservation accounting (production footer emission, the snapshot harness) should call
    /// this overload; the 2-arg overload remains for callers that don't care.</summary>
    string Apply(string raw, int exitCode, UnitLedger ledger);
}

/// <summary>Pass-through: no filtering, output as-is.</summary>
public sealed class PassthroughFilter : IOutputFilter
{
    public string Apply(string raw, int exitCode) => raw;

    public string Apply(string raw, int exitCode, UnitLedger ledger)
    {
        // Everything is shown verbatim — the whole point of a passthrough.
        ledger.Keep(HiddenLinesFooter.CountLines(raw));
        return raw;
    }
}
