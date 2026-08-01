# The greeting hand

How to finish the alphabet the pill writes with. Read this before touching `src/Halo.App/Widgets/Script.cs`.

## What is wrong with the current alphabet

`Script.cs` holds a first-pass hand authored from nothing, letter by letter, without a reference. It is
legible and it animates correctly, but it does not match the signature it sits under: the letters stand
apart while Apple's `hello` is joined, and the shapes are uneven because they were drawn blind. The user's
words for it were "crooked", and they are right.

Do not fix it by nudging the existing numbers. The method was the mistake, not the values.

## The method that works

Four of the letters needed are already drawn, at full quality, in the signature itself — `hello` contains
`h`, `e`, `l` and `o`. Lift those out of the source path and only author what is missing.

`Widgets/Greeting.Stroke` is the signature as 21 cubic beziers, flat in one array: a start point, then six
numbers per curve. Segmented by eye off the per-curve spans, the letters fall out cleanly:

| letter | curves | starts at x | ends at x | notes |
|--------|--------|-------------|-----------|-------|
| `h`    | 0–4    | −145.7      | −69.0     | curve 0 is the long lead-in stroke — word-initial only |
| `e`    | 5–7    | −69.0       | −23.5     | crossbar first, then the bowl |
| `l`    | 8–10   | −23.5       | 13.0      | |
| `l`    | 11–13  | 13.0        | 59.9      | the second `l` is wider than the first — they are not copies |
| `o`    | 14–20  | 59.9        | 138.3     | curves 19–20 are the exit flourish — word-final only |

**Every join sits on the baseline**, y between 40.3 and 41.6. That is the whole reason this works: a
letter extracted this way begins and ends where its neighbours do, so words chain into real joined
cursive instead of a row of separate marks. Any letter authored to fill the gaps must enter and leave at
y ≈ 41 with the pen still down, or it will break the chain at exactly the place the eye is watching.

Still to author, for `i'm halo` and `welcome`: `a`, `i`, `m`, `w`, `c`, and the apostrophe. Draw each one
next to the extracted `e` and `o` for scale and slant — the x-height runs baseline y ≈ 41 up to y ≈ −26,
ascenders to y ≈ −46.

Two glyphs need variants rather than one shape: `h` and `o` above carry a lead-in and an exit that are
right at the edges of a word and wrong in the middle of one.

## The trap in the data format

A stroke is a start point followed by whole cubics, so its length must be `2 + 6n` numbers. Author one
short and the final curve is dropped **in silence** — it parses, it draws, it is simply missing its tail.
This shipped once: `e` lost its exit stroke and the word `welcome` read as `welcomp`. `ScriptTests` pins
the arithmetic now, so the failure is loud, but the format still invites it.

## Pacing

Speed is per line, in `GreetingPlan`, and what the eye judges is pen speed, not duration — a short line
given the same seconds as a long one looks stamped rather than written. The current stages give `hello`
2.15s, `i'm halo` 2.06s and `welcome` 1.63s, which is roughly constant ink per second. Re-check that
ratio after changing any letterform, because changing a shape changes its length.

## Provenance

The signature is Apple's Macintosh `hello`, supplied by the user, who chose to use it as-is after the
trademark question was raised. Anything authored to sit beside it inherits that decision.
