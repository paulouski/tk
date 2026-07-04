using Tk.Common;
using Xunit;

namespace Tk.Tests.Common;

public class UnitLedgerTests
{
    [Fact]
    public void Keep_increments_kept_and_total()
    {
        var ledger = new UnitLedger();
        ledger.Keep();
        ledger.Keep();
        Assert.Equal(2, ledger.Kept);
        Assert.Equal(2, ledger.Total);
    }

    [Fact]
    public void Keep_with_count_adds_bulk()
    {
        var ledger = new UnitLedger();
        ledger.Keep(5);
        Assert.Equal(5, ledger.Kept);
        Assert.Equal(5, ledger.Total);
    }

    [Fact]
    public void Keep_with_unit_object_ignores_value_and_counts_one()
    {
        var ledger = new UnitLedger();
        ledger.Keep("some line of text");
        Assert.Equal(1, ledger.Kept);
    }

    [Fact]
    public void Summarize_hide_unparsed_each_track_their_own_category()
    {
        var ledger = new UnitLedger();
        ledger.Summarize(3);
        ledger.Hide(2);
        ledger.Unparsed(1);
        Assert.Equal(3, ledger.Summarized);
        Assert.Equal(2, ledger.Hidden);
        Assert.Equal(1, ledger.UnparsedCount);
        Assert.Equal(6, ledger.Total);
    }

    [Fact]
    public void Total_is_sum_of_all_four_categories()
    {
        var ledger = new UnitLedger();
        ledger.Keep(4);
        ledger.Summarize(3);
        ledger.Hide(2);
        ledger.Unparsed(1);
        Assert.Equal(10, ledger.Total);
    }

    [Fact]
    public void AssertConserves_passes_when_total_matches_input()
    {
        var ledger = new UnitLedger();
        ledger.Keep(3);
        ledger.Hide(2);
        ledger.AssertConserves(5); // must not throw
    }

    [Fact]
    public void AssertConserves_throws_when_total_is_under_input()
    {
        var ledger = new UnitLedger();
        ledger.Keep(3);
        var ex = Assert.Throws<InvalidOperationException>(() => ledger.AssertConserves(10));
        Assert.Contains("classified 3 units", ex.Message);
        Assert.Contains("input had 10", ex.Message);
    }

    [Fact]
    public void AssertConserves_throws_when_classified_more_than_input()
    {
        // "classifying more units than input fails loud" — misuse case.
        var ledger = new UnitLedger();
        ledger.Keep(3);
        ledger.Hide(4);
        var ex = Assert.Throws<InvalidOperationException>(() => ledger.AssertConserves(5));
        Assert.Contains("classified 7 units", ex.Message);
        Assert.Contains("input had 5", ex.Message);
    }

    [Fact]
    public void AssertConserves_zero_input_zero_total_passes()
    {
        var ledger = new UnitLedger();
        ledger.AssertConserves(0); // must not throw
    }

    [Fact]
    public void Fresh_ledger_has_zero_totals()
    {
        var ledger = new UnitLedger();
        Assert.Equal(0, ledger.Kept);
        Assert.Equal(0, ledger.Summarized);
        Assert.Equal(0, ledger.Hidden);
        Assert.Equal(0, ledger.UnparsedCount);
        Assert.Equal(0, ledger.Total);
    }
}
