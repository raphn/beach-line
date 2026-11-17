using System.Text;
using BeachLine.Music.Audio;
using OpenTab.Music;
using OpenTab.Music.Persistence;

internal static class AudioStorageTests
{
    public static void Run()
    {
        var directory = Path.Combine(Directory.GetCurrentDirectory(), ".codex-temp-godot", "audio-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var source = Path.Combine(directory, "source.wav");
            using (var output = File.Create(source))
            using (var writer = new BinaryWriter(output, Encoding.ASCII))
            {
                writer.Write(Encoding.ASCII.GetBytes("RIFF")); writer.Write(36 + 8);
                writer.Write(Encoding.ASCII.GetBytes("WAVEfmt ")); writer.Write(16);
                writer.Write((short)1); writer.Write((short)1); writer.Write(48000);
                writer.Write(96000); writer.Write((short)2); writer.Write((short)16);
                writer.Write(Encoding.ASCII.GetBytes("data")); writer.Write(8); writer.Write(new byte[8]);
            }
            var asset = WavAssetStore.Import(source, Path.Combine(directory, "cache"));
            var score = new ScoreDocument("Audio", new TempoMark(120, new RhythmicValue(4)), new TimeSignature(4, 4));
            var track = score.AddTrack(new ScoreTrack("Audio", TrackKind.Audio));
            score.AddMeasure(score.Sections[0]);
            score.AudioAssets.Add(asset);
            score.Sections[0].AudioClips.Add(new AudioClip(asset.Id, track.Id, ScoreTime.Zero, 0, 4));
            var project = Path.Combine(directory, "saved.otab");
            PortableScoreFile.Save(project, score);
            using (var archive = System.IO.Compression.ZipFile.OpenRead(project))
            using (var entry = archive.GetEntry("project.json")!.Open())
            using (var document = System.Text.Json.JsonDocument.Parse(entry))
                if (document.RootElement.GetProperty("AudioAssets")[0].GetProperty("CachePath").GetString() == asset.CachePath)
                    throw new Exception("Portable project exposed its local audio cache path.");
            File.Delete(source);
            File.Delete(asset.CachePath);
            var restored = PortableScoreFile.Load(project, Path.Combine(directory, "other-cache"));
            if (!File.Exists(restored.AudioAssets.Single().CachePath) || restored.AudioAssets.Single().FrameCount != 4 || restored.Sections[0].AudioClips.Count != 1)
                throw new Exception("Portable audio did not survive removal of the original media.");
            var previous = File.ReadAllBytes(project);
            File.AppendAllText(restored.AudioAssets.Single().CachePath, "modified");
            try { PortableScoreFile.Save(project, restored); throw new Exception("Changed media was accepted."); }
            catch (InvalidDataException) { }
            if (!previous.SequenceEqual(File.ReadAllBytes(project))) throw new Exception("Failed save changed the previous project.");
        }
        finally { Directory.Delete(directory, true); }
    }
}
