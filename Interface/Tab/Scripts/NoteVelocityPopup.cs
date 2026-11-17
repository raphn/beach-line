#nullable enable
using Godot;
using System;
using System.Collections.Generic;
using OpenTab.Music;

/// <summary>Compact floating editor for the five discrete note/beat velocity levels.</summary>
public partial class NoteVelocityPopup : PopupPanel
{
	private const int LevelMin = 1;
	private const int LevelMax = 5;
	private const int PopupWidth = 248;
	private const int PopupHeight = 252;
	private HSlider _slider = null!;
	private Label _valueLabel = null!;
	private Label _selectionFeedback = null!;
	private OptionButton _tupletOption = null!;
	private Button _effectsButton = null!;
	private HBoxContainer _articulationRow = null!;
	private Button _hammerOnButton = null!;
	private Button _pullOffButton = null!;
	private Button _clearArticulationButton = null!;
	private Button _previewArticulationButton = null!;
	private HBoxContainer _variationRow = null!;
	private OptionButton _variationOption = null!;
	private readonly List<string> _variationIds = new();
	private IReadOnlyList<SoundVariationOption> _variations = Array.Empty<SoundVariationOption>();
	private string? _selectedVariation;
	private int _selectionCount = 1;
	private bool _variationsConfigured;

	/// <summary>Raised when the user selects a different velocity level (1–5).</summary>
	public event Action<int>? LevelChanged;
	/// <summary>Raised when a tuplet mode is selected (0 straight, 3, 5, or 6).</summary>
	public event Action<int>? TupletChanged;
	/// <summary>Raised whenever the popup is dismissed, including outside-click dismissal.</summary>
	public event Action? Closed;
	/// <summary>Raised when the user asks to open the reusable note expression editor.</summary>
	public event Action? EffectsRequested;
	public event Action<string>? VariationChanged;
	public event Action<StringArticulation>? ArticulationChanged;
	public event Action? ArticulationPreviewRequested;

	public int Level => _slider is null ? LevelMin : (int)_slider.Value;

	public override void _Ready()
	{
		_slider = GetNode<HSlider>("%LevelSlider");
		_valueLabel = GetNode<Label>("%ValueLabel");
		_selectionFeedback = GetNode<Label>("%SelectionFeedback");
		_tupletOption = GetNode<OptionButton>("%TupletOption");
		_effectsButton = GetNode<Button>("%EffectsButton");
		_articulationRow = GetNode<HBoxContainer>("%ArticulationRow");
		_hammerOnButton = GetNode<Button>("%HammerOnButton");
		_pullOffButton = GetNode<Button>("%PullOffButton");
		_clearArticulationButton = GetNode<Button>("%ClearArticulationButton");
		_previewArticulationButton = GetNode<Button>("%PreviewArticulationButton");
		_variationRow = GetNode<HBoxContainer>("%VariationRow");
		_variationOption = GetNode<OptionButton>("%VariationOption");
		_variationOption.ItemSelected += OnVariationSelected;
		RefreshLocalizedText();
		_slider.ValueChanged += OnValueChanged;
		_tupletOption.ItemSelected += OnTupletSelected;
		_effectsButton.Pressed += () => EffectsRequested?.Invoke();
		_hammerOnButton.Pressed += () => ArticulationChanged?.Invoke(StringArticulation.HammerOn);
		_pullOffButton.Pressed += () => ArticulationChanged?.Invoke(StringArticulation.PullOff);
		_clearArticulationButton.Pressed += () => ArticulationChanged?.Invoke(StringArticulation.None);
		_previewArticulationButton.Pressed += () => ArticulationPreviewRequested?.Invoke();
		PopupHide += OnPopupHide;
		UpdateValueLabel(Level);
		UpdateSelectionFeedback(1);
	}

	public void SetArticulationAvailable(bool available) => _articulationRow.Visible = available;
	public void SetArticulationPreviewAvailable(bool available) => _previewArticulationButton.Visible = available;

	public void SetVariations(IReadOnlyList<SoundVariationOption> options, string? selected)
	{
		_variationRow.Visible = options.Count > 0;
		_variations = options;
		_selectedVariation = selected;
		_variationsConfigured = true;
		_variationOption.Clear();
		_variationIds.Clear();
		if (selected is null) { _variationOption.AddItem(Tr("Mixed")); _variationIds.Add("\0"); }
		_variationOption.AddItem(Tr("Original")); _variationIds.Add("");
		foreach (var option in options) { _variationOption.AddItem(option.DisplayName); _variationIds.Add(option.Id); }
		var index = selected is null ? 0 : _variationIds.IndexOf(selected);
		_variationOption.Select(Math.Max(0, index));
		_variationOption.TooltipText = Tr("Some variations use a different instrument preset, so their tone may change.");
	}

	private void OnVariationSelected(long index)
	{
		if (index >= 0 && index < _variationIds.Count && _variationIds[(int)index] != "\0")
			VariationChanged?.Invoke(_variationIds[(int)index]);
	}

	/// <summary>Initializes the selected level and selection count without raising LevelChanged.</summary>
	public void Initialize(int level, int selectionCount = 1, int? tupletActual = 0)
	{
		if (_slider is null) throw new InvalidOperationException("Add NoteVelocityPopup to the scene tree before initializing it.");
		_slider.SetValueNoSignal(Math.Clamp(level, LevelMin, LevelMax));
		UpdateValueLabel(Level);
		UpdateSelectionFeedback(selectionCount);
		_tupletOption.Select(tupletActual is null ? 0 : tupletActual switch { 0 => 1, 3 => 2, 5 => 3, 6 => 4, _ => 0 });
		_selectionFeedback.TooltipText = "";
	}

	public void SetTupletSelection(int? tupletActual)
	{
		_tupletOption.Select(tupletActual is null ? 0 : tupletActual switch { 0 => 1, 3 => 2, 5 => 3, 6 => 4, _ => 0 });
	}

	public void ShowTupletError(string message)
	{
		_selectionFeedback.Visible = true;
		_selectionFeedback.Text = Tr(message);
		_selectionFeedback.TooltipText = Tr(message);
	}

	/// <summary>Opens near a point in viewport coordinates, clamped to the visible viewport.</summary>
	public void OpenAt(Vector2 viewportPosition)
	{
		var parentViewport = GetParent().GetViewport();
		var embedded = parentViewport.GuiEmbedSubwindows;
		// Native windows use desktop pixels; embedded windows use parent viewport coordinates.
		var position = embedded ? viewportPosition : (Vector2)DisplayServer.MouseGetPosition();
		var bounds = embedded
			? parentViewport.GetVisibleRect()
			: (Rect2)DisplayServer.ScreenGetUsableRect((int)DisplayServer.ScreenWithMouseFocus);
		var size = new Vector2(PopupWidth, PopupHeight);
		var offset = new Vector2(8, 8);
		var desired = position + offset;
		if (desired.X + size.X > bounds.End.X) desired.X = position.X - size.X - offset.X;
		if (desired.Y + size.Y > bounds.End.Y) desired.Y = position.Y - size.Y - offset.Y;
		desired.X = Mathf.Clamp(desired.X, bounds.Position.X, Mathf.Max(bounds.Position.X, bounds.End.X - size.X));
		desired.Y = Mathf.Clamp(desired.Y, bounds.Position.Y, Mathf.Max(bounds.Position.Y, bounds.End.Y - size.Y));
		Popup(new Rect2I(new Vector2I(Mathf.RoundToInt(desired.X), Mathf.RoundToInt(desired.Y)),
			new Vector2I(PopupWidth, PopupHeight)));
	}

	private void OnValueChanged(double value)
	{
		var level = Math.Clamp((int)Math.Round(value), LevelMin, LevelMax);
		UpdateValueLabel(level);
		LevelChanged?.Invoke(level);
	}

	private void OnTupletSelected(long index)
	{
		if (index <= 0) return;
		TupletChanged?.Invoke(index switch { 1 => 0, 2 => 3, 3 => 5, 4 => 6, _ => 0 });
	}

	private void UpdateValueLabel(int level) => _valueLabel.Text = level.ToString();
	private void UpdateSelectionFeedback(int selectionCount)
	{
		var count = Math.Max(1, selectionCount);
		_selectionCount = count;
		_selectionFeedback.Visible = count > 1;
		_selectionFeedback.Text = string.Format(Tr("Editing {0} notes"), count);
	}

	public override void _Notification(int what)
	{
		if (what != NotificationTranslationChanged || !IsInsideTree() || _selectionFeedback is null) return;
		RefreshLocalizedText();
		if (_variationsConfigured) SetVariations(_variations, _selectedVariation);
		if (_selectionFeedback.Visible) UpdateSelectionFeedback(_selectionCount);
	}

	private void RefreshLocalizedText()
	{
		if (_effectsButton is null) return;
		_effectsButton.TooltipText = Tr("Curve underlines: green velocity, blue pitch, violet vibrato, orange tremolo. Matching shades identify linked curves.");
		if (_variationOption is not null) _variationOption.TooltipText = Tr("Some variations use a different instrument preset, so their tone may change.");
	}
	private void OnPopupHide() => Closed?.Invoke();
}
