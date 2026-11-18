namespace Scada.Core.Models;

public static class Backoff
{
    public static int Next(int previousMs, int baseMs, int capMs)
    {
        var next = previousMs <= 0 ? baseMs : (int)Math.Min((long)previousMs * 2, capMs);
        return Jitter(next, capMs);
    }

    public static int Jitter(int delayMs, int capMs = int.MaxValue)
    {
        var value = (int)Math.Round(delayMs * (0.75 + Random.Shared.NextDouble() * 0.5));
        return Math.Clamp(value, 1, capMs);
    }
}