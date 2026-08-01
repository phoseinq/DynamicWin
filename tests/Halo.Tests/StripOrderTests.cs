using System.Collections.Generic;
using Halo.Shell;
using Xunit;

namespace Halo.Tests;

public class StripOrderTests
{
    private static readonly string[] OnScreen = ["media", "claude", "download", "filetray"];

    [Fact]
    public void With_no_opinion_the_built_in_order_stands()
        => Assert.Equal(OnScreen, new StripOrder().Apply(OnScreen));

    [Fact]
    public void A_dragged_kind_moves_one_place()
    {
        var order = new StripOrder();
        Assert.True(order.Move(OnScreen, "download", -1));
        Assert.Equal(["media", "download", "claude", "filetray"], order.Apply(OnScreen));
    }

    [Fact]
    public void Dragging_past_the_ends_does_nothing_rather_than_wrapping()
    {
        var order = new StripOrder();
        Assert.False(order.Move(OnScreen, "media", -1));
        Assert.False(order.Move(OnScreen, "filetray", 1));
    }

    [Fact]
    public void An_arrangement_survives_the_thing_that_was_moved_disappearing()
    {
        var order = new StripOrder();
        order.Move(OnScreen, "filetray", -2);
        Assert.Equal(["media", "filetray", "claude", "download"], order.Apply(OnScreen));

        // the download finishes and its row goes; the rest must not reshuffle
        Assert.Equal(["media", "filetray", "claude"], order.Apply(["media", "claude", "filetray"]));

        // and when it comes back it returns to where it was left, not to the end
        Assert.Equal(["media", "filetray", "claude", "download"], order.Apply(OnScreen));
    }

    // The reason a whole visible order is recorded rather than one position: something arriving later must
    // not be able to land in the middle of an arrangement the user made by hand.
    [Fact]
    public void A_newcomer_lands_after_everything_that_was_arranged()
    {
        var order = new StripOrder();
        order.Move(OnScreen, "download", -2);
        var withNew = new List<string>(OnScreen) { "codex" };
        Assert.Equal(["download", "media", "claude", "filetray", "codex"], order.Apply(withNew));
    }

    [Fact]
    public void A_kind_never_seen_before_keeps_its_built_in_position_among_the_others()
    {
        var order = new StripOrder(["filetray"]);
        Assert.Equal(["filetray", "media", "claude", "download"], order.Apply(OnScreen));
    }

    [Fact]
    public void The_arrangement_round_trips_through_its_stored_form()
    {
        var order = new StripOrder();
        order.Move(OnScreen, "claude", 2);
        var reloaded = new StripOrder(order.Serialise().Split('\n'));
        Assert.Equal(order.Apply(OnScreen), reloaded.Apply(OnScreen));
    }

    [Fact]
    public void Duplicates_and_blank_lines_in_a_stored_file_are_ignored()
    {
        var order = new StripOrder(["claude", "", "claude", "   ", "media"]);
        Assert.Equal(["claude", "media"], order.Pinned);
    }
}
