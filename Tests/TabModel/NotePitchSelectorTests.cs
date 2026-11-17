internal static class NotePitchSelectorTests
{
    public static void Run()
    {
        for (int midi = 0; midi <= 127; midi++)
        {
            var text = NotePitchSelector.FormatPitch(midi);
            Check(NotePitchSelector.TryParsePitch(text, out var parsed) && parsed == midi, $"Pitch {midi} did not round-trip.");
        }
        Check(NotePitchSelector.TryParsePitch(" g♯2 ", out var sharp) && sharp == 44, "Sharp notation failed.");
        Check(NotePitchSelector.TryParsePitch("E4", out var standard) && standard == 64, "Standard guitar pitch failed.");
        foreach (var text in new[] { "", "H4", "C", "C#", "Bb3", "C-2", "G#9", "C2147483647", "C4x" })
            Check(!NotePitchSelector.TryParsePitch(text, out _), $"Invalid pitch '{text}' was accepted.");
        Console.WriteLine("PASS note pitch selector notation and bounds");
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
