using OpenTab.Music;
using Godot;
using System.Collections.Generic;

internal static class TrackSettingsDialogTests
{
    public static void Run()
    {
        var familyColors = new HashSet<Color>();
        for (var category = 0; category <= 16; category++)
            Check(familyColors.Add(TrackSettingsDialog.DefaultColorForCategory(category)), $"Instrument category {category} shares another category's default color.");

        CheckSound(TrackKind.Guitar, 25, "acoustic-guitar", ExpectedImagePath("acoustic-guitar.png"));
        CheckSound(TrackKind.Guitar, 29, "guitar", ExpectedImagePath("guitar.png"));
        CheckSound(TrackKind.Bass, 33, "e-bass", ExpectedImagePath("e-bass.png"));
        CheckSound(TrackKind.Keyboard, 0, "piano", ExpectedImagePath("piano.png"));
        CheckSound(TrackKind.Percussion, 0, "percussion-kit", ExpectedImagePath("percussion-kit.svg"));
        CheckSound(TrackKind.Other, 40, "violin", ExpectedImagePath("violin.png"));
        CheckSound(TrackKind.Other, 104, "placeholder", "res://_project/icon.png");

        Check(TrackSettingsDialog.DefaultImageIdForProgram(25) == "acoustic-guitar", "Steel acoustic guitar program did not use its matching image.");
        Check(TrackSettingsDialog.DefaultImageIdForProgram(29) == "guitar", "Overdriven guitar program did not use the guitar image.");
        Check(TrackSettingsDialog.DefaultImageIdForProgram(32) == "e-bass", "Bass programs should default to the electric bass image.");
        Check(TrackSettingsDialog.DefaultImageIdForProgram(0) == "piano", "Piano program did not use its matching image.");
        Check(TrackSettingsDialog.DefaultImageIdForProgram(8) == "piano", "Celesta should use keyboard artwork.");
        Check(TrackSettingsDialog.DefaultImageIdForProgram(13) == "xylophone", "Xylophone program did not use its matching image.");
        Check(TrackSettingsDialog.DefaultImageIdForProgram(71) == "clarinet", "Clarinet program did not use its matching image.");
        Check(TrackSettingsDialog.DefaultImageIdForProgram(57) == "trombone", "Trombone program did not use its matching image.");
        Check(TrackSettingsDialog.DefaultImageIdForProgram(105) == "banjo", "Banjo program did not use its matching image.");
        Check(TrackSettingsDialog.DefaultImageIdForProgram(104) == "placeholder", "Sitar should not be labelled with banjo artwork.");
        Console.WriteLine("PASS sound-derived track icons and GM image defaults");
    }

    private static string ExpectedImagePath(string filename) =>
        $"res://Interface/Common/Graphs/instruments/{filename}";

    private static void CheckSound(TrackKind kind, int program, string expectedId, string expectedPath)
    {
        var track = new ScoreTrack(kind.ToString(), kind, program)
        {
            BannerImagePath = "res://Interface/Common/Graphs/banners/hungquach679png-edm-7158021_1920.png"
        };
        Check(TrackSettingsDialog.ResolveImageId(track) == expectedId, $"{kind} program {program} did not resolve to its sound icon.");
        Check(TrackSettingsDialog.ResolveImagePath(track) == expectedPath, $"{kind} program {program} did not resolve to its sound icon resource.");
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
