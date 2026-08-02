using Halo.ClaudeCode;
using Xunit;

namespace Halo.Tests;

// An answer leaves the pill as keystrokes into the agent's own terminal, so the number matters more than
// anything drawn: send the wrong digit and a real option gets answered with.
//
// This replaced a walk - Down, Options.Count times, then Enter. The walk was wrong in two ways at once. It
// had one count for two rows, so it reached the free-text row while the banner had labelled that row "Chat
// about this"; and the list wraps at the bottom, so a count one too large did not overshoot harmlessly, it
// came back around onto an option.
public class AskRowNumberTests
{
    // Four options is the case that was screenshotted: the box numbered them 5 and 6.
    [Fact]
    public void The_built_in_rows_follow_the_options_in_order()
    {
        Assert.Equal(5, AskStore.RowNumber(4, AskDelivery.FreeText));
        Assert.Equal(6, AskStore.RowNumber(4, AskDelivery.Chat));
    }

    [Fact]
    public void A_single_option_puts_them_at_two_and_three()
    {
        Assert.Equal(2, AskStore.RowNumber(1, AskDelivery.FreeText));
        Assert.Equal(3, AskStore.RowNumber(1, AskDelivery.Chat));
    }

    [Fact]
    public void A_numbered_option_does_not_go_through_the_built_in_path()
        => Assert.Equal(0, AskStore.RowNumber(4, AskDelivery.Option));

    // One keystroke is one digit. Eight options put the chat row at 10, and sending "1" would answer with
    // the first option - so the caller has to refuse rather than approximate.
    [Fact]
    public void Past_nine_rows_there_is_no_single_digit_to_send()
        => Assert.True(AskStore.RowNumber(8, AskDelivery.Chat) > 9);

    // The free-text row is still reachable at eight options; only the row below it falls off the end.
    [Fact]
    public void The_free_text_row_survives_one_option_longer_than_the_chat_row()
    {
        Assert.Equal(9, AskStore.RowNumber(8, AskDelivery.FreeText));
        Assert.True(AskStore.RowNumber(8, AskDelivery.Chat) > 9);
    }
}
