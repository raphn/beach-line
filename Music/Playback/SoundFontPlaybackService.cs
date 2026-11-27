#nullable enable

using System;
using System.IO;
using System.Collections.Generic;
using System.Linq;
using MeltySynth;

namespace OpenTab.Music.Playback;

/// <summary>
/// Loads a SoundFont and provides serialized MIDI note commands and stereo PCM rendering.
/// This class has no dependency on Godot or the score model.
/// </summary>
public sealed class SoundFontPlaybackService
{
    private readonly object _synthesizerLock = new();
    private readonly Synthesizer _synthesizer;
    private readonly SoundFont _soundFont;
    private readonly Dictionary<int, DrumPiecePlayer> _drumPlayers = new();
    private readonly Dictionary<int, float> _drumPieceGains = new();
    private readonly Dictionary<Guid, TrackOutput> _trackOutputs = new();
    private readonly Dictionary<Guid, ExpressiveVoice> _expressiveVoices = new();
    private readonly List<ExpressiveVoice> _releasedExpressiveVoices = new();
    private readonly Stack<Synthesizer> _expressiveSynthPool = new();
    private const int ExpressiveSynthPoolLimit = 16;
    private readonly Dictionary<int, int> _programByChannel = new();
    private readonly Dictionary<int, int> _bankByChannel = new();
    private float[] _drumLeft = Array.Empty<float>();
    private float[] _drumRight = Array.Empty<float>();
    private float[] _trackLeft = Array.Empty<float>();
    private float[] _trackRight = Array.Empty<float>();
    private int _drumProgram;
    private int _drumBank;
    private int _drumBend = 8192;
    private long _effectSourceFrame;
    private bool _holdEffectAutomation;

    private sealed class ExpressiveVoice
    {
        public required Synthesizer Synth { get; init; }
        public required int Channel { get; init; }
        public required int Pitch { get; init; }
        public double LegatoOffset { get; set; }
        public float Tremolo { get; set; } = 1f;
        public float PieceGain { get; set; } = 1f;
        public int PieceGainPitch { get; set; } = -1;
        public int ReleaseFrames { get; set; }
    }

    public SoundFontPlaybackService(Stream soundFontStream, int sampleRate)
    {
        ArgumentNullException.ThrowIfNull(soundFontStream);
        if (!soundFontStream.CanRead)
        {
            throw new ArgumentException("The SoundFont stream must be readable.", nameof(soundFontStream));
        }

        if (sampleRate is < 16000 or > 192000)
        {
            throw new ArgumentOutOfRangeException(nameof(sampleRate), "MeltySynth supports sample rates from 16,000 to 192,000 Hz.");
        }

        if (soundFontStream.CanSeek)
        {
            soundFontStream.Position = 0;
        }

        _soundFont = new SoundFont(soundFontStream);
        _synthesizer = new Synthesizer(_soundFont, sampleRate);
        SampleRate = sampleRate;
    }

    private SoundFontPlaybackService(SoundFont soundFont, int sampleRate)
    {
        _soundFont = soundFont;
        _synthesizer = new Synthesizer(soundFont, sampleRate);
        SampleRate = sampleRate;
    }

    public int SampleRate { get; }

    /// <summary>Creates a fresh synth service sharing this loaded bank but no voices or track state.</summary>
    public SoundFontPlaybackService CreateIsolatedInstance() => new(_soundFont, SampleRate);

    private sealed class TrackOutput
    {
        public TrackOutput(SoundFont soundFont, int sampleRate) => Service = new SoundFontPlaybackService(soundFont, sampleRate);
        public SoundFontPlaybackService Service { get; }
        public float TrackGain { get; set; } = 1f;
        public float PercussionMasterGain { get; set; } = 1f;
        public TrackEffectsChain? Effects { get; set; }
    }

    /// <summary>Replaces the track effect snapshots without changing the synthesizer voices.</summary>
    public void SetTrackEffects(IReadOnlyDictionary<Guid, TrackEffectsChain> effects)
    {
        ArgumentNullException.ThrowIfNull(effects);
        lock (_synthesizerLock)
        {
            foreach (var output in _trackOutputs.Values) output.Effects = null;
            foreach (var (trackId, chain) in effects) GetTrackOutput(trackId).Effects = chain;
        }
    }

    private TrackOutput GetTrackOutput(Guid trackId)
    {
        if (!_trackOutputs.TryGetValue(trackId, out var output))
        {
            output = new TrackOutput(_soundFont, SampleRate);
            _trackOutputs.Add(trackId, output);
        }
        return output;
    }

    /// <summary>Sets post-synthesis amplitude gain without changing MIDI velocity.</summary>
    public void SetTrackVolume(Guid trackId, double decibels)
    {
        if (!double.IsFinite(decibels) || decibels is < -80 or > 20)
            throw new ArgumentOutOfRangeException(nameof(decibels), "Track volume must be between -80 and 20 dB.");
        lock (_synthesizerLock) GetTrackOutput(trackId).TrackGain = (float)Math.Pow(10, decibels / 20.0);
    }

    /// <summary>Sets a separate post-synthesis master factor for one percussion track.</summary>
    public void SetPercussionMasterGain(Guid trackId, double decibels)
    {
        if (!double.IsFinite(decibels) || decibels is < -80 or > 20)
            throw new ArgumentOutOfRangeException(nameof(decibels), "Percussion master gain must be between -80 and 20 dB.");
        lock (_synthesizerLock) GetTrackOutput(trackId).PercussionMasterGain = (float)Math.Pow(10, decibels / 20.0);
    }

    /// <summary>Sets post-synthesis gain for a GM drum pitch within one track.</summary>
    public void SetDrumPieceGain(Guid trackId, int pitch, double decibels)
    {
        ValidatePitch(pitch);
        if (!double.IsFinite(decibels) || decibels is < -80 or > 20)
            throw new ArgumentOutOfRangeException(nameof(decibels));
        lock (_synthesizerLock)
        {
            var service = GetTrackOutput(trackId).Service;
            var gain = (float)Math.Pow(10, decibels / 20.0);
            service._drumPieceGains[pitch] = gain;
            foreach (var voice in service._expressiveVoices.Values.Concat(service._releasedExpressiveVoices))
                if (voice.Channel == 9 && (voice.PieceGainPitch == pitch || voice.PieceGainPitch < 0 && voice.Pitch == pitch)) voice.PieceGain = gain;
        }
    }

    public void NoteOn(Guid trackId, int channel, int pitch, int velocity, Guid noteId)
    {
        lock (_synthesizerLock) GetTrackOutput(trackId).Service.NoteOnCore(channel, pitch, velocity, noteId);
    }

    /// <summary>Starts a note on an independent synth voice so expression cannot affect neighboring notes.</summary>
    public void NoteOnExpressive(Guid trackId, int channel, int pitch, int velocity, Guid noteId)
    {
        lock (_synthesizerLock) GetTrackOutput(trackId).Service.NoteOnExpressiveCore(channel, pitch, velocity, noteId);
    }

    /// <summary>Starts an isolated note using an explicit MIDI program, without changing its track's program.</summary>
    public void NoteOnExpressive(Guid trackId, int channel, int pitch, int velocity, Guid noteId, int program, double pieceGainDb = 0, int pieceGainPitch = -1)
    {
        if (program is < -1 or > 127) throw new ArgumentOutOfRangeException(nameof(program));
        if (!double.IsFinite(pieceGainDb) || pieceGainDb is < -80 or > 20) throw new ArgumentOutOfRangeException(nameof(pieceGainDb));
        lock (_synthesizerLock) GetTrackOutput(trackId).Service.NoteOnExpressiveCore(channel, pitch, velocity, noteId, program >= 0 ? program : null, (float)Math.Pow(10, pieceGainDb / 20.0), pieceGainPitch);
    }

    /// <summary>Updates the pitch bend and amplitude multiplier of one expressive note.</summary>
    public void SetNoteExpression(Guid trackId, Guid noteId, double pitchSemitones, double tremolo)
    {
        lock (_synthesizerLock)
            if (_trackOutputs.TryGetValue(trackId, out var output)) output.Service.SetNoteExpressionCore(noteId, pitchSemitones, tremolo);
    }

    /// <summary>Transfers an isolated sounding voice to a following note without a new attack.</summary>
    public bool TryContinueExpressiveNote(Guid trackId, Guid fromId, Guid toId, int targetPitch, double minimumExpression = 0, double maximumExpression = 0)
    {
        lock (_synthesizerLock)
        {
            if (!_trackOutputs.TryGetValue(trackId, out var output)) return false;
            var service = output.Service;
            if (!service._expressiveVoices.TryGetValue(fromId, out var voice) ||
                service._expressiveVoices.ContainsKey(toId) || targetPitch - voice.Pitch + minimumExpression < -24 ||
                targetPitch - voice.Pitch + maximumExpression > 24)
                return false;
            service._expressiveVoices.Remove(fromId);
            voice.LegatoOffset = targetPitch - voice.Pitch;
            service._expressiveVoices.Add(toId, voice);
            service.SetNoteExpressionCore(toId, 0, 1);
            return true;
        }
    }

    public void NoteOff(Guid trackId, int channel, int pitch, Guid noteId)
    {
        lock (_synthesizerLock) GetTrackOutput(trackId).Service.NoteOffCore(channel, pitch, noteId);
    }

    public void ProgramChange(Guid trackId, int channel, int program) { lock (_synthesizerLock) GetTrackOutput(trackId).Service.ProgramChange(channel, program); }
    public void ControlChange(Guid trackId, int channel, int controller, int value) { lock (_synthesizerLock) GetTrackOutput(trackId).Service.ControlChange(channel, controller, value); }
    public void PitchBendValue(Guid trackId, int channel, int value) { lock (_synthesizerLock) GetTrackOutput(trackId).Service.PitchBendValue(channel, value); }

    public void NoteOn(int channel, int midiPitch, int velocity) => NoteOnCore(channel, midiPitch, velocity, Guid.Empty);

    private void NoteOnCore(int channel, int midiPitch, int velocity, Guid noteId)
    {
        ValidateChannel(channel);
        ValidatePitch(midiPitch);
        if (velocity is < 1 or > 127)
        {
            throw new ArgumentOutOfRangeException(nameof(velocity), "MIDI note-on velocity must be between 1 and 127.");
        }

        lock (_synthesizerLock)
        {
            if (channel == 9)
            {
                // FluidR3's open hi-hat sample is a separate pitch player here,
                // so reproduce the GM choke when a closed or pedal hat is struck.
                if (midiPitch is 42 or 44) ChokeOpenHatVoices();
                if (!_drumPlayers.TryGetValue(midiPitch, out var player))
                {
                    player = new DrumPiecePlayer(_soundFont, SampleRate, _drumBank, _drumProgram, _drumBend);
                    _drumPlayers.Add(midiPitch, player);
                }
                player.NoteOn(midiPitch, velocity, noteId);
            }
            else _synthesizer.NoteOn(channel, midiPitch, velocity);
        }
    }

    private void NoteOnExpressiveCore(int channel, int pitch, int velocity, Guid noteId, int? programOverride = null, float? pieceGainOverride = null, int pieceGainPitch = -1)
    {
        ValidateChannel(channel); ValidatePitch(pitch);
        if (velocity is < 1 or > 127) throw new ArgumentOutOfRangeException(nameof(velocity));
        if (noteId == Guid.Empty) throw new ArgumentException("Expressive notes need an event identity.", nameof(noteId));
        if (channel == 9 && pitch is 42 or 44) ChokeOpenHatVoices();
        if (_expressiveVoices.Remove(noteId, out var previous))
        {
            RetireExpressiveVoice(previous);
        }
        var synth = _expressiveSynthPool.Count > 0 ? _expressiveSynthPool.Pop() : new Synthesizer(_soundFont, SampleRate);
        synth.ProcessMidiMessage(channel, 0xB0, 120, 0);
        synth.ProcessMidiMessage(channel, 0xB0, 0, _bankByChannel.GetValueOrDefault(channel));
        synth.ProcessMidiMessage(channel, 0xC0, programOverride ?? _programByChannel.GetValueOrDefault(channel), 0);
        // RPN 0,0 is pitch-bend sensitivity. Each expressive note has its own
        // synth, so a wide bend range cannot affect other notes or channels.
        synth.ProcessMidiMessage(channel, 0xB0, 101, 0);
        synth.ProcessMidiMessage(channel, 0xB0, 100, 0);
        synth.ProcessMidiMessage(channel, 0xB0, 6, 24);
        synth.ProcessMidiMessage(channel, 0xB0, 38, 0);
        synth.ProcessMidiMessage(channel, 0xB0, 101, 127);
        synth.ProcessMidiMessage(channel, 0xB0, 100, 127);
        synth.ProcessMidiMessage(channel, 0xE0, 0, 64);
        synth.NoteOn(channel, pitch, velocity);
        _expressiveVoices[noteId] = new ExpressiveVoice { Synth = synth, Channel = channel, Pitch = pitch,
            PieceGain = pieceGainOverride ?? (channel == 9 ? _drumPieceGains.GetValueOrDefault(pitch, 1f) : 1f), PieceGainPitch = pieceGainPitch };
    }

    private void ChokeOpenHatExpressiveVoices()
    {
        foreach (var pair in _expressiveVoices.Where(pair => pair.Value.Channel == 9 && pair.Value.Pitch == 46).ToArray())
        {
            _expressiveVoices.Remove(pair.Key);
            RetireExpressiveVoice(pair.Value);
        }
        for (var index = _releasedExpressiveVoices.Count - 1; index >= 0; index--)
        {
            var voice = _releasedExpressiveVoices[index];
            if (voice.Channel != 9 || voice.Pitch != 46) continue;
            _releasedExpressiveVoices.RemoveAt(index);
            RetireExpressiveVoice(voice);
        }
    }

    private void ChokeOpenHatDrumPlayers()
    {
        if (_drumPlayers.TryGetValue(46, out var openHat)) openHat.Stop();
    }

    private void ChokeOpenHatVoices()
    {
        ChokeOpenHatDrumPlayers();
        ChokeOpenHatExpressiveVoices();
    }

    private void SetNoteExpressionCore(Guid noteId, double pitchSemitones, double tremolo)
    {
        if (!_expressiveVoices.TryGetValue(noteId, out var voice)) return;
        var bend = Math.Clamp(voice.LegatoOffset + pitchSemitones, -24.0, 24.0);
        var offset = bend < 0 ? bend * (8192.0 / 24.0) : bend * (8191.0 / 24.0);
        var value = Math.Clamp((int)Math.Round(8192.0 + offset, MidpointRounding.AwayFromZero), 0, 16383);
        voice.Synth.ProcessMidiMessage(voice.Channel, 0xE0, value & 127, value >> 7);
        voice.Tremolo = (float)Math.Clamp(tremolo, 0.0, 1.0);
    }

    private void RetireExpressiveVoice(ExpressiveVoice voice)
    {
        voice.Synth.ProcessMidiMessage(voice.Channel, 0xB0, 120, 0);
        voice.Synth.ProcessMidiMessage(voice.Channel, 0xE0, 0, 64);
        if (_expressiveSynthPool.Count < ExpressiveSynthPoolLimit) _expressiveSynthPool.Push(voice.Synth);
    }

    public void NoteOff(int channel, int midiPitch) => NoteOffCore(channel, midiPitch, Guid.Empty);

    private void NoteOffCore(int channel, int midiPitch, Guid noteId)
    {
        ValidateChannel(channel);
        ValidatePitch(midiPitch);

        lock (_synthesizerLock)
        {
            if (noteId != Guid.Empty && _expressiveVoices.Remove(noteId, out var expressive))
            {
                expressive.Synth.NoteOff(expressive.Channel, expressive.Pitch);
                expressive.ReleaseFrames = 1;
                _releasedExpressiveVoices.Add(expressive);
                return;
            }
            if (channel == 9) { if (_drumPlayers.TryGetValue(midiPitch, out var player)) player.NoteOff(midiPitch, noteId); }
            else _synthesizer.NoteOff(channel, midiPitch);
        }
    }

    public void ProgramChange(int channel, int program)
    {
        ValidateChannel(channel);
        if (program is < 0 or > 127)
        {
            throw new ArgumentOutOfRangeException(nameof(program), "MIDI program numbers range from 0 to 127.");
        }

        lock (_synthesizerLock)
        {
            _programByChannel[channel] = program;
            if (channel == 9) { _drumProgram = program; foreach (var player in _drumPlayers.Values) player.Message(0xC0, program, 0); }
            else _synthesizer.ProcessMidiMessage(channel, 0xC0, program, 0);
        }
    }

    /// <summary>Selects a MIDI controller value using a serialized channel message.</summary>
    public void ControlChange(int channel, int controller, int value)
    {
        ValidateChannel(channel);
        ValidateMidiData(controller, nameof(controller));
        ValidateMidiData(value, nameof(value));
        lock (_synthesizerLock)
        {
            if (controller == 0) _bankByChannel[channel] = value;
            if (channel == 9) { if (controller == 0) _drumBank = value; foreach (var player in _drumPlayers.Values) player.Message(0xB0, controller, value); }
            else _synthesizer.ProcessMidiMessage(channel, 0xB0, controller, value);
        }
    }

    /// <summary>
    /// Sets channel pitch bend in semitones for a fixed +/-2 semitone range.
    /// MIDI's 14-bit center is 8192; the two bytes are sent least-significant first.
    /// </summary>
    public void PitchBend(int channel, decimal semitones)
    {
        ValidateChannel(channel);
        if (semitones is < -2m or > 2m)
        {
            throw new ArgumentOutOfRangeException(nameof(semitones), "Pitch bend must be between -2 and 2 semitones.");
        }

        var offset = semitones < 0m ? semitones * 4096m : semitones * (8191m / 2m);
        var value = Math.Clamp((int)Math.Round(8192m + offset, MidpointRounding.AwayFromZero), 0, 16383);
        PitchBendValue(channel, value);
    }

    /// <summary>Sends a raw 14-bit MIDI pitch bend value (0 to 16383).</summary>
    public void PitchBendValue(int channel, int value)
    {
        ValidateChannel(channel);
        if (value is < 0 or > 16383)
        {
            throw new ArgumentOutOfRangeException(nameof(value), "A 14-bit MIDI pitch bend value ranges from 0 to 16383.");
        }

        lock (_synthesizerLock)
        {
            if (channel == 9) { _drumBend = value; foreach (var player in _drumPlayers.Values) player.Message(0xE0, value & 0x7F, value >> 7); }
            else _synthesizer.ProcessMidiMessage(channel, 0xE0, value & 0x7F, (value >> 7) & 0x7F);
        }
    }

    public void AllNotesOff(int channel)
    {
        ValidateChannel(channel);
        lock (_synthesizerLock)
        {
            foreach (var pair in _expressiveVoices.Where(pair => pair.Value.Channel == channel).ToArray())
            {
                _expressiveVoices.Remove(pair.Key);
                RetireExpressiveVoice(pair.Value);
            }
            for (var index = _releasedExpressiveVoices.Count - 1; index >= 0; index--)
            {
                var voice = _releasedExpressiveVoices[index];
                if (voice.Channel != channel) continue;
                _releasedExpressiveVoices.RemoveAt(index);
                RetireExpressiveVoice(voice);
            }
            foreach (var output in _trackOutputs.Values) output.Service.AllNotesOff(channel);
            if (channel == 9)
            {
                foreach (var player in _drumPlayers.Values) player.Stop();
                _drumBend = 8192;
            }
            else
            {
                _synthesizer.ProcessMidiMessage(channel, 0xB0, 123, 0);
                _synthesizer.ProcessMidiMessage(channel, 0xE0, 0, 64);
            }
        }
    }

    /// <summary>
    /// Renders the same number of stereo frames into the supplied channel buffers.
    /// Calls are serialized with note commands because MeltySynth is not thread-safe.
    /// </summary>
    public void Render(Span<float> left, Span<float> right) => RenderCore(left, right, _effectSourceFrame, _holdEffectAutomation);

    /// <summary>Renders with an explicit source-score position for pedal automation.</summary>
    public void Render(Span<float> left, Span<float> right, long sourceFrame)
    {
        _holdEffectAutomation = false;
        RenderCore(left, right, sourceFrame, false);
    }

    /// <summary>Holds the last rendered pedal position during an offline release tail.</summary>
    public void HoldTrackEffectsAutomation() { _effectSourceFrame = Math.Max(0, _effectSourceFrame - 1); _holdEffectAutomation = true; }

    private void RenderCore(Span<float> left, Span<float> right, long sourceFrame, bool holdAutomation)
    {
        if (left.Length != right.Length)
        {
            throw new ArgumentException("Left and right audio buffers must have equal lengths.");
        }

        lock (_synthesizerLock)
        {
            _synthesizer.Render(left, right);
            if (_drumLeft.Length < left.Length) { _drumLeft = new float[left.Length]; _drumRight = new float[left.Length]; }
            foreach (var (pitch, player) in _drumPlayers)
                player.Mix(left, right, _drumLeft.AsSpan(0, left.Length), _drumRight.AsSpan(0, left.Length), _drumPieceGains.GetValueOrDefault(pitch, 1f));
            for (var index = _releasedExpressiveVoices.Count - 1; index >= 0; index--)
            {
                var voice = _releasedExpressiveVoices[index];
                voice.Synth.Render(_drumLeft.AsSpan(0, left.Length), _drumRight.AsSpan(0, left.Length));
                for (var i = 0; i < left.Length; i++) { left[i] += _drumLeft[i] * voice.Tremolo * voice.PieceGain; right[i] += _drumRight[i] * voice.Tremolo * voice.PieceGain; }
                voice.ReleaseFrames += left.Length;
                if (voice.Synth.ActiveVoiceCount == 0 || voice.ReleaseFrames > SampleRate * 5)
                {
                    _releasedExpressiveVoices.RemoveAt(index);
                    RetireExpressiveVoice(voice);
                }
            }
            foreach (var voice in _expressiveVoices.Values)
            {
                voice.Synth.Render(_drumLeft.AsSpan(0, left.Length), _drumRight.AsSpan(0, left.Length));
                for (var i = 0; i < left.Length; i++) { left[i] += _drumLeft[i] * voice.Tremolo * voice.PieceGain; right[i] += _drumRight[i] * voice.Tremolo * voice.PieceGain; }
            }
            if (_trackLeft.Length < left.Length) { _trackLeft = new float[left.Length]; _trackRight = new float[left.Length]; }
            foreach (var output in _trackOutputs.Values)
            {
                var trackLeft = _trackLeft.AsSpan(0, left.Length);
                var trackRight = _trackRight.AsSpan(0, left.Length);
                output.Service.Render(trackLeft, trackRight);
                output.Effects?.Process(trackLeft, trackRight, sourceFrame, holdAutomation);
                var gain = output.TrackGain * output.PercussionMasterGain;
                for (var i = 0; i < left.Length; i++) { left[i] += trackLeft[i] * gain; right[i] += trackRight[i] * gain; }
            }
            _effectSourceFrame = holdAutomation ? sourceFrame : sourceFrame + left.Length;
        }
    }

    // One independent four-slot player per GM drum piece. A generation prevents an
    // old note-off from cutting off a newer hit after a slot has been reused.
    private sealed class DrumPiecePlayer
    {
        private readonly Synthesizer[] _voices = new Synthesizer[4];
        private readonly int[] _generations = new int[4];
        private readonly Queue<(int Slot, int Generation)> _pending = new();
        private readonly Dictionary<Guid, (int Slot, int Generation)> _notes = new();
        private int _next;
        public DrumPiecePlayer(SoundFont bank, int rate, int bankNumber, int program, int bend)
        {
            for (var i = 0; i < _voices.Length; i++) _voices[i] = new Synthesizer(bank, rate);
            Message(0xB0, 0, bankNumber); Message(0xC0, program, 0); Message(0xE0, bend & 127, bend >> 7);
        }
        public void NoteOn(int pitch, int velocity, Guid noteId)
        {
            var slot = _next++ % _voices.Length;
            _voices[slot].ProcessMidiMessage(9, 0xB0, 120, 0);
            var generation = ++_generations[slot];
            if (noteId == Guid.Empty) _pending.Enqueue((slot, generation));
            else _notes[noteId] = (slot, generation);
            _voices[slot].NoteOn(9, pitch, velocity);
        }
        public void NoteOff(int pitch, Guid noteId)
        {
            (int Slot, int Generation) item;
            if (noteId == Guid.Empty)
            {
                if (_pending.Count == 0) return;
                item = _pending.Dequeue();
            }
            else if (!_notes.Remove(noteId, out item)) return;
            if (_generations[item.Slot] == item.Generation) _voices[item.Slot].NoteOff(9, pitch);
        }
        public void Message(int command, int value, int second) { foreach (var voice in _voices) voice.ProcessMidiMessage(9, command, value, second); }
        public void Stop() { Message(0xB0, 120, 0); Message(0xE0, 0, 64); _pending.Clear(); _notes.Clear(); }
        public void Mix(Span<float> left, Span<float> right, Span<float> scratchLeft, Span<float> scratchRight, float gain)
        {
            foreach (var voice in _voices)
            {
                voice.Render(scratchLeft, scratchRight);
                for (var i = 0; i < left.Length; i++) { left[i] += scratchLeft[i] * gain; right[i] += scratchRight[i] * gain; }
            }
        }
    }

    private static void ValidateChannel(int channel)
    {
        if (channel is < 0 or > 15)
        {
            throw new ArgumentOutOfRangeException(nameof(channel), "MIDI channels range from 0 to 15.");
        }
    }

    private static void ValidatePitch(int midiPitch)
    {
        if (midiPitch is < 0 or > 127)
        {
            throw new ArgumentOutOfRangeException(nameof(midiPitch), "MIDI pitch ranges from 0 to 127.");
        }
    }

    private static void ValidateMidiData(int value, string parameterName)
    {
        if (value is < 0 or > 127)
        {
            throw new ArgumentOutOfRangeException(parameterName, "MIDI data values range from 0 to 127.");
        }
    }
}
