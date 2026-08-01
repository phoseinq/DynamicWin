using System;
using Halo.Shell;
using Xunit;

namespace Halo.Tests;

public class GreetingGateTests
{
    private const string Version = "3.1.7.0";

    [Fact]
    public void No_marker_means_this_build_has_never_run_here()
        => Assert.Equal(GreetingKind.Install, GreetingGate.Decide(null, Version));

    // the marker used to hold a boot timestamp; an upgrade reads that and must treat it as "not this build"
    [Fact]
    public void A_marker_it_cannot_recognise_is_a_new_build()
        => Assert.Equal(GreetingKind.Install, GreetingGate.Decide("2026-07-31T17:11:05.4669755Z", Version));

    [Fact]
    public void An_upgrade_introduces_itself()
        => Assert.Equal(GreetingKind.Install, GreetingGate.Decide("3.1.6.0", Version));

    // The one that matters: the settings panel restarts Halo on apply. Same build, so the long
    // introduction stays put - but the hand still comes, which is what was asked for.
    [Fact]
    public void A_restart_of_the_same_build_gets_the_hand_and_not_the_introduction()
        => Assert.Equal(GreetingKind.Login, GreetingGate.Decide(Version, Version));

    // whitespace happens to a file written by a text editor; it is not a different build
    [Fact]
    public void The_marker_is_compared_trimmed()
        => Assert.Equal(GreetingKind.Login, GreetingGate.Decide(" 3.1.7.0\r\n", Version));

    // a real version, not "0": the fallback would make every launch an install
    [Fact]
    public void The_running_build_reports_a_version()
    {
        Assert.NotEqual("0", GreetingGate.Version);
        Assert.Equal(GreetingKind.Login, GreetingGate.Decide(GreetingGate.Version, GreetingGate.Version));
    }

    // Long enough that no stall, alt-tab storm or fps tier can reach it, short enough that a nap does.
    [Fact]
    public void A_wake_is_a_gap_no_running_frame_could_produce()
        => Assert.True(NotchController.WakeGap >= TimeSpan.FromSeconds(60));
}

public class ScriptTests
{
    // The failure this catches is silent by construction: a stroke authored one point short still parses,
    // still draws, and just loses its last curve. "e" shipped that way and turned "welcome" into a word
    // that read as "welcomp".
    [Fact]
    public void Every_stroke_is_a_start_point_followed_by_whole_cubics()
    {
        foreach (var (c, i, n) in Halo.Widgets.Script.Strokes())
        {
            Assert.True(n >= 8, $"'{c}' stroke {i} has no curve at all");
            Assert.True((n - 2) % 6 == 0,
                $"'{c}' stroke {i} has {n} numbers - {(n - 2) % 6} short of a whole cubic, so its tail is dropped");
        }
    }

    [Fact]
    public void The_hand_can_write_every_line_the_greeting_uses()
    {
        foreach (var line in Halo.Widgets.Greeting.Lines)
            Assert.True(Halo.Widgets.Script.Can(line), $"the hand is missing a letter of \"{line}\"");
    }

    [Fact]
    public void A_wider_word_measures_wider()
        => Assert.True(Halo.Widgets.Script.Width("welcome") > Halo.Widgets.Script.Width("i'm"));
}

public class GreetingPlanTests
{
    private static float[] Clock() =>
        [0f, 0.05f, 0.1f, 0.2f, 0.3f, 0.4f, 0.5f, 0.6f, 0.7f, 0.8f, 0.9f, 0.95f, 1f];

    // A signature has one end. If the pen ever went backwards the ink would rub itself out mid-word, and
    // an overshooting ease is exactly how that happens - so the monotonicity is pinned, not assumed.
    [Fact]
    public void The_pen_never_goes_backwards()
    {
        float last = -1f;
        foreach (float t in Clock())
        {
            float w = GreetingPlan.Install(t).Written;
            Assert.True(w >= last, $"written went backwards at t={t}");
            last = w;
        }
    }

    [Fact]
    public void The_pen_never_overshoots_the_end_of_the_path()
    {
        foreach (float t in Clock())
            Assert.InRange(GreetingPlan.Install(t).Written, 0f, 1f);
    }

    [Fact]
    public void The_install_pill_is_never_smaller_than_a_collapsed_one()
    {
        foreach (float t in Clock())
        {
            var f = GreetingPlan.Install(t);
            Assert.True(f.PillW >= GreetingPlan.CollapsedW - 0.01f, $"too narrow at t={t}");
            Assert.True(f.PillH >= GreetingPlan.CollapsedH - 0.01f, $"too short at t={t}");
        }
    }

    [Fact]
    public void The_login_greeting_never_opens_the_pill()
    {
        foreach (float t in Clock())
        {
            var f = GreetingPlan.Login(t);
            Assert.Equal(GreetingPlan.CollapsedW, f.PillW);
            Assert.Equal(GreetingPlan.CollapsedH, f.PillH);
            Assert.Equal(0f, f.LineAlpha);
        }
    }

    // Both greetings have to leave the pill exactly as they found it, or whatever was showing before is
    // stuck behind a half-faded word.
    [Fact]
    public void Both_greetings_end_with_an_empty_collapsed_pill()
    {
        var install = GreetingPlan.Install(1f);
        Assert.Equal(GreetingPlan.CollapsedW, install.PillW, 1);
        Assert.Equal(0f, install.LineAlpha, 2);
        Assert.Equal(0f, install.HelloAlpha, 2);

        var login = GreetingPlan.Login(1f);
        Assert.Equal(0f, login.HelloAlpha, 2);
    }

    // The two lines cross over rather than meeting exactly: a frame with neither on the page reads as the
    // animation having stopped. What must never happen is the FIRST line reappearing after the second.
    [Fact]
    public void The_second_line_replaces_the_first_and_never_the_other_way_round()
    {
        bool sawSecond = false;
        for (float t = 0.5f; t <= 1f; t += 0.01f)
        {
            var f = GreetingPlan.Install(t);
            if (f.LineIndex == 1 && f.LineAlpha > 0.01f) sawSecond = true;
            else if (sawSecond && f.LineIndex == 0 && f.LineAlpha > 0.01f)
                Assert.Fail($"the first line came back at t={t}");
        }
        Assert.True(sawSecond, "the second line never appeared at all");
    }

    [Fact]
    public void The_signature_is_gone_before_the_first_line_is_fully_up()
    {
        for (float t = 0f; t <= 1f; t += 0.01f)
        {
            var f = GreetingPlan.Install(t);
            Assert.True(f.HelloAlpha < 0.5f || f.LineAlpha < 0.5f,
                $"the signature and a line were both solid at t={t}");
        }
    }
}
