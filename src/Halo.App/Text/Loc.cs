using System;
using System.Collections.Generic;
using System.Globalization;

namespace Halo.Text;

/// <summary>
/// Minimal gettext-style lookup: the English source string is the key, so an
/// untranslated string simply falls through unchanged. Adding a language means
/// adding one file with a table — no resource compiler, no designer files.
/// Set HALO_LANG (e.g. "pt-BR", "en") to override the detected UI culture.
/// </summary>
internal static class Loc
{
    private static readonly Dictionary<string, IReadOnlyDictionary<string, string>> Tables =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["pt-BR"] = PtBr.Table,
            // Deliberate: neutral "pt" maps to the Brazilian table, so pt-PT falls back to it
            // through the neutral lookup below. Brazilian wording in Portugal beats English in
            // Portugal; a separate pt-PT table can be added later without touching this.
            ["pt"] = PtBr.Table,
        };

    private static readonly IReadOnlyDictionary<string, string>? Table;

    /// <summary>The resolved UI language tag, e.g. "pt-BR".</summary>
    public static string Lang { get; }

    static Loc()
    {
        var name = Environment.GetEnvironmentVariable("HALO_LANG");
        if (string.IsNullOrWhiteSpace(name))
        {
            try { name = CultureInfo.CurrentUICulture.Name; }
            catch { name = "en"; }
        }
        Lang = string.IsNullOrWhiteSpace(name) ? "en" : name;

        if (Tables.TryGetValue(Lang, out var exact))
        {
            Table = exact;
            return;
        }

        int dash = Lang.IndexOf('-');
        if (dash > 0 && Tables.TryGetValue(Lang[..dash], out var neutral))
            Table = neutral;
    }

    /// <summary>Translates <paramref name="en"/>, or returns it unchanged when untranslated.</summary>
    public static string T(string en)
        => Table is not null && Table.TryGetValue(en, out var v) ? v : en;

    /// <summary>
    /// Translates a composite format string, then fills it using the current culture.
    /// A translation with a malformed placeholder must not throw: these run from
    /// DrawCollapsed/DrawContent on the render timer, so a FormatException would drop
    /// every frame and the pill would silently stop updating. Degrade to the English
    /// format, then to the raw text, the way every interop call here degrades.
    /// </summary>
    public static string T(string en, params object?[] args)
    {
        try { return string.Format(CultureInfo.CurrentCulture, T(en), args); }
        catch (FormatException) { }
        try { return string.Format(CultureInfo.CurrentCulture, en, args); }
        catch (FormatException) { return en; }
    }
}
