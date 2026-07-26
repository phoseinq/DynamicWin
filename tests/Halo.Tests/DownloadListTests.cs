using System;
using System.Collections.Generic;
using System.Linq;
using Halo.Widgets;
using Xunit;

namespace Halo.Tests;

// Several downloads can be in flight at once, and the pill shows one at a time. The order they are shown
// in is the whole contract: oldest first, and never reshuffled by progress — a list sorted by speed or
// percentage would swap the pill out from under the user every second.
public class DownloadListTests
{
    private static Downloads.DlItem It(string key, string name = "x", int pct = 0) =>
        new(key, name, pct, 0, 0, false, false, false, false, false, false, false, null, null, null, 0, IntPtr.Zero);

    private static List<string> Keys(List<Downloads.DlItem> l) => l.Select(i => i.Key).ToList();

    [Fact]
    public void Downloads_keep_the_order_they_arrived_in()
    {
        var born = new Dictionary<string, long>();
        var first = new List<Downloads.DlItem> { It("b") };
        Downloads.Order(first, born, 1000);

        // "a" shows up later, and must still sort after "b" even though its key is smaller
        var second = new List<Downloads.DlItem> { It("a"), It("b") };
        Downloads.Order(second, born, 2000);
        Assert.Equal(new[] { "b", "a" }, Keys(second));
    }

    [Fact]
    public void Downloads_noticed_in_the_same_scan_are_ordered_by_key_so_they_cannot_wobble()
    {
        var born = new Dictionary<string, long>();
        var l = new List<Downloads.DlItem> { It("c"), It("a"), It("b") };
        Downloads.Order(l, born, 1000);
        Assert.Equal(new[] { "a", "b", "c" }, Keys(l));
    }

    [Fact]
    public void Progress_never_reorders_the_list()
    {
        var born = new Dictionary<string, long>();
        var l = new List<Downloads.DlItem> { It("a", pct: 5), It("b", pct: 90) };
        Downloads.Order(l, born, 1000);
        var later = new List<Downloads.DlItem> { It("b", pct: 99), It("a", pct: 6) };
        Downloads.Order(later, born, 5000);
        Assert.Equal(new[] { "a", "b" }, Keys(later));
    }

    [Fact]
    public void A_finished_download_is_forgotten_so_the_same_name_can_start_over()
    {
        var born = new Dictionary<string, long>();
        Downloads.Order(new List<Downloads.DlItem> { It("a"), It("b") }, born, 1000);
        Downloads.Order(new List<Downloads.DlItem> { It("b") }, born, 2000);   // "a" completed
        Assert.False(born.ContainsKey("a"));

        // downloading "a" again makes it the newer one now
        var again = new List<Downloads.DlItem> { It("a"), It("b") };
        Downloads.Order(again, born, 3000);
        Assert.Equal(new[] { "b", "a" }, Keys(again));
    }

    // The switcher shows four rows; the window has to contain whichever row is selected, or picking the
    // fifth download would scroll it out of sight.
    [Theory]
    [InlineData(3, 0, 0)]   // shorter than the window → no scrolling at all
    [InlineData(3, 2, 0)]
    [InlineData(6, 0, 0)]   // selection already visible → stay at the top
    [InlineData(6, 3, 0)]
    [InlineData(6, 4, 1)]   // selection fell off the bottom → scroll just enough
    [InlineData(6, 5, 2)]
    public void The_switcher_window_always_contains_the_selected_row(int n, int selected, int expected)
        => Assert.Equal(expected, DownloadWidget.MenuTop(n, selected, 4));
}
