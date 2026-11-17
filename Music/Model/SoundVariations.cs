using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;

namespace OpenTab.Music;

/// <summary>One supported alternate sound for the bundled default sound font.</summary>
public sealed record SoundVariationOption(string Id, string DisplayName);

/// <summary>Catalog of supported approximations for per-note sound changes.</summary>
public static class SoundVariations
{
    private static readonly SoundVariationOption PalmMute = new("palm_mute", "Palm mute");
    private static readonly SoundVariationOption Harmonics = new("harmonics", "Guitar harmonics");
    private static readonly SoundVariationOption TremoloStrings = new("tremolo", "Tremolo strings");
    private static readonly SoundVariationOption PizzicatoStrings = new("pizzicato", "Pizzicato strings");
    private static readonly SoundVariationOption MutedTrumpet = new("muted_trumpet", "Muted trumpet");
    private static readonly SoundVariationOption ClosedHat = new("closed", "Closed hi-hat");
    private static readonly SoundVariationOption PedalHat = new("pedal", "Pedal hi-hat");
    private static readonly SoundVariationOption OpenHat = new("open", "Open hi-hat");
    private static readonly SoundVariationOption MutedCuica = new("muted", "Muted");
    private static readonly SoundVariationOption OpenCuica = new("open", "Open");
    private static readonly SoundVariationOption MutedTriangle = new("muted", "Muted");
    private static readonly SoundVariationOption OpenTriangle = new("open", "Open");
    private static readonly IReadOnlyList<SoundVariationOption> GuitarFamilyOptions = Freeze(PalmMute, Harmonics);
    private static readonly IReadOnlyList<SoundVariationOption> StringOptions = Freeze(TremoloStrings, PizzicatoStrings);
    private static readonly IReadOnlyList<SoundVariationOption> TrumpetOptions = Freeze(MutedTrumpet);
    private static readonly IReadOnlyList<SoundVariationOption> HatOptions = Freeze(ClosedHat, PedalHat, OpenHat);
    private static readonly IReadOnlyList<SoundVariationOption> CuicaOptions = Freeze(MutedCuica, OpenCuica);
    private static readonly IReadOnlyList<SoundVariationOption> TriangleOptions = Freeze(MutedTriangle, OpenTriangle);

    /// <summary>Returns supported non-default options for the track program and optional percussion lane pitch.</summary>
    public static IReadOnlyList<SoundVariationOption> GetOptions(int program, int percussionPitch = -1)
    {
        if (percussionPitch >= 0)
        {
            if (percussionPitch is 42 or 44 or 46) return HatOptions;
            if (percussionPitch is 78 or 79) return CuicaOptions;
            if (percussionPitch is 80 or 81) return TriangleOptions;
            return Array.Empty<SoundVariationOption>();
        }
        if (program is >= 24 and <= 30) return GuitarFamilyOptions;
        if (program is 48 or 49) return StringOptions;
        if (program == 56) return TrumpetOptions;
        return Array.Empty<SoundVariationOption>();
    }

    /// <summary>Resolves a guitar preset choice, preserving the original program for unknown or inapplicable IDs.</summary>
    public static int ResolveProgram(int program, string variationId)
    {
        if (program is >= 24 and <= 30)
            return variationId switch { "palm_mute" => 28, "harmonics" => 31, _ => program };
        if (program is 48 or 49)
            return variationId switch { "tremolo" => 44, "pizzicato" => 45, _ => program };
        if (program == 56 && string.Equals(variationId, MutedTrumpet.Id, StringComparison.Ordinal)) return 59;
        return program;
    }

    /// <summary>Resolves an alternate GM percussion pitch within the lane's sound family.</summary>
    public static int ResolvePercussionPitch(int pitch, string variationId)
    {
        if (pitch is 42 or 44 or 46)
            return variationId switch { "closed" => 42, "pedal" => 44, "open" => 46, _ => pitch };
        if (pitch is 78 or 79)
            return variationId switch { "muted" => 78, "open" => 79, _ => pitch };
        if (pitch is 80 or 81)
            return variationId switch { "muted" => 80, "open" => 81, _ => pitch };
        return pitch;
    }

    private static IReadOnlyList<SoundVariationOption> Freeze(params SoundVariationOption[] options) =>
        new ReadOnlyCollection<SoundVariationOption>(options);
}
