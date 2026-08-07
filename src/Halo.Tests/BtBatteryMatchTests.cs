using System;
using System.Collections.Generic;
using Halo.Notifications;
using Xunit;

namespace Halo.Tests;

/// <summary>
/// The pill picks its device by id. These pin down that the number attached to it is picked the
/// same way, so two devices of the same model cannot end up wearing each other's charge.
/// </summary>
public class BtBatteryMatchTests
{
    private const string C1 = "{11111111-1111-1111-1111-111111111111}";
    private const string C2 = "{22222222-2222-2222-2222-222222222222}";

    private static BtDevice Dev(string id, string name, string? container) =>
        new(id, name, BtBatteryMatch.Normalize(container));

    private static List<BtBatterySource> Sources(params (string? container, string name, int pct)[] rows)
    {
        var list = new List<BtBatterySource>(rows.Length);
        foreach (var (container, name, pct) in rows)
            list.Add(new BtBatterySource(BtBatteryMatch.Normalize(container), name, pct));
        return list;
    }

    [Fact]
    public void TwoDevicesSharingADisplayName_EachGetsItsOwnBatteryByContainer()
    {
        var left = Dev("bt#aaa", "WH-1000XM4", C1);
        var right = Dev("bt#bbb", "WH-1000XM4", C2);
        var sources = Sources((C1, "WH-1000XM4", 80), (C2, "WH-1000XM4", 20));

        Assert.Equal(80, BtBatteryMatch.Resolve(left, sources));
        Assert.Equal(20, BtBatteryMatch.Resolve(right, sources));
    }

    [Fact]
    public void TwoDevicesSharingADisplayNameWithNoContainer_ResolveUnknownRatherThanGuess()
    {
        var dev = Dev("bt#aaa", "WH-1000XM4", container: null);
        var sources = Sources((null, "WH-1000XM4", 80), (null, "WH-1000XM4", 20));

        Assert.Equal(BtBatteryMatch.Unknown, BtBatteryMatch.Resolve(dev, sources));
    }

    [Fact]
    public void ContainerWins_EvenWhenAnotherObjectMatchesTheNameExactly()
    {
        var dev = Dev("bt#aaa", "Pixel Buds", C1);
        var sources = Sources((C1, "Pixel Buds Pro", 55), (C2, "Pixel Buds", 90));

        Assert.Equal(55, BtBatteryMatch.Resolve(dev, sources));
    }

    [Fact]
    public void NoContainerOnTheBatteryObject_FallsBackToAUniqueNameMatch()
    {
        var dev = Dev("bt#aaa", "DualSense Wireless Controller", C1);
        var sources = Sources((null, "DualSense Wireless Controller", 65));

        Assert.Equal(65, BtBatteryMatch.Resolve(dev, sources));
    }

    [Fact]
    public void ExactNameIsPreferredOverASubstringMatch()
    {
        var dev = Dev("bt#aaa", "Arctis 7", null);
        var sources = Sources((null, "Arctis 7 Chat", 30), (null, "Arctis 7", 88));

        Assert.Equal(88, BtBatteryMatch.Resolve(dev, sources));
    }

    [Fact]
    public void AmbiguousSubstringMatches_ResolveUnknown()
    {
        var dev = Dev("bt#aaa", "Arctis 7 Wireless", null);
        var sources = Sources((null, "Arctis 7", 30), (null, "Arctis", 88));

        Assert.Equal(BtBatteryMatch.Unknown, BtBatteryMatch.Resolve(dev, sources));
    }

    [Fact]
    public void ASingleSubstringMatch_IsStillAccepted()
    {
        var dev = Dev("bt#aaa", "Arctis 7 Wireless", null);
        var sources = Sources((null, "Arctis 7", 30), (null, "Magic Mouse", 88));

        Assert.Equal(30, BtBatteryMatch.Resolve(dev, sources));
    }

    [Fact]
    public void NothingMatches_ResolvesUnknown()
    {
        var dev = Dev("bt#aaa", "Pixel Buds", C1);
        var sources = Sources((C2, "Magic Keyboard", 40));

        Assert.Equal(BtBatteryMatch.Unknown, BtBatteryMatch.Resolve(dev, sources));
    }

    [Fact]
    public void TwoObjectsClaimingTheSameContainer_ResolveUnknown()
    {
        var dev = Dev("bt#aaa", "WH-1000XM4", C1);
        var sources = Sources((C1, "WH-1000XM4", 80), (C1, "WH-1000XM4 Hands-Free", 20));

        Assert.Equal(BtBatteryMatch.Unknown, BtBatteryMatch.Resolve(dev, sources));
    }

    /// <summary>
    /// Windows hands out an all-zero container for devices that belong to none. Treating it as a
    /// value would make every such device match every other one.
    /// </summary>
    [Fact]
    public void AnEmptyContainerIsNotAnIdentity()
    {
        Assert.Null(BtBatteryMatch.Normalize(Guid.Empty));
        Assert.Null(BtBatteryMatch.Normalize("{00000000-0000-0000-0000-000000000000}"));
        Assert.Null(BtBatteryMatch.Normalize(null));
        Assert.Null(BtBatteryMatch.Normalize(""));

        var dev = Dev("bt#aaa", "Buds", Guid.Empty.ToString("B"));
        var sources = Sources((Guid.Empty.ToString("B"), "Other Device", 80));
        Assert.Equal(BtBatteryMatch.Unknown, BtBatteryMatch.Resolve(dev, sources));
    }

    /// <summary>The two APIs disagree on bracketing and case; the same device must still match.</summary>
    [Fact]
    public void ContainerIdsMatchAcrossGuidFormats()
    {
        var dev = new BtDevice("bt#aaa", "Buds", BtBatteryMatch.Normalize(
            Guid.Parse(C1).ToString("D").ToLowerInvariant()));
        var sources = new List<BtBatterySource>
        {
            new(BtBatteryMatch.Normalize(Guid.Parse(C1)), "Something Else", 42),
        };

        Assert.Equal(42, BtBatteryMatch.Resolve(dev, sources));
    }
}
