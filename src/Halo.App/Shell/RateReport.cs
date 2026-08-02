using System;
using System.IO;

namespace Halo.Shell;

// A rate the pill ASKS for is not a rate it gets. DispatcherQueueTimer cannot beat the platform's timer
// resolution, so a 3.571ms period on a machine that has not had it raised delivers whatever it delivers,
// and the settings row would otherwise be repeating the number the user picked back at them. This
// measures the morph - the one stretch where the pill genuinely tries to run flat out - and the panel
// shows that instead.
internal struct MorphRate
{
    // Short movements are not evidence: a three-frame morph divides by a duration barely longer than one
    // tick, so the answer is mostly the noise in where the samples landed.
    internal const int MinFrames = 8;
    internal const double MinSeconds = 0.06;

    private int _frames;
    private double _seconds;

    // fps of the last morph long enough to mean anything; 0 until one has been seen
    internal int Measured { get; private set; }

    // true only when a morph has just ended and the number changed, so the caller writes on an edge
    // rather than every frame
    internal bool Step(bool morphing, double dt)
    {
        if (morphing)
        {
            _frames++;
            _seconds += dt;
            return false;
        }
        if (_frames == 0) return false;
        int frames = _frames;
        double seconds = _seconds;
        _frames = 0;
        _seconds = 0;
        if (frames < MinFrames || seconds < MinSeconds) return false;
        int fps = (int)Math.Round(frames / seconds);
        if (fps == Measured) return false;
        Measured = fps;
        return true;
    }
}

// The settings panel is a separate executable that shares no code with the pill, so what it knows about
// the running app it reads off disk - the same loose-file seam as offset, pin and tray.
internal static class RateReport
{
    // Two integers and nothing else: a file the panel parses is a contract, and a formatted sentence
    // would put the panel's wording inside the pill.
    internal static string Format(int measured, int hz) => $"{measured} {hz}";

    private static string _written = "";

    internal static string Path => System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Halo", "fps");

    internal static void Write(int measured, int hz)
    {
        string line = Format(measured, hz);
        if (line == _written) return;
        _written = line;
        try
        {
            string path = Path;
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
            File.WriteAllText(path, line);
        }
        catch { }
    }
}
