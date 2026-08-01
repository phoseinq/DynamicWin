using System.Collections.Generic;
using System.IO;
using Halo.Settings;
using Xunit;

namespace Halo.Tests;

// The contract between two executables that ship together and are versioned together but must not be
// able to break each other. The panel writes this file, the pill reads it, and neither can see the
// other's types - so what is pinned here is the round trip and the tolerance either side needs.
public class SettingsFileTests
{
    [Fact]
    public void ValuesSurviveTheRoundTrip()
    {
        var written = SettingsFile.Empty
            .With("feature.media", "off")
            .With("appearance.scale", "110%");
        var read = SettingsFile.FromJson(written.ToJson());

        Assert.False(read.Bool("feature.media", true));
        Assert.Equal(110f, read.Number("appearance.scale", 100f));
    }

    // A newer panel writing a key this build has never heard of must not lose it on the next save, or a
    // downgrade for one session would quietly wipe settings the user set.
    [Fact]
    public void UnknownKeysAreKept()
    {
        var read = SettingsFile.FromJson(SettingsFile.Empty.With("something.new", "yes").ToJson());
        Assert.Equal("yes", read.Text("something.new", ""));
        Assert.Contains("something.new", SettingsFile.FromJson(read.ToJson()).Values.Keys);
    }

    // Defaults live at the read. An absent key is "the user has not said", which is not the same as off.
    [Theory]
    [InlineData("on", true)]
    [InlineData("true", true)]
    [InlineData("off", false)]
    [InlineData("anything else", false)]
    public void ATogglesWordIsItsValue(string stored, bool expected)
        => Assert.Equal(expected, SettingsFile.Empty.With("k", stored).Bool("k", fallback: true));

    [Fact]
    public void AnAbsentKeyTakesTheFallback()
    {
        Assert.True(SettingsFile.Empty.Bool("k", true));
        Assert.False(SettingsFile.Empty.Bool("k", false));
        Assert.Equal("soft", SettingsFile.Empty.Text("appearance.motion", "soft"));
    }

    // The panel's slider rows carry their unit in the value, because the value IS the label on the row
    [Theory]
    [InlineData("100%", 100f)]
    [InlineData("  95 % ", 95f)]
    [InlineData("not a number", 100f)]
    public void ASliderValueReadsThroughItsUnit(string stored, float expected)
        => Assert.Equal(expected, SettingsFile.Empty.With("appearance.scale", stored).Number("appearance.scale", 100f));

    // Half a file, no file, a file of nonsense: all mean defaults, never a throw. This is read during
    // startup of a process whose whole job is to not crash.
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("{")]
    [InlineData("[]")]
    [InlineData("{\"version\":1}")]
    public void NothingReadableMeansDefaults(string? json)
        => Assert.Empty(SettingsFile.FromJson(json).Values);

    [Fact]
    public void FeatureKeysAreStableWireNames()
    {
        Assert.Equal("feature.media", SettingsKeys.Feature(FeatureId.Media));
        Assert.Equal("feature.claudeCode", SettingsKeys.Feature(FeatureId.ClaudeCode));
        foreach (var feature in FeatureCatalog.All)
            Assert.False(string.IsNullOrWhiteSpace(feature.Key));
    }

    // The pill's default is that everything it can show, it shows: a fresh install has no file at all.
    [Fact]
    public void EveryFeatureIsOnUntilSwitchedOff()
    {
        var store = new SettingsStore(Path.Combine(Path.GetTempPath(), $"halo-settings-{System.Guid.NewGuid():N}.json"),
            watch: false);
        foreach (var feature in FeatureCatalog.All)
            Assert.True(store.Enabled(feature.Id));
    }

    [Fact]
    public void WritingIsAtomicAndReadableBack()
    {
        string path = Path.Combine(Path.GetTempPath(), $"halo-settings-{System.Guid.NewGuid():N}.json");
        try
        {
            using var store = new SettingsStore(path, watch: false);
            Assert.True(store.Set("feature.codex", "off"));
            Assert.False(store.Set("feature.codex", "off"));   // unchanged: no write, no version bump
            Assert.False(store.Enabled(FeatureId.Codex));
            Assert.False(SettingsFile.Read(path).Bool("feature.codex", true));
            Assert.False(File.Exists(path + ".tmp"));
        }
        finally { try { File.Delete(path); } catch { } }
    }
}
