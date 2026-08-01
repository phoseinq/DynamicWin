using System;
using Halo.Shell;
using Xunit;

namespace Halo.Tests;

public class GreetingGateTests
{
    private static readonly DateTime Boot = new(2026, 8, 1, 9, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void No_stamp_means_this_machine_has_never_seen_it()
        => Assert.Equal(GreetingKind.Install, GreetingGate.Decide(null, Boot));

    [Fact]
    public void An_unreadable_stamp_greets_rather_than_staying_silent_forever()
        => Assert.Equal(GreetingKind.Install, GreetingGate.Decide("not a date", Boot));

    // The one that matters: the settings panel restarts Halo on apply, and the boot it comes back to is
    // the same boot it left. A greeting there would fire on every changed setting.
    [Fact]
    public void A_restart_inside_the_same_windows_session_says_nothing()
    {
        var stamp = GreetingGate.Stamp(Boot.AddSeconds(0.4));   // same boot, measured a moment later
        Assert.Equal(GreetingKind.None, GreetingGate.Decide(stamp, Boot));
    }

    [Fact]
    public void The_first_run_after_a_reboot_gets_the_short_greeting()
    {
        var stamp = GreetingGate.Stamp(Boot.AddDays(-1));
        Assert.Equal(GreetingKind.Login, GreetingGate.Decide(stamp, Boot));
    }

    // a clock moved backwards is not a boot that has not happened yet
    [Fact]
    public void A_stamp_from_the_future_is_treated_as_a_new_session_not_ignored()
    {
        var stamp = GreetingGate.Stamp(Boot.AddHours(5));
        Assert.Equal(GreetingKind.Login, GreetingGate.Decide(stamp, Boot));
    }

    [Fact]
    public void Drift_inside_the_slack_is_still_the_same_session()
    {
        var stamp = GreetingGate.Stamp(Boot.AddSeconds(-(GreetingGate.Slack.TotalSeconds / 2)));
        Assert.Equal(GreetingKind.None, GreetingGate.Decide(stamp, Boot));
    }
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
