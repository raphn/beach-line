#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using OpenTab.Music;

namespace OpenTab.Music.Persistence;

/// <summary>Versioned OpenTab archive containing a JSON score document.</summary>
public static class ScoreFile
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public static byte[] Serialize(ScoreDocument score, bool portable = false)
    {
        ValidateExpressionLinks(score);
        AudioModelValidation.ValidateAudio(score);
        ValidatePedals(score.Tracks);
        var document = new Document
        {
            Version = 17,
            Id = score.Id, Title = score.Title, Tempo = TempoData.From(score.DefaultTempo),
            Meter = MeterData.From(score.DefaultTimeSignature), Key = KeyData.From(score.DefaultKeySignature),
            PercussionMasterGainDb = score.PercussionMasterGainDb,
            MetronomeEnabled = score.MetronomeEnabled,
            CountInEnabled = score.CountInEnabled,
            CountInEveryLoop = score.CountInEveryLoop,
            Arrangements = score.Arrangements.Select(a => new ArrangementData { Id = a.Id, Name = a.Name,
                Sections = a.Sections.Select(e => new ArrangementSectionData { SectionId = e.SectionId, Repetitions = e.Repetitions }).ToList(),
                AudioClips = a.AudioClips.Select(AudioClipData.From).ToList() }).ToList(),
            AudioAssets = score.AudioAssets.Select(asset =>
            {
                var data = AudioAssetData.From(asset);
                if (portable) data.CachePath = "embedded/" + asset.Id.ToString("N");
                return data;
            }).ToList(),
            Tracks = score.Tracks.Select(t => new TrackData { Id = t.Id, Name = t.Name, Kind = t.Kind,
                Program = t.DefaultMidiProgram, Volume = t.VolumeDb, Mute = t.IsMuted, Solo = t.IsSoloed,
                TabTuning = t.TabTuningMidiPitches.ToList(), MaxFret = t.MaxFret,
                BannerImagePath = t.BannerImagePath, DisplayColorRgba = t.DisplayColorRgba, TabSnapDenominator = t.TabSnapDenominator,
                Pedals = t.Pedals.Select(PedalData.From).ToList(),
                PercussionPieces = t.HasConfiguredPercussionKit ? t.PercussionPieces.Select(piece => new PercussionPieceData { Id = piece.Id, Name = piece.Name,
                    MidiPitch = piece.MidiPitch, IsMuted = piece.IsMuted, GainDb = piece.GainDb }).ToList() : null }).ToList(),
            Sections = score.Sections.Select(s => new SectionData { Id = s.Id, Name = s.Name, Title = s.Title,
                Lead = s.LeadTrackId, Tempo = s.DefaultTempo is null ? null : TempoData.From(s.DefaultTempo),
                Meter = s.DefaultTimeSignature is { } m ? MeterData.From(m) : null,
                Key = s.DefaultKeySignature is { } k ? KeyData.From(k) : null,
                Patterns = score.Tracks.ToDictionary(t => t.Id, t => score.GetTrackPatternLength(s, t)),
                Measures = s.Measures.Select(MeasureData.From).ToList(),
                AudioClips = s.AudioClips.Select(AudioClipData.From).ToList() }).ToList()
        };
        using var bytes = new MemoryStream();
        using (var zip = new ZipArchive(bytes, ZipArchiveMode.Create, true))
        using (var stream = CreateProjectEntry(zip).Open())
            JsonSerializer.Serialize(stream, document, JsonOptions);
        return bytes.ToArray();
    }

    private static ZipArchiveEntry CreateProjectEntry(ZipArchive zip)
    {
        var entry = zip.CreateEntry("project.json", CompressionLevel.Optimal);
        // Stable bytes make score snapshots comparable for undo history and reproducible saves.
        entry.LastWriteTime = new DateTimeOffset(2000, 1, 1, 0, 0, 0, TimeSpan.Zero);
        return entry;
    }

    public static ScoreDocument Deserialize(byte[] bytes)
    {
        using var stream = new MemoryStream(bytes, false);
        using var zip = new ZipArchive(stream, ZipArchiveMode.Read);
        var entry = zip.GetEntry("project.json") ?? throw new InvalidDataException("The score archive has no project.json.");
        if (entry.Length > 64 * 1024 * 1024) throw new InvalidDataException("The score document is too large.");
        using var json = entry.Open();
        var data = JsonSerializer.Deserialize<Document>(json, JsonOptions) ?? throw new InvalidDataException("The score is empty.");
        if (data.Version is not (1 or 2 or 3 or 4 or 5 or 6 or 7 or 8 or 9 or 10 or 11 or 12 or 13 or 14 or 15 or 16 or 17) || data.Sections.Count == 0) throw new InvalidDataException("Unsupported score version or missing section.");
        var score = new ScoreDocument(data.Title, data.Tempo.ToValue(), data.Meter.ToValue(), data.Key.ToValue())
        {
            Id = data.Id,
            PercussionMasterGainDb = data.PercussionMasterGainDb,
            MetronomeEnabled = data.Version >= 5 && data.MetronomeEnabled,
            CountInEnabled = data.Version >= 10 && data.CountInEnabled,
            CountInEveryLoop = data.Version >= 10 && data.CountInEveryLoop
        };
        foreach (var t in data.Tracks)
        {
            var track = new ScoreTrack(t.Name, t.Kind, t.Program) { Id = t.Id, VolumeDb = t.Volume, IsMuted = t.Mute, IsSoloed = t.Solo,
                BannerImagePath = t.BannerImagePath, DisplayColorRgba = t.DisplayColorRgba,
                TabSnapDenominator = data.Version >= 14 ? t.TabSnapDenominator : null };
            if (data.Version >= 15)
                foreach (var pedal in t.Pedals ?? new())
                {
                    if (pedal is null) throw new InvalidDataException("A track pedal is missing.");
                    track.Pedals.Add(pedal.ToValue());
                }
            if (data.Version >= 2) track.RestoreTabConfiguration(t.TabTuning ?? new List<int>(), t.MaxFret);
            if (track.Kind == TrackKind.Percussion && data.Version >= 4 && t.PercussionPieces is not null)
            {
                var pieces = t.PercussionPieces;
                track.ConfigurePercussionPieces(pieces.Select(piece => new PercussionPiece(piece.Id, piece.Name, piece.MidiPitch)
                {
                    IsMuted = piece.IsMuted,
                    GainDb = piece.GainDb
                }));
            }
            else if (track.Kind == TrackKind.Percussion && data.Version < 4 && track.TabTuningMidiPitches.Count != 0)
            {
                // Older files had no lane metadata. Keep their ordered pitches usable as
                // fixed-pitch lanes while giving them stable fallback identities.
                track.ConfigurePercussionPieces(track.TabTuningMidiPitches.Select((pitch, index) =>
                    new PercussionPiece($"legacy-{index + 1}-{pitch}", $"Percussion {pitch}", pitch)));
            }
            score.RestoreTrack(track);
        }
        ValidatePedals(score.Tracks);
        if (data.Version >= 9)
            foreach (var asset in data.AudioAssets ?? new()) score.AudioAssets.Add(asset.ToValue());
        var tracks = score.Tracks.ToDictionary(t => t.Id);
        for (int i = 0; i < data.Sections.Count; i++)
        {
            var s = data.Sections[i];
            var section = i == 0 ? score.Sections[0] : score.AddSection(s.Name, s.Title);
            section.Id = s.Id; section.Name = s.Name; section.Title = s.Title;
            section.DefaultTempo = s.Tempo?.ToValue(); section.DefaultTimeSignature = s.Meter?.ToValue(); section.DefaultKeySignature = s.Key?.ToValue();
            section.Measures.Clear();
            foreach (var m in s.Measures)
            {
                var measure = score.AddMeasure(section, m.Meter?.ToValue(), m.Key?.ToValue());
                measure.Id = m.Id; measure.IsPickup = m.Pickup; measure.IsFreeTime = m.FreeTime; measure.BarlineStyle = m.Barline;
                foreach (var e in m.Tempos) measure.TempoEvents.Add(new TempoEvent(new ScoreTime(e.Numerator, e.Denominator), e.Tempo.ToValue(), e.Interpolation) { Id = e.Id });
                measure.TrackMeasures.Clear();
                foreach (var part in m.Parts)
                {
                    if (!tracks.ContainsKey(part.Track)) throw new InvalidDataException("A measure refers to an unknown track.");
                    var target = new TrackMeasure(part.Track) { SnapDenominator = data.Version >= 13 ? part.SnapDenominator : 0 };
                    if (data.Version >= 3) target.RepeatCount = part.RepeatCount;
                    foreach (var v in part.Voices)
                    {
                        var voice = new Voice { Id = v.Id, Number = v.Number };
                        foreach (var b in v.Beats)
                        {
                            var beat = new Beat(b.Rhythm.ToValue()) { Id = b.Id, Intensity = b.Intensity, PitchBendSemitones = b.Bend };
                            foreach (var n in b.Notes) beat.Notes.Add(new ScoreNote(n.Pitch, n.Velocity) { Id = n.Id });
                            voice.Beats.Add(beat);
                        }
                        target.Voices.Add(voice);
                    }
                    if (data.Version >= 2)
                    {
                        target.UsesTabEvents = part.UsesTabEvents;
                        var track = tracks[part.Track];
                        foreach (var n in part.TabEvents)
                        {
                            var onset = new ScoreTime(n.OnsetNumerator, n.OnsetDenominator);
                            var duration = new ScoreTime(n.DurationNumerator, n.DurationDenominator);
                            NormalizeTabAssignment(track, n, data.Version);
                            if (track.TabLaneMidiPitches[n.StringNumber - 1] + n.Fret != n.Pitch)
                                throw new InvalidDataException("A tablature event's string, fret, and pitch do not match its track tuning.");
                            var tabEvent = new TabEvent(n.StringNumber, n.Fret, n.Pitch, n.Velocity, onset, duration) { Id = n.Id, AllowsBarOverflow = n.AllowsBarOverflow, TupletActual = data.Version >= 6 ? n.TupletActual : 0, SoundVariation = data.Version >= 8 ? n.SoundVariation ?? "" : "", IncomingArticulation = data.Version >= 12 ? n.IncomingArticulation : StringArticulation.None };
                            if (data.Version >= 7)
                            {
                                foreach (var expressionData in n.Expressions ?? new List<NoteExpressionData>())
                                {
                                    var expression = new NoteExpression { Id = expressionData.Id, Kind = expressionData.Kind,
                                        Position = expressionData.Position, MinSemitones = expressionData.MinSemitones,
                                        MaxSemitones = expressionData.MaxSemitones,
                                        Points = (expressionData.Points ?? new()).Select(point => new ExpressionPoint(point.X, point.Y, data.Version >= 12 ? point.SegmentMode : ExpressionSegmentMode.Linear)).ToList() };
                                    try { expression.Validate(); }
                                    catch (ArgumentException ex) { throw new InvalidDataException("A note expression curve is invalid.", ex); }
                                    tabEvent.Expressions.Add(expression);
                                }
                            }
                            target.TabEvents.Add(tabEvent);
                        }
                        if (!track.UsesFullTab && target.TabEvents.GroupBy(n => (n.StringNumber, n.Onset)).Any(group => group.Count() > 1))
                            throw new InvalidDataException("A string cannot have two tablature attacks at the same onset.");
                    }
                    measure.TrackMeasures.Add(target);
                }
                if (measure.TrackMeasures.Count != tracks.Count || measure.TrackMeasures.Select(p => p.TrackId).Distinct().Count() != tracks.Count)
                    throw new InvalidDataException("Each measure must contain every track exactly once.");
                if (data.Version >= 11)
                {
                    foreach (var version in m.Versions ?? new())
                    {
                        if (!tracks.ContainsKey(version.Track) || version.Content.Track != version.Track)
                            throw new InvalidDataException("A content version refers to an unknown or mismatched track.");
                        var content = DecodePart(version.Content, tracks, data.Version);
                        measure.TrackMeasureVersions.Add(new TrackMeasureVersion(version.Track, version.Name, content) { Id = version.Id });
                    }
                    foreach (var rule in m.TransitionRules ?? new())
                    {
                        if (!tracks.ContainsKey(rule.Track) || (rule.Version != Guid.Empty && !measure.TrackMeasureVersions.Any(v => v.Id == rule.Version && v.TrackId == rule.Track)))
                            throw new InvalidDataException("A transition rule refers to an unknown track or version.");
                        measure.TransitionRules.Add(new TrackMeasureTransitionRule(rule.Track, rule.Section, rule.Version));
                    }
                    foreach (var alternate in m.Alternations ?? new())
                    {
                        if (!tracks.ContainsKey(alternate.Track) || alternate.Versions.Any(id => id != Guid.Empty && !measure.TrackMeasureVersions.Any(v => v.Id == id && v.TrackId == alternate.Track)))
                            throw new InvalidDataException("An alternation refers to an unknown track or version.");
                        measure.Alternations.Add(new TrackMeasureAlternation(alternate.Track, alternate.Versions));
                    }
                }
            }
            if (s.Lead is { } lead)
            {
                if (!tracks.TryGetValue(lead, out var leadTrack)) throw new InvalidDataException("The section lead is missing.");
                score.SetLeadTrack(section, leadTrack);
            }
            else if (tracks.Count != 0) throw new InvalidDataException("The section has no lead track.");
            foreach (var (id, length) in s.Patterns)
            {
                if (!tracks.TryGetValue(id, out var track)) throw new InvalidDataException("A pattern refers to an unknown track.");
                score.SetTrackPatternLength(section, track, length);
            }
            if (data.Version >= 9)
                foreach (var clip in s.AudioClips ?? new()) section.AudioClips.Add(clip.ToValue());
        }
        foreach (var arrangement in data.Arrangements ?? new())
        {
            if (arrangement.Id == Guid.Empty || string.IsNullOrWhiteSpace(arrangement.Name) ||
                score.Arrangements.Any(a => a.Id == arrangement.Id || string.Equals(a.Name, arrangement.Name.Trim(), StringComparison.OrdinalIgnoreCase)))
                throw new InvalidDataException("An arrangement has an invalid or duplicate identity or name.");
            var restored = new ScoreArrangement(arrangement.Name.Trim()) { Id = arrangement.Id };
            foreach (var arrangementEntry in arrangement.Sections ?? new())
            {
                if (!score.Sections.Any(section => section.Id == arrangementEntry.SectionId) || arrangementEntry.Repetitions < 1 || arrangementEntry.Repetitions > ArrangementEditing.MaxRepetitions)
                    throw new InvalidDataException("An arrangement refers to an invalid section or repetition count.");
                restored.Sections.Add(new ArrangementSection(arrangementEntry.SectionId, arrangementEntry.Repetitions));
            }
            if (data.Version >= 9)
                foreach (var clip in arrangement.AudioClips ?? new()) restored.AudioClips.Add(clip.ToValue());
            score.Arrangements.Add(restored);
        }
        _ = score.CreateTempoMap();
        ValidateExpressionLinks(score);
        AudioModelValidation.ValidateAudio(score);
        return score;
    }

    private static void ValidatePedals(IEnumerable<ScoreTrack> tracks)
    {
        var identities = new HashSet<Guid>();
        try
        {
            foreach (var track in tracks)
                foreach (var pedal in track.Pedals)
                {
                    if (pedal is null || !identities.Add(pedal.Id)) throw new ArgumentException("Pedal identities must be unique.");
                    pedal.Validate();
                }
        }
        catch (ArgumentException ex) { throw new InvalidDataException("A track pedal or its automation is invalid.", ex); }
    }

    private static void ValidateExpressionLinks(ScoreDocument score)
    {
        var allExpressions = score.Sections.SelectMany(section => section.Measures)
            .SelectMany(measure => measure.TrackMeasures).SelectMany(part => part.TabEvents)
            .SelectMany(note =>
            {
                if (note.Expressions.GroupBy(expression => expression.Kind).Any(group => group.Count() > 1))
                    throw new InvalidDataException("A note cannot contain more than one expression of each kind.");
                foreach (var expression in note.Expressions) expression.Validate();
                return note.Expressions;
            }).ToArray();
        foreach (var group in allExpressions.GroupBy(expression => expression.Id))
        {
            var first = group.First();
            if (group.Any(expression => expression.Kind != first.Kind || !expression.Points.SequenceEqual(first.Points) ||
                expression.MinSemitones != first.MinSemitones || expression.MaxSemitones != first.MaxSemitones ||
                (expression.Kind != ExpressionKind.Velocity && expression.Position != first.Position)))
                throw new InvalidDataException("Expressions with the same identity must share the same definition.");
        }
    }

    private static TrackMeasure DecodePart(PartData part, IReadOnlyDictionary<Guid, ScoreTrack> tracks, int version)
    {
        var track = tracks[part.Track];
        var target = new TrackMeasure(part.Track) { RepeatCount = part.RepeatCount, UsesTabEvents = part.UsesTabEvents,
            SnapDenominator = version >= 13 ? part.SnapDenominator : 0 };
        foreach (var v in part.Voices ?? new())
        {
            var voice = new Voice { Id = v.Id, Number = v.Number };
            foreach (var b in v.Beats ?? new())
            {
                var beat = new Beat(b.Rhythm.ToValue()) { Id = b.Id, Intensity = b.Intensity, PitchBendSemitones = b.Bend };
                foreach (var n in b.Notes ?? new()) beat.Notes.Add(new ScoreNote(n.Pitch, n.Velocity) { Id = n.Id });
                voice.Beats.Add(beat);
            }
            target.Voices.Add(voice);
        }
        foreach (var n in part.TabEvents ?? new())
        {
            var onset = new ScoreTime(n.OnsetNumerator, n.OnsetDenominator);
            var duration = new ScoreTime(n.DurationNumerator, n.DurationDenominator);
            NormalizeTabAssignment(track, n, version);
            if (track.TabLaneMidiPitches[n.StringNumber - 1] + n.Fret != n.Pitch)
                throw new InvalidDataException("A tablature event's string, fret, and pitch do not match its track tuning.");
            var tabEvent = new TabEvent(n.StringNumber, n.Fret, n.Pitch, n.Velocity, onset, duration)
            { Id = n.Id, AllowsBarOverflow = n.AllowsBarOverflow, TupletActual = version >= 6 ? n.TupletActual : 0, SoundVariation = version >= 8 ? n.SoundVariation ?? "" : "", IncomingArticulation = version >= 12 ? n.IncomingArticulation : StringArticulation.None };
            if (version >= 7)
                foreach (var expressionData in n.Expressions ?? new())
                {
                    var expression = new NoteExpression { Id = expressionData.Id, Kind = expressionData.Kind, Position = expressionData.Position,
                        MinSemitones = expressionData.MinSemitones, MaxSemitones = expressionData.MaxSemitones,
                        Points = (expressionData.Points ?? new()).Select(point => new ExpressionPoint(point.X, point.Y, version >= 12 ? point.SegmentMode : ExpressionSegmentMode.Linear)).ToList() };
                    expression.Validate();
                    tabEvent.Expressions.Add(expression);
                }
            target.TabEvents.Add(tabEvent);
        }
        // An older fixed-pitch track can have the same pitch authored on different lanes.
        // Keep those attacks when the lanes are mapped to the same chromatic row.
        if (!track.UsesFullTab && target.TabEvents.GroupBy(n => (n.StringNumber, n.Onset)).Any(group => group.Count() > 1))
            throw new InvalidDataException("A string cannot have two tablature events at the same onset.");
        return target;
    }

    public static ScoreDocument Load(string path) => Deserialize(File.ReadAllBytes(path));

    public static void Save(string path, ScoreDocument score)
    {
        var fullPath = Path.GetFullPath(path);
        var temporary = fullPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try { File.WriteAllBytes(temporary, Serialize(score)); File.Move(temporary, fullPath, true); }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    public sealed class Document { public int Version { get; set; } = 11; public Guid Id { get; set; } public string Title { get; set; } = ""; public TempoData Tempo { get; set; } = new(); public MeterData Meter { get; set; } = new(); public KeyData Key { get; set; } = new(); public double PercussionMasterGainDb { get; set; } public bool MetronomeEnabled { get; set; } public bool CountInEnabled { get; set; } public bool CountInEveryLoop { get; set; } public List<TrackData> Tracks { get; set; } = new(); public List<AudioAssetData>? AudioAssets { get; set; } public List<SectionData> Sections { get; set; } = new(); public List<ArrangementData>? Arrangements { get; set; } }
    public sealed class ArrangementData { public Guid Id { get; set; } public string Name { get; set; } = ""; public List<ArrangementSectionData>? Sections { get; set; } public List<AudioClipData>? AudioClips { get; set; } }
    public sealed class ArrangementSectionData { public Guid SectionId { get; set; } public int Repetitions { get; set; } = 1; }
    public sealed class TrackData { public Guid Id { get; set; } public string Name { get; set; } = ""; public TrackKind Kind { get; set; } public int Program { get; set; } public double Volume { get; set; } public bool Mute { get; set; } public bool Solo { get; set; } public List<int>? TabTuning { get; set; } public int MaxFret { get; set; } = 24; public string? BannerImagePath { get; set; } public uint DisplayColorRgba { get; set; } = 0xffffffff; public int? TabSnapDenominator { get; set; } public List<PercussionPieceData>? PercussionPieces { get; set; } public List<PedalData>? Pedals { get; set; } }
    public sealed class PedalData
    {
        public Guid Id { get; set; }
        public string TypeId { get; set; } = "overdrive";
        public bool Enabled { get; set; } = true;
        public float Drive { get; set; } = 0.4f;
        public float Tone { get; set; } = 0.5f;
        public float Level { get; set; } = 0.5f;
        public Dictionary<string, float>? Parameters { get; set; }
        public List<PedalAutomationData>? Automation { get; set; }
        [JsonExtensionData] public Dictionary<string, JsonElement>? ExtensionData { get; set; }
        public static PedalData From(TrackPedal pedal) => new() { Id = pedal.Id, TypeId = pedal.TypeId, Enabled = pedal.Enabled,
            Drive = pedal.Drive, Tone = pedal.Tone, Level = pedal.Level, Parameters = new Dictionary<string, float>(pedal.Parameters, StringComparer.Ordinal),
            ExtensionData = new Dictionary<string, JsonElement>(pedal.ExtensionData, StringComparer.Ordinal),
            Automation = pedal.Automation.Select(lane => new PedalAutomationData { ParameterId = lane.ParameterId, Points = lane.Points.ToList(), ExtensionData = new Dictionary<string, JsonElement>(lane.ExtensionData, StringComparer.Ordinal) }).ToList() };
        public TrackPedal ToValue()
        {
            var pedal = new TrackPedal { Id = Id, TypeId = TypeId, Enabled = Enabled, Drive = Drive, Tone = Tone, Level = Level };
            if (Parameters is not null)
                foreach (var (parameter, value) in Parameters) pedal.Parameters.Add(parameter, value);
            if (ExtensionData is not null)
                foreach (var (name, value) in ExtensionData) pedal.ExtensionData.Add(name, value);
            foreach (var data in Automation ?? new())
            {
                if (data is null) throw new InvalidDataException("A pedal automation lane is missing.");
                var lane = new PedalAutomationLane { ParameterId = data.ParameterId };
                lane.Points.AddRange(data.Points ?? new());
                if (data.ExtensionData is not null)
                    foreach (var (name, value) in data.ExtensionData) lane.ExtensionData.Add(name, value);
                pedal.Automation.Add(lane);
            }
            return pedal;
        }
    }
    public sealed class PedalAutomationData { public string ParameterId { get; set; } = ""; public List<PedalAutomationPoint>? Points { get; set; } [JsonExtensionData] public Dictionary<string, JsonElement>? ExtensionData { get; set; } }
    public sealed class PercussionPieceData { public string Id { get; set; } = ""; public string Name { get; set; } = ""; public int MidiPitch { get; set; } public bool IsMuted { get; set; } public double GainDb { get; set; } }
    public sealed class SectionData { public Guid Id { get; set; } public string Name { get; set; } = ""; public string Title { get; set; } = ""; public Guid? Lead { get; set; } public TempoData? Tempo { get; set; } public MeterData? Meter { get; set; } public KeyData? Key { get; set; } public Dictionary<Guid, int> Patterns { get; set; } = new(); public List<MeasureData> Measures { get; set; } = new(); public List<AudioClipData>? AudioClips { get; set; } }
    public sealed class AudioAssetData { public Guid Id { get; set; } public string FileName { get; set; } = ""; public string Sha256 { get; set; } = ""; public int SampleRate { get; set; } public int Channels { get; set; } public long FrameCount { get; set; } public string CachePath { get; set; } = ""; public static AudioAssetData From(AudioAsset a) => new() { Id = a.Id, FileName = a.FileName, Sha256 = a.Sha256, SampleRate = a.SampleRate, Channels = a.Channels, FrameCount = a.FrameCount, CachePath = a.CachePath }; public AudioAsset ToValue() => new(Id, FileName, Sha256, SampleRate, Channels, FrameCount, CachePath); }
    public sealed class AudioClipData { public Guid Id { get; set; } public Guid AssetId { get; set; } public Guid TrackId { get; set; } public long StartNumerator { get; set; } public long StartDenominator { get; set; } = 1; public long SourceOffsetFrames { get; set; } public long DurationFrames { get; set; } public static AudioClipData From(AudioClip c) => new() { Id = c.Id, AssetId = c.AssetId, TrackId = c.TrackId, StartNumerator = c.Start.Numerator, StartDenominator = c.Start.Denominator, SourceOffsetFrames = c.SourceOffsetFrames, DurationFrames = c.DurationFrames }; public AudioClip ToValue() => new(Id, AssetId, TrackId, new ScoreTime(StartNumerator, StartDenominator), SourceOffsetFrames, DurationFrames); }
    public sealed class RhythmData { public int Denominator { get; set; } = 4; public int Dots { get; set; } public int Actual { get; set; } = 1; public int Normal { get; set; } = 1; public static RhythmData From(RhythmicValue r) => new() { Denominator = r.NoteDenominator, Dots = r.Dots, Actual = r.TupletActual, Normal = r.TupletNormal }; public RhythmicValue ToValue() => new(Denominator, Dots, Actual, Normal); }
    public sealed class TempoData { public decimal Bpm { get; set; } = 120; public RhythmData Unit { get; set; } = new(); public static TempoData From(TempoMark t) => new() { Bpm = t.BeatsPerMinute, Unit = RhythmData.From(t.BeatUnit) }; public TempoMark ToValue() => new(Bpm, Unit.ToValue()); }
    public sealed class MeterData { public int Numerator { get; set; } = 4; public int Denominator { get; set; } = 4; public static MeterData From(TimeSignature m) => new() { Numerator = m.Numerator, Denominator = m.Denominator }; public TimeSignature ToValue() => new(Numerator, Denominator); }
    public sealed class KeyData { public int Fifths { get; set; } public KeyMode Mode { get; set; } public static KeyData From(KeySignature k) => new() { Fifths = k.Fifths, Mode = k.Mode }; public KeySignature ToValue() => new(Fifths, Mode); }
    public sealed class TempoEventData { public Guid Id { get; set; } public long Numerator { get; set; } public long Denominator { get; set; } public TempoData Tempo { get; set; } = new(); public TempoInterpolation Interpolation { get; set; } }
    public sealed class MeasureData
    {
        public Guid Id { get; set; } public MeterData? Meter { get; set; } public KeyData? Key { get; set; } public bool Pickup { get; set; } public bool FreeTime { get; set; } public string? Barline { get; set; }
        public List<TempoEventData> Tempos { get; set; } = new(); public List<PartData> Parts { get; set; } = new();
        public List<VersionData>? Versions { get; set; } public List<TransitionRuleData>? TransitionRules { get; set; } public List<AlternationData>? Alternations { get; set; }
        public static MeasureData From(ScoreMeasure m) => new() { Id = m.Id, Meter = m.TimeSignatureOverride is { } meter ? MeterData.From(meter) : null, Key = m.KeySignatureOverride is { } key ? KeyData.From(key) : null,
            Pickup = m.IsPickup, FreeTime = m.IsFreeTime, Barline = m.BarlineStyle,
            Tempos = m.TempoEvents.Select(e => new TempoEventData { Id = e.Id, Numerator = e.Offset.Numerator, Denominator = e.Offset.Denominator, Tempo = TempoData.From(e.Value), Interpolation = e.Interpolation }).ToList(),
            Parts = m.TrackMeasures.Select(p => new PartData { Track = p.TrackId, UsesTabEvents = p.UsesTabEvents, RepeatCount = p.RepeatCount, SnapDenominator = p.SnapDenominator,
                TabEvents = p.TabEvents.Select(n => new TabEventData { Id = n.Id, StringNumber = n.StringNumber, Fret = n.Fret, Pitch = n.MidiPitch, Velocity = n.Velocity, SoundVariation = n.SoundVariation, IncomingArticulation = n.IncomingArticulation,
                    OnsetNumerator = n.Onset.Numerator, OnsetDenominator = n.Onset.Denominator, DurationNumerator = n.Duration.Numerator, DurationDenominator = n.Duration.Denominator, AllowsBarOverflow = n.AllowsBarOverflow, TupletActual = n.TupletActual,
                    Expressions = n.Expressions.Select(expression => { expression.Validate(); return new NoteExpressionData { Id = expression.Id, Kind = expression.Kind, Position = expression.Position,
                        MinSemitones = expression.MinSemitones, MaxSemitones = expression.MaxSemitones,
                        Points = expression.Points.Select(point => new ExpressionPointData { X = point.X, Y = point.Y, SegmentMode = point.SegmentMode }).ToList() }; }).ToList() }).ToList(),
                Voices = p.Voices.Select(v => new VoiceData { Id = v.Id, Number = v.Number,
                    Beats = v.Beats.Select(b => new BeatData { Id = b.Id, Rhythm = RhythmData.From(b.RhythmicValue), Intensity = b.Intensity, Bend = b.PitchBendSemitones,
                Notes = b.Notes.Select(n => new NoteData { Id = n.Id, Pitch = n.MidiPitch, Velocity = n.Velocity }).ToList() }).ToList() }).ToList() }).ToList(),
            Versions = m.TrackMeasureVersions.Select(v => new VersionData { Id = v.Id, Track = v.TrackId, Name = v.Name, Content = PartData.From(v.Content) }).ToList(),
            TransitionRules = m.TransitionRules.Select(rule => new TransitionRuleData { Track = rule.TrackId, Section = rule.SectionId, Version = rule.VersionId }).ToList(),
            Alternations = m.Alternations.Select(item => new AlternationData { Track = item.TrackId, Versions = item.VersionIds.ToList() }).ToList() };
    }
    public sealed class VersionData { public Guid Id { get; set; } public Guid Track { get; set; } public string Name { get; set; } = ""; public PartData Content { get; set; } = new(); }
    public sealed class TransitionRuleData { public Guid Track { get; set; } public Guid Section { get; set; } public Guid Version { get; set; } }
    public sealed class AlternationData { public Guid Track { get; set; } public List<Guid> Versions { get; set; } = new(); }
    public sealed class PartData
    {
        public Guid Track { get; set; } public bool UsesTabEvents { get; set; } public int RepeatCount { get; set; } public int SnapDenominator { get; set; } public List<TabEventData> TabEvents { get; set; } = new(); public List<VoiceData> Voices { get; set; } = new();
        public static PartData From(TrackMeasure p) => new() { Track = p.TrackId, UsesTabEvents = p.UsesTabEvents, RepeatCount = p.RepeatCount, SnapDenominator = p.SnapDenominator,
            TabEvents = p.TabEvents.Select(n => new TabEventData { Id = n.Id, StringNumber = n.StringNumber, Fret = n.Fret, Pitch = n.MidiPitch, Velocity = n.Velocity, SoundVariation = n.SoundVariation, IncomingArticulation = n.IncomingArticulation,
                OnsetNumerator = n.Onset.Numerator, OnsetDenominator = n.Onset.Denominator, DurationNumerator = n.Duration.Numerator, DurationDenominator = n.Duration.Denominator, AllowsBarOverflow = n.AllowsBarOverflow, TupletActual = n.TupletActual,
                Expressions = n.Expressions.Select(expression => new NoteExpressionData { Id = expression.Id, Kind = expression.Kind, Position = expression.Position, MinSemitones = expression.MinSemitones, MaxSemitones = expression.MaxSemitones,
                    Points = expression.Points.Select(point => new ExpressionPointData { X = point.X, Y = point.Y, SegmentMode = point.SegmentMode }).ToList() }).ToList() }).ToList(),
            Voices = p.Voices.Select(v => new VoiceData { Id = v.Id, Number = v.Number, Beats = v.Beats.Select(b => new BeatData { Id = b.Id, Rhythm = RhythmData.From(b.RhythmicValue), Intensity = b.Intensity, Bend = b.PitchBendSemitones,
                Notes = b.Notes.Select(n => new NoteData { Id = n.Id, Pitch = n.MidiPitch, Velocity = n.Velocity }).ToList() }).ToList() }).ToList() };
    }
    private static void NormalizeTabAssignment(ScoreTrack track, TabEventData note, int version)
    {
        if (track.UsesFullTab && version < 17)
        {
            if (note.StringNumber < 1 || note.StringNumber > track.TabTuningMidiPitches.Count || note.Fret < 0 || note.Fret > track.MaxFret ||
                track.TabTuningMidiPitches[note.StringNumber - 1] + note.Fret != note.Pitch || note.Pitch is < 0 or > 127)
                throw new InvalidDataException("A legacy tablature event does not match its track tuning.");
            note.StringNumber = 128 - note.Pitch;
            note.Fret = 0;
        }
        TabEventEditing.ValidateAssignment(track, note.StringNumber, note.Fret);
    }

    public sealed class TabEventData { public Guid Id { get; set; } public int StringNumber { get; set; } public int Fret { get; set; } public int Pitch { get; set; } public int Velocity { get; set; } public long OnsetNumerator { get; set; } public long OnsetDenominator { get; set; } = 1; public long DurationNumerator { get; set; } public long DurationDenominator { get; set; } = 1; public bool AllowsBarOverflow { get; set; } public int TupletActual { get; set; } public string? SoundVariation { get; set; } public StringArticulation IncomingArticulation { get; set; } public List<NoteExpressionData>? Expressions { get; set; } }
    public sealed class NoteExpressionData { public Guid Id { get; set; } public ExpressionKind Kind { get; set; } public double Position { get; set; } public double MinSemitones { get; set; } = -2d; public double MaxSemitones { get; set; } = 2d; public List<ExpressionPointData>? Points { get; set; } }
    public sealed class ExpressionPointData { public double X { get; set; } public double Y { get; set; } public ExpressionSegmentMode SegmentMode { get; set; } }
    public sealed class VoiceData { public Guid Id { get; set; } public int Number { get; set; } public List<BeatData> Beats { get; set; } = new(); }
    public sealed class BeatData { public Guid Id { get; set; } public RhythmData Rhythm { get; set; } = new(); public int Intensity { get; set; } = 3; public decimal Bend { get; set; } public List<NoteData> Notes { get; set; } = new(); }
    public sealed class NoteData { public Guid Id { get; set; } public int Pitch { get; set; } public int Velocity { get; set; } }
}
