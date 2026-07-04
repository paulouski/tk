namespace Tk.Common;

/// <summary>
/// Routes every input unit a filter processes into exactly one of four categories — Kept,
/// Summarized, Hidden, Unparsed — so that <see cref="Total"/> is definitionally the count of
/// everything the filter looked at. See docs/output-contract.md for the full contract.
/// </summary>
/// <remarks>
/// There is no free-form counter escape hatch: a filter that wants to account for a unit must
/// call one of <see cref="Keep"/>/<see cref="Summarize"/>/<see cref="Hide"/>/<see cref="Unparsed"/>.
/// <see cref="AssertConserves"/> is how the test harness enforces that a filter's ledger totals
/// match the real input-unit count for every fixture.
/// </remarks>
public sealed class UnitLedger
{
    /// <summary>Rendered verbatim or near-verbatim (may be truncated for length).</summary>
    public int Kept { get; private set; }

    /// <summary>Not rendered itself, but represented by an aggregate output line (a count, a
    /// top=/+N more line, a dedup marker, a collapsed group).</summary>
    public int Summarized { get; private set; }

    /// <summary>Recognized and intentionally omitted (blank lines, known noise, a capped tail).
    /// Only legal for content the filter actually recognized — alien input must go to
    /// <see cref="Unparsed"/> instead.</summary>
    public int Hidden { get; private set; }

    /// <summary>The filter did not recognize this unit's shape at all.</summary>
    public int UnparsedCount { get; private set; }

    /// <summary>Sum of every category — the count of all units classified so far.</summary>
    public int Total => Kept + Summarized + Hidden + UnparsedCount;

    /// <summary>Classify one unit (or <paramref name="count"/> units) as Kept. The optional
    /// <paramref name="unit"/> parameter exists so call sites read naturally
    /// (<c>ledger.Keep(line)</c>) — its value is not inspected.</summary>
    public void Keep(object? unit = null) => Kept++;
    public void Keep(int count) => Kept += count;

    public void Summarize(object? unit = null) => Summarized++;
    public void Summarize(int count) => Summarized += count;

    public void Hide(object? unit = null) => Hidden++;
    public void Hide(int count) => Hidden += count;

    public void Unparsed(object? unit = null) => UnparsedCount++;
    public void Unparsed(int count) => UnparsedCount += count;

    /// <summary>Throws when the categorized <see cref="Total"/> doesn't equal
    /// <paramref name="inputUnits"/> — the conservation invariant. Call once after a filter has
    /// finished classifying every unit of its input.</summary>
    public void AssertConserves(int inputUnits)
    {
        if (Total != inputUnits)
            throw new InvalidOperationException(
                $"UnitLedger conservation violated: classified {Total} units " +
                $"(kept={Kept} summarized={Summarized} hidden={Hidden} unparsed={UnparsedCount}) " +
                $"but input had {inputUnits}.");
    }
}
