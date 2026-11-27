#nullable enable
using System;

namespace OpenTab.Music.Playback;

/// <summary>Per-track stereo chorus with a modulated fractional delay line.</summary>
public sealed class ChorusTrackEffect : ITrackAudioEffect
{
    private readonly AudioAutomationCurve _rate, _depth, _mix;
    private readonly float[] _leftDelay, _rightDelay;
    private readonly float _sampleRate, _smoothing;
    private float _smoothRate, _smoothDepth, _smoothMix;
    private double _phase;
    private int _write;
    private bool _initialized;

    public ChorusTrackEffect(int sampleRate, AudioAutomationCurve rate, AudioAutomationCurve depth, AudioAutomationCurve mix)
    {
        if (sampleRate <= 0) throw new ArgumentOutOfRangeException(nameof(sampleRate));
        _sampleRate = sampleRate;
        _smoothing = 1f - MathF.Exp(-1f / (sampleRate * 0.01f));
        _rate = rate; _depth = depth; _mix = mix;
        // 12 ms centre delay +/- 10 ms modulation, with a little interpolation room.
        var frames = checked((int)Math.Ceiling(sampleRate * .025) + 4);
        _leftDelay = new float[frames]; _rightDelay = new float[frames];
    }

    public void Process(Span<float> left, Span<float> right, long sourceFrame, bool holdAutomation = false)
    {
        if (left.Length != right.Length) throw new ArgumentException("Stereo buffers must have equal lengths.");
        for (var i = 0; i < left.Length; i++)
        {
            var frame = holdAutomation ? sourceFrame : sourceFrame + i;
            var rate = _rate.ValueAt(frame); var depth = _depth.ValueAt(frame); var mix = _mix.ValueAt(frame);
            if (!_initialized) { _smoothRate = rate; _smoothDepth = depth; _smoothMix = mix; _initialized = true; }
            _smoothRate += (rate - _smoothRate) * _smoothing;
            _smoothDepth += (depth - _smoothDepth) * _smoothing;
            _smoothMix += (mix - _smoothMix) * _smoothing;

            var leftInput = left[i]; var rightInput = right[i];
            _leftDelay[_write] = leftInput; _rightDelay[_write] = rightInput;
            var modulation = (float)Math.Sin(_phase);
            var centre = .012f * _sampleRate; var excursion = .010f * _sampleRate * _smoothDepth;
            var wetLeft = ReadDelay(_leftDelay, _write, centre + excursion * modulation);
            var wetRight = ReadDelay(_rightDelay, _write, centre - excursion * modulation);
            left[i] = leftInput * (1f - _smoothMix) + wetLeft * _smoothMix;
            right[i] = rightInput * (1f - _smoothMix) + wetRight * _smoothMix;
            if (++_write == _leftDelay.Length) _write = 0;
            _phase += 2d * Math.PI * (.1 + 4.9 * _smoothRate) / _sampleRate;
            if (_phase >= 2d * Math.PI) _phase -= 2d * Math.PI;
        }
    }

    internal static float ReadDelay(float[] buffer, int write, float delayFrames)
    {
        // Wrapping a tiny negative float can round up to buffer.Length. Keep
        // the fractional position in double precision before converting to an index.
        var position = (double)write - Math.Clamp(delayFrames, 1f, buffer.Length - 2f);
        if (position < 0) position += buffer.Length;
        if (position >= buffer.Length) position -= buffer.Length;
        var first = (int)position; var next = first + 1;
        if (next == buffer.Length) next = 0;
        var fraction = (float)(position - first);
        return buffer[first] + (buffer[next] - buffer[first]) * fraction;
    }
}

/// <summary>Per-track stereo feedback delay; time, feedback and wet mix are automatable.</summary>
public sealed class DelayTrackEffect : ITrackAudioEffect
{
    private readonly AudioAutomationCurve _time, _feedback, _mix;
    private readonly float[] _leftDelay, _rightDelay;
    private readonly float _sampleRate, _smoothing;
    private float _smoothTime, _smoothFeedback, _smoothMix;
    private int _write;
    private bool _initialized;

    public DelayTrackEffect(int sampleRate, AudioAutomationCurve time, AudioAutomationCurve feedback, AudioAutomationCurve mix)
    {
        if (sampleRate <= 0) throw new ArgumentOutOfRangeException(nameof(sampleRate));
        _sampleRate = sampleRate;
        _smoothing = 1f - MathF.Exp(-1f / (sampleRate * 0.01f));
        _time = time; _feedback = feedback; _mix = mix;
        var frames = checked(sampleRate + 4); // up to one second of delay
        _leftDelay = new float[frames]; _rightDelay = new float[frames];
    }

    public void Process(Span<float> left, Span<float> right, long sourceFrame, bool holdAutomation = false)
    {
        if (left.Length != right.Length) throw new ArgumentException("Stereo buffers must have equal lengths.");
        for (var i = 0; i < left.Length; i++)
        {
            var frame = holdAutomation ? sourceFrame : sourceFrame + i;
            var time = _time.ValueAt(frame); var feedback = _feedback.ValueAt(frame); var mix = _mix.ValueAt(frame);
            if (!_initialized) { _smoothTime = time; _smoothFeedback = feedback; _smoothMix = mix; _initialized = true; }
            _smoothTime += (time - _smoothTime) * _smoothing;
            _smoothFeedback += (feedback - _smoothFeedback) * _smoothing;
            _smoothMix += (mix - _smoothMix) * _smoothing;

            var leftInput = left[i]; var rightInput = right[i];
            var delayFrames = (.04f + .96f * _smoothTime) * _sampleRate;
            var wetLeft = ChorusTrackEffect.ReadDelay(_leftDelay, _write, delayFrames);
            var wetRight = ChorusTrackEffect.ReadDelay(_rightDelay, _write, delayFrames);
            _leftDelay[_write] = leftInput + wetLeft * (.85f * _smoothFeedback);
            _rightDelay[_write] = rightInput + wetRight * (.85f * _smoothFeedback);
            left[i] = leftInput * (1f - _smoothMix) + wetLeft * _smoothMix;
            right[i] = rightInput * (1f - _smoothMix) + wetRight * _smoothMix;
            if (++_write == _leftDelay.Length) _write = 0;
        }
    }
}
