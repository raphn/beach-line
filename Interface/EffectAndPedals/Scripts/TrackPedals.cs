#nullable enable
using Godot;
using OpenTab.Music;
using System;
using System.Collections.Generic;
using System.Linq;

/// <summary>Track-owned pedal chain controls and source-score automation editor.</summary>
public partial class TrackPedals : FoldableContainer
{
	[Export] private PackedScene _pedalScene = null!;
	[Export] private PackedScene _knobScene = null!;
	[Export] private PackedScene _automationScene = null!;
	[Export] private PedalCatalog _catalog = null!;
	[Export] private BoxContainer _pedals = null!;
	[Export] private MenuButton _addPedalButton = null!;
	private PopupMenu? _addPedalMenu;
	private ScoreTrack? _track;
	private Action? _changed;

	public override void _Ready()
	{
		_addPedalMenu = _addPedalButton.GetPopup();
		_addPedalMenu.Clear();
		for (var index = 0; index < _catalog.Pedals.Count; index++)
		{
			var info = _catalog.Pedals[index];
			if (info is not null && !string.IsNullOrWhiteSpace(info.TypeId))
				_addPedalMenu.AddItem(Tr(info.DisplayName), index);
		}
		_addPedalMenu.IdPressed += AddPedal;
		Rebuild();
	}

	public override void _ExitTree()
	{
		if (GodotObject.IsInstanceValid(_addPedalMenu)) _addPedalMenu!.IdPressed -= AddPedal;
		_addPedalMenu = null;
		_track = null;
		_changed = null;
	}

	/// <summary>Binds to the existing score track; modifications are reported to its owner.</summary>
	public void Configure(ScoreTrack track, Action changed)
	{
		_track = track ?? throw new ArgumentNullException(nameof(track));
		_changed = changed ?? throw new ArgumentNullException(nameof(changed));
		if (IsNodeReady()) Rebuild();
	}

	private void AddPedal(long index)
	{
		if (_track is null || index < 0 || index >= _catalog.Pedals.Count) return;
		var info = _catalog.Pedals[(int)index];
		if (info is null || string.IsNullOrWhiteSpace(info.TypeId)) return;
		var pedal = new TrackPedal { TypeId = info.TypeId };
		info.DefaultConfiguration.Apply(pedal);
		_track.Pedals.Add(pedal);
		_changed?.Invoke();
		Rebuild();
	}

	private void Rebuild()
	{
		if (_pedals is null) return;
		foreach (var child in _pedals.GetChildren()) { _pedals.RemoveChild(child); child.QueueFree(); }
		if (_track is null) return;
		foreach (var pedal in _track.Pedals)
		{
			var info = _catalog.Find(pedal.TypeId);
			var panel = _pedalScene.Instantiate<EffectPedalTile>();
			_pedals.AddChild(panel);
			panel.TitleLabel.Text = info is null ? pedal.TypeId : Tr(info.DisplayName);
			panel.Artwork.Texture = info?.Image;
			panel.Artwork.Visible = info?.Image is not null;
			panel.EnabledButton.SetPressedNoSignal(pedal.Enabled);
			panel.EnabledButton.Toggled += on => { pedal.Enabled = on; _changed?.Invoke(); };
			panel.RemoveButton.Pressed += () =>
			{
				if (_track?.Pedals.Remove(pedal) == true) { _changed?.Invoke(); Rebuild(); }
			};
			var knobs = panel.Knobs;
			Action? refreshPreset = info is null ? null : BindPresets(panel.PresetButton, pedal, info);
			if (pedal.TypeId == "overdrive")
			{
				AddKnob(knobs, pedal, "drive", Tr("Drive"), () => pedal.Drive, v => pedal.Drive = v, Tr("Adjust the amount of overdrive."), changed: refreshPreset);
				AddKnob(knobs, pedal, "tone", Tr("Tone"), () => pedal.Tone, v => pedal.Tone = v, Tr("Adjust the brightness of the overdrive."), changed: refreshPreset);
				AddKnob(knobs, pedal, "level", Tr("Level"), () => pedal.Level, v => pedal.Level = v, Tr("Adjust the output level after overdrive."), changed: refreshPreset);
			}
			else if (pedal.TypeId == "chorus")
			{
				AddKnob(knobs, pedal, "rate", Tr("Rate"), () => pedal.GetParameter("rate"), v => pedal.SetParameter("rate", v), Tr("Set chorus modulation speed from 0.1 to 5 Hz."), v => $"{0.1f + 4.9f * v:0.0} Hz", changed: refreshPreset);
				AddKnob(knobs, pedal, "depth", Tr("Depth"), () => pedal.GetParameter("depth"), v => pedal.SetParameter("depth", v), Tr("Set the chorus modulation depth from 0 to 10 ms."), v => $"{10f * v:0.0} ms", changed: refreshPreset);
				AddKnob(knobs, pedal, "mix", Tr("Mix"), () => pedal.GetParameter("mix"), v => pedal.SetParameter("mix", v), Tr("Blend the dry and chorus signals."), changed: refreshPreset);
			}
			else if (pedal.TypeId == "delay")
			{
				AddKnob(knobs, pedal, "time", Tr("Time"), () => pedal.GetParameter("time"), v => pedal.SetParameter("time", v), Tr("Set delay time from 40 to 1000 ms."), v => $"{40f + 960f * v:0} ms", changed: refreshPreset);
				AddKnob(knobs, pedal, "feedback", Tr("Feedback"), () => pedal.GetParameter("feedback"), v => pedal.SetParameter("feedback", v), Tr("Set how much delayed sound repeats, from 0 to 85%."), v => $"{85f * v:0}%", changed: refreshPreset);
				AddKnob(knobs, pedal, "mix", Tr("Mix"), () => pedal.GetParameter("mix"), v => pedal.SetParameter("mix", v), Tr("Blend the dry and delayed signals."), changed: refreshPreset);
			}
			else knobs.AddChild(new Label { Text = Tr("Unsupported effect (preserved)") });
		}
	}

	private Action BindPresets(OptionButton options, TrackPedal pedal, PedalInfo info)
	{
		var configurations = info.BuiltinConfigurations.Prepend(info.DefaultConfiguration).ToArray();
		options.Show();
		options.AddItem(Tr("Custom"));
		foreach (var configuration in configurations) options.AddItem(Tr(configuration.DisplayName));
		void Refresh()
		{
			options.Select(Array.FindIndex(configurations, configuration => configuration.Matches(pedal)) + 1);
		}
		options.ItemSelected += index =>
		{
			if (index <= 0 || index > configurations.Length) return;
			configurations[(int)index - 1].Apply(pedal);
			_changed?.Invoke();
			Rebuild();
		};
		Refresh();
		return Refresh;
	}

	private void AddKnob(BoxContainer parent, TrackPedal pedal, string parameter, string label, Func<float> get, Action<float> set, string tooltip, Func<float, string>? format = null, Action? changed = null)
	{
		var row = _knobScene.Instantiate<EffectPedalKnob>();
		parent.AddChild(row);
		row.NameLabel.Text = label;
		var slider = row.Slider;
		slider.SetValueNoSignal(get());
		slider.TooltipText = tooltip + " " + Tr("When keyframes exist, they override this value throughout playback; the first and last values are held outside the keyframes.");
		var valueLabel = row.ValueLabel;
		format ??= v => $"{v * 100:0}%";
		valueLabel.Text = format(get());
		slider.ValueChanged += value => { set((float)value); valueLabel.Text = format((float)value); changed?.Invoke(); _changed?.Invoke(); };
		var automation = row.AutomationButton;
		void RefreshIndicator() => automation.Text = pedal.Automation.Any(lane => lane.ParameterId == parameter && lane.Points.Count > 0) ? "A•" : "A";
		RefreshIndicator();
		automation.Pressed += () => OpenAutomation(pedal, parameter, label, get(), RefreshIndicator);
	}

	private sealed class DraftPoint
	{
		public double Position;
		public float Value;
		public ExpressionSegmentMode Mode;
		public PedalAutomationPoint? Original;
		public DraftPoint(double position, float value, ExpressionSegmentMode mode, PedalAutomationPoint? original = null) { Position = position; Value = value; Mode = mode; Original = original; }
	}

	private void OpenAutomation(TrackPedal pedal, string parameter, string label, float fallback, Action refreshIndicator)
	{
		var lane = pedal.Automation.FirstOrDefault(item => item.ParameterId == parameter);
		var draft = lane?.Points.Select(p => new DraftPoint(p.QuarterNote, p.Value, p.SegmentMode, p)).ToList() ?? new List<DraftPoint>();
		var dialog = _automationScene.Instantiate<ConfirmationDialog>();
		AddChild(dialog);
		dialog.Title = string.Format(Tr("{0} automation"), label);
		var rows = dialog.GetNode<VBoxContainer>("%Rows");
		var status = dialog.GetNode<Label>("%Status");
		void Validate()
		{
			var ordered = draft.OrderBy(point => point.Position).ToArray();
			bool valid = ordered.Zip(ordered.Skip(1), (a, b) => b.Position > a.Position).All(value => value);
			dialog.GetOkButton().Disabled = !valid;
			status.Text = valid ? Tr("No keyframes uses the slider value. Outside keyframes, the nearest value is held.") : Tr("Keyframes must have different positions.");
		}
		void RebuildRows()
		{
			foreach (var child in rows.GetChildren()) { rows.RemoveChild(child); child.QueueFree(); }
			foreach (var point in draft)
			{
				var row = new HBoxContainer(); rows.AddChild(row);
				var position = new SpinBox { MinValue = 0, MaxValue = 1000000, Step = 0.25, Value = point.Position, CustomMinimumSize = new Vector2(110, 0), TooltipText = Tr("Quarter-note position from the start of the source score.") };
				var value = new SpinBox { MinValue = 0, MaxValue = 100, Step = 1, Value = point.Value * 100, CustomMinimumSize = new Vector2(90, 0), TooltipText = Tr("Effect value as a percentage.") };
				var mode = new OptionButton { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, TooltipText = Tr("Choose how the value changes until the next keyframe.") };
				mode.AddItem(Tr("Linear")); mode.AddItem(Tr("Hold")); mode.Select(point.Mode == ExpressionSegmentMode.Step ? 1 : 0);
				var remove = new Button { Text = "×", TooltipText = Tr("Remove this automation keyframe.") };
				row.AddChild(position); row.AddChild(value); row.AddChild(mode); row.AddChild(remove);
				position.ValueChanged += v => { point.Position = v; Validate(); };
				value.ValueChanged += v => { point.Value = (float)(v / 100); Validate(); };
				mode.ItemSelected += v => { point.Mode = v == 1 ? ExpressionSegmentMode.Step : ExpressionSegmentMode.Linear; };
				remove.Pressed += () => { draft.Remove(point); RebuildRows(); };
			}
			Validate();
		}
		dialog.GetNode<Button>("%AddPoint").Pressed += () =>
		{
			double position = draft.Count == 0 ? 0 : draft.Max(point => point.Position) + 1;
			draft.Add(new DraftPoint(position, fallback, ExpressionSegmentMode.Linear)); RebuildRows();
		};
		dialog.Confirmed += () =>
		{
			if (!GodotObject.IsInstanceValid(this) || _track is null || !_track.Pedals.Contains(pedal)) { dialog.QueueFree(); return; }
			var existing = pedal.Automation.FirstOrDefault(item => item.ParameterId == parameter);
			if (existing is null && draft.Count > 0)
			{
				existing = new PedalAutomationLane { ParameterId = parameter };
				pedal.Automation.Add(existing);
			}
			if (existing is not null)
			{
				existing.Points.Clear();
				foreach (var point in draft.OrderBy(point => point.Position))
					existing.Points.Add(new PedalAutomationPoint(point.Position, point.Value, point.Mode) { ExtensionData = point.Original?.ExtensionData });
			}
			_changed?.Invoke();
			refreshIndicator();
			dialog.QueueFree();
		};
		dialog.Canceled += dialog.QueueFree;
		RebuildRows();
		dialog.PopupCentered();
	}
}
