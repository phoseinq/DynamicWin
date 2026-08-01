using System;

namespace Halo.Shell;

// What the greeting looks like at one instant. Everything the drawing needs, and nothing about when.
internal readonly record struct GreetingFrame(
    float PillW,
    float PillH,
    float Radius,
    float Written,        // 0..1 of the signature laid down
    float HelloAlpha,
    float LineWritten,    // 0..1 of the current line laid down - it is written, not faded up
    float LineAlpha,
    int LineIndex);       // which of Greeting.Lines is on the page

// The two greetings, as pure functions of a 0..1 clock.
//
// Split out of the controller the way NotchVisibility and AgentNoticeCoordinator were, because timing is
// exactly the part that has to be unit-testable: "the pen never goes backwards", "the pill is never
// smaller than collapsed", "the second line never overlaps the first" are all claims about numbers, and
// none of them can be settled by looking at a still.
//
// Two functions rather than one with a flag. They are different animations: install owns an expanded pill
// and gets to use it, login has to say the same thing inside a pill that never opens and is about to go
// back to work.
internal static class GreetingPlan
{
    internal const int CollapsedW = 220, CollapsedH = 40, CollapsedR = 20;
    // Sized to the signature, not to the existing expanded pill. The ink is 3.1:1, so a 190-tall pill left
    // a band of empty glass under the word that read as a mistake rather than as space.
    internal const int OpenW = 460, OpenH = 150, OpenR = 30;

    // Long, because the writing has to be readable as writing. At 6.4s the two short lines were laid down
    // in about a second each and read as being stamped rather than written - the signature had nearly
    // twice as long for barely more ink. The stages below give every line roughly the same pen SPEED,
    // which is the thing the eye is actually judging.
    internal const float InstallSeconds = 10.2f;
    internal const float LoginSeconds = 2.6f;

    // install: open, write, hold, clear, say who it is, hold, close.
    internal static GreetingFrame Install(float t)
    {
        t = Math.Clamp(t, 0f, 1f);

        // the pill leads and trails the ink - it is open before the pen lands and stays open until after
        // the last word has gone, so neither the shape nor the writing is ever seen mid-morph
        float open = Span(t, 0f, 0.07f), shut = Span(t, 0.96f, 1f);
        float size = EaseOutBack(open) * (1f - EaseInOut(shut));
        // LINEAR, and every writing stage below is too. Easing the pen was the mistake: EaseOutSine is
        // fastest the instant it starts and crawls into the finish, which is why the middle of a word
        // raced and the end dragged. A hand writing a word keeps roughly one speed, and since the progress
        // is paced by ink length rather than by parameter, linear here IS constant speed along the stroke.
        float written = Span(t, 0.04f, 0.25f);   // ~2.1s

        // Every line is WRITTEN, the signature and the two after it alike - the pill has one hand, and a
        // line that faded up while the one before it was drawn looked like two different surfaces taking
        // turns. What is left of the crossfade is only the leaving: a finished line fades out under the
        // next one starting, so there is never a frame of empty pill in the middle of the greeting.
        // Every line gets the same ~2.1s of pen and the same beat to be read afterwards. "welcome" used to
        // get 1.6s and then leave immediately, which is why it went past before it could be read - it was
        // the last line, so its hold had been quietly eaten by the pill closing on top of it.
        // The leaving is slower than the writing by design. A word that vanishes faster than it arrived
        // reads as being taken away; at ~0.9s it reads as settling.
        float helloOut = EaseInOut(Span(t, 0.29f, 0.38f));
        float write1 = Span(t, 0.35f, 0.56f), out1 = EaseInOut(Span(t, 0.62f, 0.71f));
        float write2 = Span(t, 0.67f, 0.88f), out2 = EaseInOut(Span(t, 0.92f, 1f));

        bool second = write2 > 0f;
        return new GreetingFrame(
            Lerp(CollapsedW, OpenW, size),
            Lerp(CollapsedH, OpenH, size),
            Lerp(CollapsedR, OpenR, size),
            written,
            (1f - helloOut) * Math.Min(1f, open * 3f),
            second ? write2 : write1,
            second ? 1f - out2 : (write1 <= 0f ? 0f : 1f - out1),
            second ? 1 : 0);
    }

    // login: the pill does not open at all. Same hand, smaller, and gone before it is in the way.
    internal static GreetingFrame Login(float t)
    {
        t = Math.Clamp(t, 0f, 1f);
        float written = Span(t, 0.04f, 0.58f);   // linear, for the same reason as the install hand
        float fade = EaseInOut(Span(t, 0.78f, 1f));
        return new GreetingFrame(CollapsedW, CollapsedH, CollapsedR, written, 1f - fade, 0f, 0f, 0);
    }

    // 0 before a, 1 after b, linear between - the building block every stage above is cut from
    internal static float Span(float t, float a, float b)
        => b <= a ? (t >= b ? 1f : 0f) : Math.Clamp((t - a) / (b - a), 0f, 1f);

    private static float Lerp(float a, float b, float t) => a + (b - a) * t;

    // The pen decelerates into the last stroke rather than stopping dead, which is what a hand does. Back
    // easing is deliberately NOT used on the writing: an overshoot would run the pen past the end of the
    // path and snap it back, and the path is a signature - it has one end.
    private static float EaseOutSine(float t) => MathF.Sin(Math.Clamp(t, 0f, 1f) * MathF.PI / 2f);

    private static float EaseInOut(float t)
    {
        t = Math.Clamp(t, 0f, 1f);
        return t < 0.5f ? 2f * t * t : 1f - MathF.Pow(-2f * t + 2f, 2f) / 2f;
    }

    private static float EaseOutBack(float t)
    {
        t = Math.Clamp(t, 0f, 1f);
        const float c1 = 1.70158f, c3 = c1 + 1f;
        return 1f + c3 * MathF.Pow(t - 1f, 3f) + c1 * MathF.Pow(t - 1f, 2f);
    }
}
