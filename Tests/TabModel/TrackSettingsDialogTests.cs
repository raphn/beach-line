using OpenTab.Music;
using Godot;
using System.Collections.Generic;
using System.IO;

internal static class TrackSettingsDialogTests
{
    private static readonly (string Id, string Filename)[] AvailableImages =
    [
        ("accordion", "accordion.png"),
        ("acoustic-guitar", "acoustic-guitar.png"),
        ("banjo", "banjo.png"),
        ("cello", "cello.png"),
        ("clarinet", "clarinet.png"),
        ("e-bass", "e-bass.png"),
        ("flute", "flute.png"),
        ("french-horn", "french-horn.png"),
        ("guitar", "guitar.png"),
        ("harp", "harp.png"),
        ("piano", "piano.png"),
        ("saxophone", "saxophone.png"),
        ("tambourine", "tambourine.png"),
        ("trombone", "trombone.png"),
        ("trumpet", "trumpet.png"),
        ("ukulele", "ukulele.png"),
        ("violin", "violin.png"),
        ("xylophone", "xylophone.png"),
        ("percussion-kit", "percussion-kit.svg")
    ];

    public static void Run()
    {
        var familyColors = new HashSet<Color>();
        for (var category = 0; category <= 16; category++)
            Check(familyColors.Add(TrackSettingsDialog.DefaultColorForCategory(category)), $"Instrument category {category} shares another category's default color.");

        var ids = TrackSettingsDialog.SelectableImageIds;
        Check(ids.Count == AvailableImages.Length + 1, "The picker should include all instrument images and retain its legacy placeholder ID.");
        foreach (var (id, filename) in AvailableImages)
        {
            Check(ids.Contains(id), $"The {id} image is not selectable.");
            var track = new ScoreTrack("Image test", TrackKind.Other) { InstrumentImageId = id };
            Check(TrackSettingsDialog.ResolveImageId(track) == id, $"Explicit image ID {id} was not preserved.");
            Check(TrackSettingsDialog.ResolveImagePath(track) == ExpectedImagePath(filename),
                $"Image ID {id} did not resolve to its image resource.");
        }

        CheckDefault(TrackKind.Guitar, "acoustic-guitar", ExpectedImagePath("acoustic-guitar.png"));
        CheckDefault(TrackKind.Bass, "guitar", ExpectedImagePath("guitar.png"));
        CheckDefault(TrackKind.Keyboard, "piano", ExpectedImagePath("piano.png"));
        CheckDefault(TrackKind.Percussion, "percussion-kit", ExpectedImagePath("percussion-kit.svg"));
        CheckDefault(TrackKind.Other, "placeholder", "res://_project/icon.png");

        CheckInvalidId(TrackKind.Guitar, "acoustic-guitar", ExpectedImagePath("acoustic-guitar.png"));
        CheckInvalidId(TrackKind.Bass, "guitar", ExpectedImagePath("guitar.png"));
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
        Console.WriteLine("PASS track image choices, compatibility fallbacks, and GM image defaults");
    }

    private static string ExpectedImagePath(string filename)
    {
        var relativePath = Path.Combine("Interface", "Common", "Graphs", "instruments", filename);
        for (var directory = new DirectoryInfo(System.Environment.CurrentDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, relativePath)))
                return $"res://Interface/Common/Graphs/instruments/{filename}";
            if (File.Exists(Path.Combine(directory.FullName, "App", relativePath)))
                return $"res://Interface/Common/Graphs/instruments/{filename}";
        }
        return "res://Interface/Common/Graphs/guitar_lorc.png";
    }

    private static void CheckDefault(TrackKind kind, string expectedId, string expectedPath)
    {
        var track = new ScoreTrack(kind.ToString(), kind);
        Check(TrackSettingsDialog.ResolveImageId(track) == expectedId, $"{kind} did not resolve to its existing default image.");
        Check(TrackSettingsDialog.ResolveImagePath(track) == expectedPath, $"{kind} did not resolve to its expected image path.");
    }

    private static void CheckInvalidId(TrackKind kind, string expectedId, string expectedPath)
    {
        var track = new ScoreTrack(kind.ToString(), kind) { InstrumentImageId = "../unknown.png" };
        Check(TrackSettingsDialog.ResolveImageId(track) == expectedId, $"Invalid image ID did not fall back for {kind}.");
        Check(TrackSettingsDialog.ResolveImagePath(track) == expectedPath, $"Invalid image ID produced an unsafe path for {kind}.");
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
