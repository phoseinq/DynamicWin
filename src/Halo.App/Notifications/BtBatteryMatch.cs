using System;
using System.Collections.Generic;

namespace Halo.Notifications;

/// <summary>A PnP object that publishes a battery level, reduced to what identifying it needs.</summary>
/// <param name="Container">Windows container identity, or null when the object published none.</param>
internal readonly record struct BtBatterySource(string? Container, string Name, int Pct);

/// <summary>
/// Decides which battery reading belongs to which device.
///
/// Display names are not identity. Two headsets of the same model report the same
/// <c>System.ItemNameDisplay</c>, and the old lookup returned the first one it found -- including
/// on a substring match -- so one device could be shown wearing the other's charge. The pill
/// selects by id, and a number attached by name undoes that.
///
/// So identity is tried first: the container id ties an association endpoint to its PnP object and
/// is the same for both halves of one physical device. Name is kept only as a fallback, because
/// plenty of battery objects publish no container at all, and it is only allowed to answer when it
/// picks out exactly one candidate. Anything ambiguous returns unknown. A missing number is a
/// state the pill can show honestly; a confident wrong number is not.
/// </summary>
internal static class BtBatteryMatch
{
    public const int Unknown = -1;

    public static int Resolve(BtDevice dev, IReadOnlyList<BtBatterySource> sources)
    {
        string? want = Normalize(dev.Container);
        if (want is not null)
        {
            int pct = Unknown, hits = 0;
            foreach (var s in sources)
                if (Normalize(s.Container) == want) { pct = s.Pct; hits++; }
            if (hits == 1) return pct;
            // Two objects claiming the same container is not something to guess about. Devices
            // whose battery object publishes no container fall through to the name rules below.
            if (hits > 1) return Unknown;
        }

        int exact = Match(dev.Name, sources, exactOnly: true);
        if (exact != Unknown) return exact;
        return Match(dev.Name, sources, exactOnly: false);
    }

    /// <summary>Returns the single candidate matching <paramref name="name"/>, or unknown if none
    /// or more than one does. "More than one" is the case the old code got wrong.</summary>
    private static int Match(string name, IReadOnlyList<BtBatterySource> sources, bool exactOnly)
    {
        if (name.Length == 0) return Unknown;
        int pct = Unknown, hits = 0;
        foreach (var s in sources)
        {
            bool hit = exactOnly
                ? string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase)
                : s.Name.Length > 0 && (name.Contains(s.Name, StringComparison.OrdinalIgnoreCase)
                    || s.Name.Contains(name, StringComparison.OrdinalIgnoreCase));
            if (hit) { pct = s.Pct; hits++; }
        }
        return hits == 1 ? pct : Unknown;
    }

    /// <summary>
    /// Container ids arrive as a Guid from PnP and as a string from the association endpoint, in
    /// whichever bracketing that API felt like. An all-zero Guid means "no container", not a
    /// container every device shares -- treating it as a value would match everything to everything.
    /// </summary>
    public static string? Normalize(object? value)
    {
        Guid g = value switch
        {
            Guid guid => guid,
            string s when Guid.TryParse(s, out var parsed) => parsed,
            _ => Guid.Empty,
        };
        if (g != Guid.Empty) return g.ToString("B").ToUpperInvariant();
        // Not a Guid at all: keep a non-empty string as an opaque identifier rather than dropping it.
        return value is string raw && raw.Length > 0 && !Guid.TryParse(raw, out _)
            ? raw.ToUpperInvariant()
            : null;
    }
}
