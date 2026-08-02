using System;
using System.IO;
using System.Text.RegularExpressions;
using Xunit;

namespace Halo.Tests;

// The About page in Halo.Settings reads its own assembly version. That is only correct while every
// project inherits one number - when <Version> lived in Halo.App.csproj alone, the pill said 3.x and
// the panel said 1.0.0, the SDK default. These pin the single source rather than the symptom.
public class VersionSourceTests
{
    private static string Root
    {
        get
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "Halo.sln"))) dir = dir.Parent;
            Assert.NotNull(dir);
            return dir!.FullName;
        }
    }

    private static string Declared
    {
        get
        {
            string props = File.ReadAllText(Path.Combine(Root, "Directory.Build.props"));
            var m = Regex.Match(props, @"<Version>\s*([^<\s]+)\s*</Version>");
            Assert.True(m.Success, "Directory.Build.props must declare <Version>");
            return m.Groups[1].Value;
        }
    }

    [Fact]
    public void Every_assembly_inherits_the_declared_version()
    {
        var actual = typeof(Halo.Shell.StripOrder).Assembly.GetName().Version;
        Assert.NotNull(actual);
        // ToString(3) is what Live.Version shows on the About page
        Assert.Equal(Declared, actual!.ToString(3));
    }

    [Fact]
    public void No_shipped_project_pins_a_version_of_its_own()
    {
        foreach (var proj in Directory.EnumerateFiles(Path.Combine(Root, "src"), "*.csproj", SearchOption.AllDirectories))
            Assert.DoesNotContain("<Version>", File.ReadAllText(proj), StringComparison.Ordinal);
    }
}
