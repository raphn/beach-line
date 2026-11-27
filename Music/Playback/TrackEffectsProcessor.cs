#nullable enable
using System;

namespace OpenTab.Music.Playback;

/// <summary>An independently owned stereo processor in a track's ordered effects chain.</summary>
public interface ITrackAudioEffect
{
    void Process(Span<float> left, Span<float> right, long sourceFrame, bool holdAutomation = false);
}

/// <summary>An automation point compiled from musical time into source-score sample frames.</summary>
public readonly record struct AudioAutomationPoint(long Frame, float Value, bool Step);

/// <summary>A compiled, reusable parameter curve. Empty curves use their static value.</summary>
public sealed class AudioAutomationCurve
{
    private readonly AudioAutomationPoint[] _points;
    private readonly float _fallback;
    private int _index;

    public AudioAutomationCurve(float fallback, AudioAutomationPoint[] points)
    {
        _fallback = Math.Clamp(float.IsFinite(fallback) ? fallback : 0f, 0f, 1f);
        _points = points ?? Array.Empty<AudioAutomationPoint>();
    }

    public float ValueAt(long frame)
    {
        if (_points.Length == 0) return _fallback;
        if (frame <= _points[0].Frame) { _index = 0; return _points[0].Value; }
        if (frame >= _points[^1].Frame) { _index = _points.Length - 1; return _points[^1].Value; }
        if (_index >= _points.Length - 1 || frame < _points[_index].Frame)
        {
            var low = 0; var high = _points.Length - 1;
            while (low + 1 < high) { var mid = (low + high) / 2; if (_points[mid].Frame <= frame) low = mid; else high = mid; }
            _index = low;
        }
        while (_index + 1 < _points.Length && _points[_index + 1].Frame <= frame) _index++;
        var from = _points[_index]; var to = _points[_index + 1];
        if (from.Step || to.Frame <= from.Frame) return from.Value;
        return from.Value + (to.Value - from.Value) * ((float)(frame - from.Frame) / (to.Frame - from.Frame));
    }
}

/// <summary>An ordered list of pedal processors, independent for each track and renderer.</summary>
public sealed class TrackEffectsChain
{
    private readonly ITrackAudioEffect[] _effects;
    public TrackEffectsChain(ITrackAudioEffect[] effects) => _effects = effects ?? Array.Empty<ITrackAudioEffect>();
    public void Process(Span<float> left, Span<float> right, long sourceFrame, bool holdAutomation = false)
    {
        foreach (var effect in _effects) effect.Process(left, right, sourceFrame, holdAutomation);
    }
}

/// <summary>Soft-clipped overdrive with smoothed drive, tone and output controls.</summary>
public sealed class OverdriveTrackEffect : ITrackAudioEffect
{
    private readonly AudioAutomationCurve _drive, _tone, _level;
    private readonly float _sampleRate, _smoothing;
    private float _smoothDrive, _smoothTone, _smoothLevel, _leftLow, _rightLow;
    private bool _initialized;

    public OverdriveTrackEffect(int sampleRate, AudioAutomationCurve drive, AudioAutomationCurve tone, AudioAutomationCurve level)
    {
        _sampleRate = sampleRate;
        _smoothing = 1f - MathF.Exp(-1f / (sampleRate * 0.005f));
        _drive = drive; _tone = tone; _level = level;
    }

    public void Process(Span<float> left, Span<float> right, long sourceFrame, bool holdAutomation = false)
    {
        for (var i = 0; i < left.Length; i++)
        {
            var frame = holdAutomation ? sourceFrame : sourceFrame + i;
            var drive = _drive.ValueAt(frame); var tone = _tone.ValueAt(frame); var level = _level.ValueAt(frame);
            if (!_initialized) { _smoothDrive = drive; _smoothTone = tone; _smoothLevel = level; _initialized = true; }
            _smoothDrive += (drive - _smoothDrive) * _smoothing;
            _smoothTone += (tone - _smoothTone) * _smoothing;
            _smoothLevel += (level - _smoothLevel) * _smoothing;
            var inputGain = 1f + 24f * _smoothDrive * _smoothDrive;
            var outputGain = 2f * _smoothLevel;
            var cutoff = 700f + 10500f * _smoothTone * _smoothTone;
            var omega = 2f * MathF.PI * MathF.Min(cutoff, _sampleRate * .45f) / _sampleRate;
            var coefficient = omega / (1f + omega);
            var wetLeft = MathF.Tanh(left[i] * inputGain);
            var wetRight = MathF.Tanh(right[i] * inputGain);
            _leftLow += coefficient * (wetLeft - _leftLow);
            _rightLow += coefficient * (wetRight - _rightLow);
            left[i] = _leftLow * outputGain;
            right[i] = _rightLow * outputGain;
        }
    }
}
