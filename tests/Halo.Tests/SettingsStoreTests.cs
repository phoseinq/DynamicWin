using System;
using System.IO;
using Halo.Settings;
using Xunit;

namespace Halo.Tests;

// The panel's draft/Apply layer. The interesting case is not what Apply writes but what it must NOT
// overwrite: the pill owns the same file and writes to it while the panel sits open.
public sealed class SettingsStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "halo-store-" + Guid.NewGuid().ToString("N"));
    private string Path_ => Path.Combine(_dir, "settings.json");

    public SettingsStoreTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private void WriteFile(params (string key, string value)[] rows)
    {
        var values = new System.Text.Json.Nodes.JsonObject();
        foreach (var (key, value) in rows) values[key] = value;
        File.WriteAllText(Path_, new System.Text.Json.Nodes.JsonObject
        { ["version"] = 1, ["values"] = values }.ToJsonString());
    }

    private string Read(string key)
    {
        var store = new Store(Path_);
        return store.Text(key, "");
    }

    [Fact]
    public void Apply_writes_the_edited_row()
    {
        var store = new Store(Path_);
        store.Set("scale", "1.25");
        store.Apply();
        Assert.Equal("1.25", Read("scale"));
    }

    // the regression: the panel used to merge its draft onto the snapshot it OPENED with, so applying one
    // row wrote that whole stale copy back and silently undid anything the pill had written meanwhile
    [Fact]
    public void Apply_does_not_clobber_a_row_the_pill_wrote_while_the_panel_was_open()
    {
        WriteFile(("scale", "1.0"), ("glass", "on"));
        var store = new Store(Path_);          // panel opens, snapshots scale=1.0 glass=on

        WriteFile(("scale", "1.0"), ("glass", "off"));   // pill writes glass=off behind its back

        store.Set("scale", "1.5");
        store.Apply();

        Assert.Equal("1.5", Read("scale"));    // the panel's own edit lands
        Assert.Equal("off", Read("glass"));    // and the pill's write survives it
    }

    // a row the pill ADDED must survive too - the stale snapshot had no key for it at all
    [Fact]
    public void Apply_keeps_a_row_that_appeared_after_the_panel_opened()
    {
        WriteFile(("scale", "1.0"));
        var store = new Store(Path_);

        WriteFile(("scale", "1.0"), ("token", "abc"));

        store.Set("scale", "1.5");
        store.Apply();

        Assert.Equal("abc", Read("token"));
    }

    // reset is the one case that is meant to be destructive: defaults are the ABSENCE of values, so it
    // clears the file rather than merging onto it
    [Fact]
    public void Staged_defaults_clear_the_file_rather_than_merging()
    {
        WriteFile(("scale", "1.5"), ("glass", "off"));
        var store = new Store(Path_);
        store.StageDefaults();
        Assert.True(store.IsDirty);
        store.Apply();

        Assert.Equal("", Read("scale"));
        Assert.Equal("", Read("glass"));
    }

    [Fact]
    public void A_clean_store_writes_nothing()
    {
        WriteFile(("scale", "1.0"));
        var store = new Store(Path_);
        Assert.False(store.IsDirty);
        store.Apply();
        Assert.Equal("1.0", Read("scale"));
    }

    // setting a row back to where it started is not a pending change
    [Fact]
    public void Returning_a_row_to_its_starting_value_drops_it_from_the_draft()
    {
        WriteFile(("glass", "on"));
        var store = new Store(Path_);
        store.Set("glass", "off", "on");
        Assert.Equal(1, store.PendingCount);
        store.Set("glass", "on", "on");
        Assert.Equal(0, store.PendingCount);
    }
}
