using Halo.Widgets;

namespace Halo.Tests;

// Direction used to be "any Hebrew..Arabic character anywhere in the string", so a single Persian word
// inside an English message flipped the whole paragraph right-to-left and GDI+ bidi then reordered every
// latin run in it as a block - the "|" separators ended up between the wrong pieces. These pin the
// Unicode rule instead (UAX #9 P2/P3): the FIRST strong character decides, neutrals do not vote.
//
// The RTL sample is written as escapes, not as literal script, so this file stays ASCII: Salam = hello.
public sealed class NotifBannerRtlTests
{
    private const string Rtl = "\u0633\u0644\u0627\u0645";

    [Theory]
    [InlineData("")]                       // the sample on its own
    [InlineData(" hello")]                 // first strong is RTL, latin trails inside it
    [InlineData(" world | Halo")]
    public void FirstStrongRightToLeftIsRtl(string tail) => Assert.True(NotifBanner.IsRtl(Rtl + tail));

    // neutrals - digits, punctuation, separators, spaces - are skipped until the first strong character
    [Theory]
    [InlineData("  |  123  ")]
    [InlineData("[ 15 ] | ")]
    public void NeutralsBeforeTheFirstStrongCharacterDoNotVote(string lead)
        => Assert.True(NotifBanner.IsRtl(lead + Rtl));

    [Theory]
    [InlineData("hello")]
    [InlineData("Halo | Media | General")]
    [InlineData("123 | Idle timeout 15 sec | Claude")]
    public void FirstStrongLeftToRightIsNotRtl(string s) => Assert.False(NotifBanner.IsRtl(s));

    // the regression this was reported on: one RTL word late in an English line no longer flips the line
    [Fact]
    public void LatinFirstStaysLeftToRightEvenWithRtlLater()
        => Assert.False(NotifBanner.IsRtl("Halo | Media | " + Rtl + " | General"));

    // nothing strong at all. Left-to-right is the safe default and the one the Unicode rule specifies.
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("| 123 |")]
    public void NoStrongCharacterDefaultsLeftToRight(string s) => Assert.False(NotifBanner.IsRtl(s));
}
