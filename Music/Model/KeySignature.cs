using System;

namespace OpenTab.Music;

public enum KeyMode
{
    Major,
    Minor
}

/// <summary>
/// A conventional key signature represented by its circle-of-fifths position.
/// Fifths ranges from seven flats (-7) through no accidentals (0) to seven sharps (+7).
/// </summary>
public readonly record struct KeySignature
{
    private static readonly char[] SharpOrder = ['F', 'C', 'G', 'D', 'A', 'E', 'B'];
    private static readonly char[] FlatOrder = ['B', 'E', 'A', 'D', 'G', 'C', 'F'];
    private static readonly string[] MajorTonics =
    [
        "Cb", "Gb", "Db", "Ab", "Eb", "Bb", "F", "C", "G", "D", "A", "E", "B", "F#", "C#"
    ];
    private static readonly string[] MinorTonics =
    [
        "Ab", "Eb", "Bb", "F", "C", "G", "D", "A", "E", "B", "F#", "C#", "G#", "D#", "A#"
    ];

    public KeySignature(int fifths, KeyMode mode = KeyMode.Major)
    {
        if (fifths is < -7 or > 7)
        {
            throw new ArgumentOutOfRangeException(nameof(fifths), "A conventional key signature ranges from seven flats to seven sharps.");
        }

        if (mode is not (KeyMode.Major or KeyMode.Minor))
        {
            throw new ArgumentOutOfRangeException(nameof(mode));
        }

        Fifths = fifths;
        Mode = mode;
    }

    public static KeySignature CMajor => new(0, KeyMode.Major);
    public static KeySignature AMinor => new(0, KeyMode.Minor);

    public int Fifths { get; }
    public KeyMode Mode { get; }

    public string Tonic => (Mode == KeyMode.Major ? MajorTonics : MinorTonics)[Fifths + 7];
    public string Name => $"{Tonic} {(Mode == KeyMode.Major ? "major" : "minor")}";

    /// <summary>
    /// Returns the key-signature alteration for a natural note letter: -1 for flat,
    /// 0 for natural, and +1 for sharp.
    /// </summary>
    public int AlterationForNaturalNote(char noteLetter)
    {
        var letter = char.ToUpperInvariant(noteLetter);
        if (letter is < 'A' or > 'G')
        {
            throw new ArgumentOutOfRangeException(nameof(noteLetter), "Note letter must be A through G.");
        }

        if (Fifths > 0)
        {
            return Array.IndexOf(SharpOrder, letter) < Fifths ? 1 : 0;
        }

        if (Fifths < 0)
        {
            return Array.IndexOf(FlatOrder, letter) < -Fifths ? -1 : 0;
        }

        return 0;
    }

    public override string ToString() => Name;
}
