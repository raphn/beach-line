using System;
using System.IO;
using System.Text;
using Godot;

#nullable enable

namespace BeachLine.Music.Audio;

/// <summary>
/// Streams microphone samples from Godot's audio capture ring buffer into a PCM WAV file.
/// Audio input must be enabled with the project setting audio/driver/enable_input.
/// </summary>
public partial class MicrophoneWavRecorder : Node
{
	public enum ChannelMode { Mono = 1, Stereo = 2 }
	public enum RecorderState { Idle, Recording, Paused, Faulted }

	private const string CaptureBusName = "BeachLineRecordingCapture";
	private string _captureBusName = CaptureBusName;
	private const int DrainFrameLimit = 8192;
	private AudioEffectCapture? _capture;
	private AudioStreamPlayer? _microphonePlayer;
	private FileStream? _output;
	private string? _partialPath;
	private string? _previousInputDevice;
	private bool _inputStartedByRecorder;
	private int _busIndex = -1;
	private bool _ownsBus;
	private int _sampleRate;
	private int _channels;
	private long _dataBytes;
	private long _discardedFramesAtStart;

	public RecorderState State { get; private set; } = RecorderState.Idle;
	public bool IsTakeActive => State is RecorderState.Recording or RecorderState.Paused;
	public bool IsPaused => State == RecorderState.Paused;
	public string? LastError { get; private set; }
	public long FramesWritten => _channels == 0 ? 0 : _dataBytes / (_channels * 2);

	/// <summary>Raised when recording fails. The writer is closed and the partial file is retained for recovery.</summary>
	public event Action<string>? RecordingFailed;

	public static string[] GetInputDevices() => AudioServer.GetInputDeviceList();

	public override void _Ready()
	{
		CreateCaptureRoute();
		SetProcess(false);
	}

	/// <summary>Begin recording to an absolute path or a Godot user:// path. The destination must not exist.</summary>
	public Error Start(string destinationPath, ChannelMode channelMode = ChannelMode.Stereo, string inputDevice = "Default")
	{
		if (IsTakeActive)
			return Error.Busy;
		if (_capture is null || _microphonePlayer is null || !IsInsideTree())
			return Fail("Recorder node must be inside the scene tree before starting.", Error.Unconfigured);
		if (!ProjectSettings.GetSetting("audio/driver/enable_input", false).AsBool())
			return Fail("Microphone input is disabled. Set audio/driver/enable_input=true in project settings and restart the project.", Error.Unavailable);

		string[] devices = AudioServer.GetInputDeviceList();
		if (inputDevice != "Default" && Array.IndexOf(devices, inputDevice) < 0)
			return Fail($"Input device '{inputDevice}' is not available.", Error.InvalidParameter);

		try
		{
			string fullPath = destinationPath.StartsWith("user://", StringComparison.Ordinal)
				? ProjectSettings.GlobalizePath(destinationPath)
				: Path.GetFullPath(destinationPath);
			string? directory = Path.GetDirectoryName(fullPath);
			if (!string.IsNullOrEmpty(directory))
				Directory.CreateDirectory(directory);
			_output = new FileStream(fullPath, FileMode.CreateNew, System.IO.FileAccess.ReadWrite, FileShare.Read, 65536, FileOptions.SequentialScan);
			_partialPath = fullPath;
			_channels = (int)channelMode;
			// AudioEffectCapture receives frames from the audio bus, which runs at the output mix rate.
			_sampleRate = checked((int)Math.Round(AudioServer.GetMixRate()));
			_dataBytes = 0;
			WriteWavHeader(_output, 0, _channels, _sampleRate);
			_previousInputDevice = AudioServer.InputDevice;
			AudioServer.InputDevice = inputDevice;
			if (AudioServer.SetInputDeviceActive(true) != Error.Ok)
				throw new IOException("Godot could not activate the selected microphone input.");
			_inputStartedByRecorder = true;
			_capture.ClearBuffer();
			_discardedFramesAtStart = _capture.GetDiscardedFrames();
			_microphonePlayer.Play();
			State = RecorderState.Recording;
			LastError = null;
			SetProcess(true);
			return Error.Ok;
		}
		catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or OverflowException)
		{
			AbortCurrentCapture();
			return Fail(ex.Message, Error.CantCreate);
		}
	}

	/// <summary>Drain pending capture frames and finish the WAV. Returns the finalized file path on success.</summary>
	public string? Stop()
	{
		if (!IsTakeActive)
			return null;
		try
		{
			if (State == RecorderState.Recording)
				DrainCapture();
			_microphonePlayer?.Stop();
			_capture?.ClearBuffer();
			SetProcess(false);
			if (_output is null || _partialPath is null)
				throw new IOException("Recording output is no longer available.");
			WriteWavHeader(_output, checked((uint)_dataBytes), _channels, _sampleRate);
			_output.Flush(true);
			_output.Dispose();
			_output = null;
			string result = _partialPath;
			_partialPath = null;
			RestoreInputDevice();
			State = RecorderState.Idle;
			return result;
		}
		catch (Exception ex) when (ex is IOException or OverflowException or ObjectDisposedException)
		{
			return Fault(ex.Message);
		}
	}

	/// <summary>Flush samples captured so far, then stop capture without retaining pause-time samples.</summary>
	public bool Pause(bool discardPendingSamples = false)
	{
		if (State == RecorderState.Paused) return true;
		if (State != RecorderState.Recording) return false;
		try
		{
			if (discardPendingSamples)
				_capture?.ClearBuffer();
			else
				DrainCapture();
			_microphonePlayer?.Stop();
			_capture?.ClearBuffer();
			SetProcess(false);
			State = RecorderState.Paused;
			return true;
		}
		catch (Exception ex) when (ex is IOException or OverflowException or ObjectDisposedException)
		{
			Fault(ex.Message);
			return false;
		}
	}

	/// <summary>Resume capture into the same WAV, discarding any samples accumulated while paused.</summary>
	public bool Resume()
	{
		if (State == RecorderState.Recording) return true;
		if (State != RecorderState.Paused || _capture is null || _microphonePlayer is null || _output is null) return false;
		try
		{
			_capture.ClearBuffer();
			_discardedFramesAtStart = _capture.GetDiscardedFrames();
			_microphonePlayer.Play();
			State = RecorderState.Recording;
			SetProcess(true);
			return true;
		}
		catch (Exception ex) when (ex is IOException or ObjectDisposedException)
		{
			Fault(ex.Message);
			return false;
		}
	}

	/// <summary>Stop capture and delete the in-progress file.</summary>
	public void Cancel()
	{
		if (IsTakeActive)
		{
			_microphonePlayer?.Stop();
			_capture?.ClearBuffer();
			SetProcess(false);
		}
		AbortCurrentCapture();
		State = RecorderState.Idle;
		LastError = null;
	}

	public override void _Process(double delta)
	{
		if (State != RecorderState.Recording)
			return;
		try { DrainCapture(); }
		catch (Exception ex) when (ex is IOException or OverflowException or ObjectDisposedException)
		{ Fault(ex.Message); }
	}

	public override void _ExitTree()
	{
		if (IsTakeActive)
			Stop();
		CleanupCaptureRoute();
	}

	private void CreateCaptureRoute()
	{
		if (AudioServer.GetBusIndex(_captureBusName) >= 0)
			_captureBusName = CaptureBusName + "_" + Guid.NewGuid().ToString("N");
		_busIndex = AudioServer.BusCount;
		AudioServer.AddBus(_busIndex);
		AudioServer.SetBusName(_busIndex, _captureBusName);
		_ownsBus = true;
		_capture = new AudioEffectCapture { BufferLength = 8.0f };
		AudioServer.AddBusEffect(_busIndex, _capture);
		// The muted bus still feeds its capture effect while preventing live microphone monitoring.
		AudioServer.SetBusMute(_busIndex, true);
		_microphonePlayer = new AudioStreamPlayer
		{
			Stream = new AudioStreamMicrophone(),
			Bus = _captureBusName,
			Autoplay = false
		};
		AddChild(_microphonePlayer);
	}

	private void DrainCapture()
	{
		if (_capture is null || _output is null)
			throw new IOException("Audio capture is not initialized.");
		long discarded = _capture.GetDiscardedFrames() - _discardedFramesAtStart;
		if (discarded > 0)
			throw new IOException($"Microphone capture dropped {discarded} frames because the capture buffer filled. The take is incomplete.");
		while (_capture.GetFramesAvailable() > 0)
		{
			int frames = Math.Min(_capture.GetFramesAvailable(), DrainFrameLimit);
			Vector2[] samples = _capture.GetBuffer(frames);
			byte[] pcm = new byte[frames * _channels * 2];
			int output = 0;
			foreach (Vector2 frame in samples)
			{
				if (_channels == 2)
				{
					WritePcm16(pcm, ref output, frame.X);
					WritePcm16(pcm, ref output, frame.Y);
				}
				else
					WritePcm16(pcm, ref output, (frame.X + frame.Y) * 0.5f);
			}
			if (_dataBytes + pcm.Length > uint.MaxValue - 36)
				throw new IOException("WAV recording reached the 4 GiB RIFF size limit.");
			_output.Write(pcm, 0, pcm.Length);
			_dataBytes += pcm.Length;
			// Keep the RIFF lengths current so a completed chunk remains playable after a crash.
			WriteWavHeader(_output, checked((uint)_dataBytes), _channels, _sampleRate);
			_output.Flush(true);
		}
	}

	private static void WritePcm16(byte[] buffer, ref int offset, float value)
	{
		short sample = (short)Math.Round(Math.Clamp(value, -1f, 1f) * (value < 0 ? 32768f : 32767f));
		buffer[offset++] = (byte)(sample & 0xff);
		buffer[offset++] = (byte)((sample >> 8) & 0xff);
	}

	private static void WriteWavHeader(Stream stream, uint dataBytes, int channels, int sampleRate)
	{
		stream.Position = 0;
		using var writer = new BinaryWriter(stream, Encoding.ASCII, leaveOpen: true);
		writer.Write(Encoding.ASCII.GetBytes("RIFF")); writer.Write(dataBytes + 36);
		writer.Write(Encoding.ASCII.GetBytes("WAVEfmt ")); writer.Write(16);
		writer.Write((short)1); writer.Write((short)channels); writer.Write(sampleRate);
		writer.Write(sampleRate * channels * 2); writer.Write((short)(channels * 2)); writer.Write((short)16);
		writer.Write(Encoding.ASCII.GetBytes("data")); writer.Write(dataBytes);
		stream.Position = 44 + dataBytes;
	}

	private Error Fail(string message, Error error)
	{
		LastError = message;
		GD.PushError(message);
		return error;
	}

	private string? Fault(string message)
	{
		LastError = message;
		State = RecorderState.Faulted;
		_microphonePlayer?.Stop();
		SetProcess(false);
		try
		{
			if (_output is not null)
			{
				WriteWavHeader(_output, checked((uint)_dataBytes), _channels, _sampleRate);
				_output.Flush(true);
			}
		}
		catch { /* Preserve the original failure and whatever bytes reached disk. */ }
		_output?.Dispose();
		_output = null;
		RestoreInputDevice();
		RecordingFailed?.Invoke(message);
		GD.PushError(message);
		return null;
	}

	private void AbortCurrentCapture()
	{
		_microphonePlayer?.Stop();
		SetProcess(false);
		_output?.Dispose();
		_output = null;
		if (_partialPath is not null)
		{
			try { File.Delete(_partialPath); } catch (IOException) { }
			_partialPath = null;
		}
		RestoreInputDevice();
	}

	private void RestoreInputDevice()
	{
		if (_inputStartedByRecorder)
		{
			AudioServer.SetInputDeviceActive(false);
			_inputStartedByRecorder = false;
		}
		if (_previousInputDevice is not null)
		{
			AudioServer.InputDevice = _previousInputDevice;
			_previousInputDevice = null;
		}
	}

	private void CleanupCaptureRoute()
	{
		if (_microphonePlayer is not null)
		{
			_microphonePlayer.Stop();
			_microphonePlayer.QueueFree();
			_microphonePlayer = null;
		}
		_busIndex = AudioServer.GetBusIndex(_captureBusName);
		if (_busIndex >= 0 && _busIndex < AudioServer.BusCount)
		{
			int effects = AudioServer.GetBusEffectCount(_busIndex);
			for (int i = effects - 1; i >= 0; i--)
				if (AudioServer.GetBusEffect(_busIndex, i) == _capture)
					AudioServer.RemoveBusEffect(_busIndex, i);
			if (_ownsBus && AudioServer.GetBusName(_busIndex) == _captureBusName)
				AudioServer.RemoveBus(_busIndex);
		}
	}
}
