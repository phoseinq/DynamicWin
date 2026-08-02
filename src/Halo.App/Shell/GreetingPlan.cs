using System;

namespace Halo.Shell;

internal readonly record struct GreetingFrame(
    float PillW,
    float PillH,
    float Radius,
    float Written,
    float HelloAlpha,
    float LineWritten,
    float LineAlpha,
    int LineIndex);

internal static class GreetingPlan
{
    internal const int CollapsedW = 220, CollapsedH = 40, CollapsedR = 20;

    internal const int OpenW = 460, OpenH = 150, OpenR = 30;

    internal const float InstallSeconds = 10.2f;
    internal const float LoginSeconds = 2.6f;

    internal static GreetingFrame Install(float t)
    {
        t = Math.Clamp(t, 0f, 1f);

        float open = Span(t, 0f, 0.07f), shut = Span(t, 0.96f, 1f);
        float size = EaseOutBack(open) * (1f - EaseInOut(shut));

        float written = Span(t, 0.04f, 0.25f);

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

    internal static GreetingFrame Login(float t)
    {
        t = Math.Clamp(t, 0f, 1f);
        float written = Span(t, 0.04f, 0.58f);
        float fade = EaseInOut(Span(t, 0.78f, 1f));
        return new GreetingFrame(CollapsedW, CollapsedH, CollapsedR, written, 1f - fade, 0f, 0f, 0);
    }

    internal static float Span(float t, float a, float b)
        => b <= a ? (t >= b ? 1f : 0f) : Math.Clamp((t - a) / (b - a), 0f, 1f);

    private static float Lerp(float a, float b, float t) => a + (b - a) * t;

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
