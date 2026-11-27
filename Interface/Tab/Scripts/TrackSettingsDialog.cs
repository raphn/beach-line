#nullable enable
using Godot;
using OpenTab.Music;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

/// <summary>Stages track creation or edits before the presenter commits them.</summary>
public partial class TrackSettingsDialog : ConfirmationDialog
{
	private readonly PackedScene _stringTuningRowScene = GD.Load<PackedScene>("res://Interface/Tracks/Scenes/StringTuningRow.tscn");
	private static readonly (string Id, string Label, string Path)[] InstrumentImages =
	[
		("accordion", "Accordion", "accordion.png"),
		("acoustic-guitar", "Acoustic guitar", "acoustic-guitar.png"),
		("banjo", "Banjo", "banjo.png"),
		("cello", "Cello", "cello.png"),
		("clarinet", "Clarinet", "clarinet.png"),
		("e-bass", "Electric bass", "e-bass.png"),
		("flute", "Flute", "flute.png"),
		("french-horn", "French horn", "french-horn.png"),
		("guitar", "Electric guitar", "guitar.png"),
		("harp", "Harp", "harp.png"),
		("piano", "Piano", "piano.png"),
		("saxophone", "Saxophone", "saxophone.png"),
		("tambourine", "Tambourine", "tambourine.png"),
		("trombone", "Trombone", "trombone.png"),
		("trumpet", "Trumpet", "trumpet.png"),
		("ukulele", "Ukulele", "ukulele.png"),
		("violin", "Violin", "violin.png"),
		("xylophone", "Xylophone", "xylophone.png"),
		("percussion-kit", "Percussion kit", "percussion-kit.svg"),
		("placeholder", "Other", "")
	];
	private static readonly string[] Categories =
	[
		"Piano", "Chromatic percussion", "Organ", "Guitar", "Bass", "Strings", "Ensemble and choir", "Brass",
		"Reeds", "Pipe", "Synth lead", "Synth pad", "Synth effects", "Ethnic", "Percussive effects", "Sound effects", "Percussion kit"
	];
	private static readonly Dictionary<string, int> ImageIndexById = InstrumentImages
		.Select((image, index) => (image.Id, index))
		.ToDictionary(item => item.Id, item => item.index, StringComparer.Ordinal);
	private static string ImagePath(int index) => InstrumentImages[index].Id == "placeholder"
		? "res://_project/icon.png"
		: $"res://Interface/Common/Graphs/instruments/{InstrumentImages[index].Path}";

	private BoxContainer _instrumentInfoHeader = null!;
	private TextureRect _instrumentHeaderIcon = null!;
	private Label _instrumentHeaderLabel = null!;
	private BoxContainer _instrumentPicker = null!;
	private TabContainer _instrumentBranch = null!;
	private Button _defaultInstrumentButton = null!;
	private int _selectedProgram = 25;
	[Export] private ItemList _bannerImages = null!;
	[Export] private AcceptDialog _bannerPopup = null!;
	[Export] private Button _chooseBannerButton = null!;
	[Export] private TextureRect _bannerPreview = null!;
	private LineEdit _name = null!;
	private VBoxContainer _tuningSection = null!;
	private Button _initializeTuning = null!;
	private Button _addHighString = null!;
	private Button _addLowString = null!;
	private VBoxContainer _stringRows = null!;
	private VBoxContainer _retuningPanel = null!;
	private OptionButton _retuningMode = null!;
	private Label _retuningFeedback = null!;
	private Label _kindInfo = null!;
	private Label _tuningHelp = null!;
	private Button _colorButton = null!;
	private ColorRect _colorSwatch = null!;
	private Button _okButton = null!;
	private Color _selectedColor = Colors.White;
	private uint _configuredAlpha = 0xff;
	private string _selectedBannerImagePath = "";
	private TrackKind _selectedKind = TrackKind.Guitar;
	private int[] _selectedTuning = [64, 59, 55, 50, 45, 40];
	private int[] _originalTuning = Array.Empty<int>();
	private Func<int[], (bool CanKeepFingering, bool CanKeepPitch, string FingeringError, string PitchError)>? _evaluateRetuning;
	private bool _isEditing;
	private bool _nameCustomized;
	private bool _colorCustomized;
	private bool _synchronizing;
	private bool _tuningInvalid;
	private bool _tuningReordered;
	private Window? _colorSelector;

	public string SelectedName => _name is null ? "" : _name.Text.Trim();
	public TrackKind SelectedKind => _selectedKind;
	public int SelectedProgram => _selectedProgram;
	public int[] SelectedTuning => (int[])_selectedTuning.Clone();
	public bool TuningChanged => _tuningReordered || !_selectedTuning.SequenceEqual(_originalTuning);
	public TabRetuningMode SelectedRetuningMode => _retuningMode is not null && _retuningMode.Selected == 1 ? TabRetuningMode.KeepPitch : TabRetuningMode.KeepFingering;
	public Func<int[], (bool CanKeepFingering, bool CanKeepPitch, string FingeringError, string PitchError)>? EvaluateRetuning
	{
		get => _evaluateRetuning;
		set { _evaluateRetuning = value; if (IsInsideTree()) RefreshRetuningOptions(); }
	}
	public uint SelectedColorRgba => PackColor(_selectedColor, _configuredAlpha);
	public string SelectedBannerImagePath => _selectedBannerImagePath;

	public override void _Ready()
	{
		_kindInfo = GetNode<Label>("%TrackInfo");
		_instrumentInfoHeader = GetNode<BoxContainer>("%InstrumentInfoHeader");
		_instrumentHeaderIcon = GetNode<TextureRect>("%InstrumentHeaderIcon");
		_instrumentHeaderLabel = GetNode<Label>("%InstrumentHeaderLabel");
		_instrumentPicker = GetNode<BoxContainer>("%Instruments");
		_instrumentBranch = GetNode<TabContainer>("%InstrumentBranch");
		_name = GetNode<LineEdit>("%TrackName");
		_tuningSection = GetNode<VBoxContainer>("%TuningSection");
		_initializeTuning = GetNode<Button>("%InitializeTuning");
		_addHighString = GetNode<Button>("%AddHighString");
		_addLowString = GetNode<Button>("%AddLowString");
		_stringRows = GetNode<VBoxContainer>("%StringRows");
		_retuningPanel = GetNode<VBoxContainer>("%RetuningPanel");
		_retuningMode = GetNode<OptionButton>("%RetuningMode");
		_retuningFeedback = GetNode<Label>("%RetuningFeedback");
		_tuningHelp = GetNode<Label>("%TuningHelp");
		_colorButton = GetNode<Button>("%TrackColor");
		_colorSwatch = _colorButton.GetNode<ColorRect>("ColorSwatch");
		_okButton = GetOkButton();

		ConfigureInstrumentButtons();

		_bannerImages.ItemSelected += OnBannerSelected;
		_chooseBannerButton.Pressed += OpenBannerPicker;
		_name.TextChanged += OnNameChanged;
		_initializeTuning.Pressed += InitializeTuning;
		_addHighString.Pressed += () => AddString(high: true);
		_addLowString.Pressed += () => AddString(high: false);
		_retuningMode.ItemSelected += OnRetuningModeSelected;
		_retuningMode.AddItem("Keep fingering (change pitch)", 0);
		_retuningMode.AddItem("Keep pitch (adjust fingering)", 1);
		_colorButton.Pressed += ShowColorSelector;
		ConfigureNew();
	}

	/// <summary>Sets up a fresh guitar track. Call after the dialog enters the scene tree.</summary>
	public void ConfigureNew()
	{
		EnsureReady();
		_isEditing = false;
		_tuningInvalid = false; _tuningReordered = false;
		_nameCustomized = false;
		_colorCustomized = false;
		Title = "Add Track";
		_selectedKind = TrackKind.Guitar;
		_selectedProgram = 25;
		_instrumentInfoHeader.Visible = false;
		_instrumentPicker.Visible = true;
		_instrumentBranch.CurrentTab = 0;
		_synchronizing = true;
		_defaultInstrumentButton.ButtonPressed = true;
		_selectedTuning = [64, 59, 55, 50, 45, 40];
		_originalTuning = Array.Empty<int>();
		_configuredAlpha = 0xff;
		_selectedColor = DefaultColorForProgram(24);
		_selectedBannerImagePath = TrackBannerArt.DefaultPath(_selectedKind);
		_name.Text = DefaultName(25);
		SetTuning(_selectedTuning);
		_colorSwatch.Color = _selectedColor;
		RefreshBannerPreview();
		_synchronizing = false;
		_tuningSection.Visible = true;
		_stringRows.Visible = true;
		_addHighString.Visible = true; _addLowString.Visible = true;
		_initializeTuning.Visible = false;
		_retuningPanel.Visible = false;
		_tuningHelp.Visible = true;
		UpdateKindDisplay();
		UpdateValidation();
	}

	/// <summary>Loads an existing track; loads the track name, color, banner, and any editable tuning.</summary>
	public void Configure(ScoreTrack track)
	{
		ArgumentNullException.ThrowIfNull(track);
		EnsureReady();
		_isEditing = true;
		_tuningInvalid = false; _tuningReordered = false;
		_nameCustomized = true;
		Title = $"{track.Name} Settings";
		_selectedKind = track.Kind;
		_selectedProgram = track.DefaultMidiProgram;
		_instrumentPicker.Visible = false;
		_instrumentInfoHeader.Visible = true;
		_selectedTuning = track.TabTuningMidiPitches.ToArray();
		_originalTuning = (int[])_selectedTuning.Clone();
		_selectedColor = Unpack(track.DisplayColorRgba);
		_configuredAlpha = track.DisplayColorRgba & 0xff;
		_selectedBannerImagePath = TrackBannerArt.ResolvePath(track);
		_synchronizing = true;
		_name.Text = track.Name;
		_colorSwatch.Color = Opaque(_selectedColor);
		SetTuning(_selectedTuning);
		_synchronizing = false;
		RefreshBannerPreview();
		UpdateInstrumentHeader();
		var canTune = track.Kind is TrackKind.Guitar or TrackKind.Bass;
		_tuningSection.Visible = canTune;
		_addHighString.Visible = canTune; _addLowString.Visible = canTune;
		_stringRows.Visible = canTune;
		_initializeTuning.Visible = canTune && _selectedTuning.Length == 0;
		_tuningHelp.Visible = canTune;
		_retuningPanel.Visible = false;
		_kindInfo.Text = track.Kind is TrackKind.Guitar or TrackKind.Bass
			? $"{track.Kind} track · tuning {FormatTuning(track.TabTuningMidiPitches)}"
			: $"{track.Kind} track";
		UpdateValidation();
	}

	public static string ResolveImageId(ScoreTrack track) => ResolveImageId(track.Kind, track.DefaultMidiProgram);

	private static string ResolveImageId(TrackKind kind, int program) => kind switch
	{
		TrackKind.Audio or TrackKind.Ruler => "placeholder",
		TrackKind.Percussion => "percussion-kit",
		_ => DefaultImageIdForProgram(program)
	};

	public static string ResolveImagePath(ScoreTrack track) => ResolveImagePath(track.Kind, track.DefaultMidiProgram);

	private static string ResolveImagePath(TrackKind kind, int program) => kind == TrackKind.Audio
		? "res://Interface/Common/Graphs/audio/import-wav-to-section.svg"
		: ImagePath(ImageIndexById[ResolveImageId(kind, program)]);

	private void ConfigureInstrumentButtons()
	{
		var group = new ButtonGroup();
		var band = GetNode<GridContainer>("%BandInstruments");
		var orchestra = GetNode<GridContainer>("%OrchestraInstruments");
		RegisterInstrument(band, "SetGuitarButton", 25, "acoustic-guitar", group);
		RegisterInstrument(band, "SetElectricGuitarButton", 27, "guitar", group);
		RegisterInstrument(band, "SetElectricBassButton", 33, "e-bass", group);
		RegisterInstrument(band, "SetKeyboardButton", 0, "piano", group);
		RegisterInstrument(band, "SetDrumkitButton", 0, "percussion-kit", group, percussion: true);
		RegisterInstrument(orchestra, "SetViolinButton", 40, "violin", group);
		RegisterInstrument(orchestra, "SetCelloButton", 42, "cello", group);
		RegisterInstrument(orchestra, "SetHarpButton", 46, "harp", group);
		RegisterInstrument(orchestra, "SetTrumpetButton", 56, "trumpet", group);
		RegisterInstrument(orchestra, "SetTromboneButton", 57, "trombone", group);
		RegisterInstrument(orchestra, "SetFrenchHornButton", 60, "french-horn", group);
		RegisterInstrument(orchestra, "SetSaxophoneButton", 65, "saxophone", group);
		RegisterInstrument(orchestra, "SetClarinetButton", 71, "clarinet", group);
		RegisterInstrument(orchestra, "SetFluteButton", 73, "flute", group);
		RegisterInstrument(orchestra, "SetXylophoneButton", 13, "xylophone", group);
		_defaultInstrumentButton = band.GetNode<Button>("SetGuitarButton");
	}

	private void RegisterInstrument(GridContainer container, string buttonName, int program, string imageId, ButtonGroup group, bool percussion = false)
	{
		var button = container.GetNode<Button>(buttonName);
		button.ButtonGroup = group;
		button.Icon = GD.Load<Texture2D>(ImagePath(ImageIndexById[imageId]));
		button.Toggled += pressed =>
		{
			if (!pressed || _synchronizing || _isEditing) return;
			if (percussion) SelectPercussionKit();
			else { _selectedProgram = program; ApplyProgramDefaults(program); }
		};
	}

	private void UpdateInstrumentHeader()
	{
		if (_instrumentHeaderLabel is null) return;
		_instrumentHeaderLabel.Text = _selectedKind == TrackKind.Percussion ? Tr("Percussion kit")
			: _selectedKind == TrackKind.Audio ? Tr("Audio")
			: GeneralMidiInstruments.GetName(Math.Clamp(_selectedProgram, 0, 127));
		_instrumentHeaderIcon.Texture = GD.Load<Texture2D>(ResolveImagePath(_selectedKind, _selectedProgram));
	}

	private void OpenBannerPicker()
	{
		if (_bannerImages.ItemCount == 0)
		{
			foreach (var path in TrackBannerArt.Paths)
			{
				var texture = GD.Load<Texture2D>(path);
				using var image = texture.GetImage();
				if (image.IsCompressed()) image.Decompress();
				var scale = Math.Min(120f / image.GetWidth(), 68f / image.GetHeight());
				image.Resize(Math.Max(1, (int)(image.GetWidth() * scale)),
					Math.Max(1, (int)(image.GetHeight() * scale)), Image.Interpolation.Lanczos);
				var item = _bannerImages.AddItem("", ImageTexture.CreateFromImage(image));
				_bannerImages.SetItemTooltip(item, Path.GetFileNameWithoutExtension(path));
			}
		}

		var index = TrackBannerArt.Paths.ToList().IndexOf(_selectedBannerImagePath);
		if (index >= 0 && index < _bannerImages.ItemCount) _bannerImages.Select(index);
		_bannerPopup.PopupCentered();
		_bannerImages.EnsureCurrentIsVisible();
	}

	private void RefreshBannerPreview()
	{
		_bannerPreview.Texture = GD.Load<Texture2D>(_selectedBannerImagePath);
	}

	private void ApplyProgramDefaults(int program)
	{
		if (!_colorCustomized) ApplyDefaultColor(CategoryForProgram(program));
		_selectedKind = KindForProgram(program);
		if (_selectedKind == TrackKind.Guitar)
		{
			_selectedTuning = [64, 59, 55, 50, 45, 40];
		}
		else if (_selectedKind == TrackKind.Bass)
		{
			_selectedTuning = [43, 38, 33, 28];
		}
		else
		{
			_selectedTuning = Array.Empty<int>();
		}
		if (!_nameCustomized) ApplyNameText(DefaultName(program));
		var canTune = _selectedKind is TrackKind.Guitar or TrackKind.Bass;
		_tuningSection.Visible = canTune;
		_addHighString.Visible = canTune; _addLowString.Visible = canTune;
		_stringRows.Visible = canTune;
		_initializeTuning.Visible = false;
		_tuningHelp.Visible = canTune;
		_synchronizing = true;
		SetTuning(_selectedTuning);
		_synchronizing = false;
		UpdateKindDisplay();
		UpdateValidation();
	}

	private void SelectPercussionKit()
	{
		if (!_colorCustomized) ApplyDefaultColor(Categories.Length - 1);
		_selectedProgram = 0;
		_selectedKind = TrackKind.Percussion;
		_tuningSection.Visible = false;
		_selectedTuning = Array.Empty<int>();
		if (!_nameCustomized) ApplyNameText("Percussion kit");
		_addHighString.Visible = false; _addLowString.Visible = false;
		_stringRows.Visible = false;
		_initializeTuning.Visible = false;
		_retuningPanel.Visible = false;
		_tuningHelp.Visible = false;
		UpdateKindDisplay();
		UpdateValidation();
	}

	private void ApplyNameText(string value)
	{
		_synchronizing = true;
		_name.Text = value;
		_synchronizing = false;
	}

	private void OnNameChanged(string value)
	{
		if (!_synchronizing && !_isEditing) _nameCustomized = !string.Equals(value, _selectedKind == TrackKind.Percussion ? "Percussion kit" : DefaultName(SelectedProgram), StringComparison.Ordinal);
		UpdateValidation();
	}

	private void OnTuningChanged()
	{
		UpdateValidation();
		UpdateKindDisplay();
		RefreshRetuningOptions();
	}

	private void OnRetuningModeSelected(long _) => UpdateRetuningFeedback();

	private void OnBannerSelected(long index)
	{
		if ((ulong)index >= (ulong)TrackBannerArt.Paths.Count) return;
		_selectedBannerImagePath = TrackBannerArt.Paths[(int)index];
		RefreshBannerPreview();
	}

	private void SetTuning(int[] pitches)
	{
		var wasSynchronizing = _synchronizing;
		_synchronizing = true;
		_tuningInvalid = false;
		_selectedTuning = (int[])pitches.Clone();
		RebuildStringRows(pitches, pitches.Length == 0);
		_synchronizing = wasSynchronizing;
	}

	private void InitializeTuning()
	{
		var pitches = _selectedKind == TrackKind.Bass ? new[] { 43 } : new[] { 64 };
		SetTuning(pitches);
		_stringRows.Visible = true;
		_initializeTuning.Visible = false;
		_addHighString.Visible = true; _addLowString.Visible = true;
		OnTuningChanged();
	}

	private void AddString(bool high)
	{
		if (_tuningInvalid || _selectedTuning.Length == 0 || _selectedTuning.Length >= 24) return;
		int pitch = high ? _selectedTuning[0] + 5 : _selectedTuning[^1] - 7;
		if (pitch is < 0 or > 127) return;
		_selectedTuning = high ? new[] { pitch }.Concat(_selectedTuning).ToArray() : _selectedTuning.Append(pitch).ToArray();
		RebuildStringRows(_selectedTuning); OnTuningChanged();
	}

	private void RemoveString(int index)
	{
		if (_tuningInvalid || _selectedTuning.Length <= 1 || (uint)index >= (uint)_selectedTuning.Length) return;
		_selectedTuning = _selectedTuning.Where((_, i) => i != index).ToArray();
		RebuildStringRows(_selectedTuning); OnTuningChanged();
	}

	private void MoveString(int source, int destination)
	{
		if (_tuningInvalid || source < 0 || source >= _selectedTuning.Length || destination < 0 || destination >= _selectedTuning.Length || source == destination) return;
		var pitches = _selectedTuning.ToList(); int pitch = pitches[source]; pitches.RemoveAt(source); pitches.Insert(destination, pitch);
		_selectedTuning = pitches.ToArray(); _tuningReordered = true;
		RebuildStringRows(_selectedTuning); OnTuningChanged();
	}

	private void RebuildStringRows(int[] pitches, bool emptyTuning = false)
	{
		foreach (var child in _stringRows.GetChildren()) { _stringRows.RemoveChild(child); child.QueueFree(); }
		if (emptyTuning)
		{
			_stringRows.AddChild(new Label { Name = "UnconfiguredTuning", Text = "No tuning is configured for this track. Choose Initialize tuning to set one.", AutowrapMode = TextServer.AutowrapMode.WordSmart });
			UpdateAddStringButtons(); return;
		}
		for (var i = 0; i < pitches.Length; i++)
		{
			var row = _stringTuningRowScene.Instantiate<StringTuningRow>();
			row.Name = $"StringRow{i + 1}";
			row.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
			row.Setup((long)GetInstanceId(), i, pitches.Length, pitches[i]);
			row.Selector.PitchChanged += OnStringPitchChanged;
			row.MoveRequested += MoveString; row.RemoveRequested += RemoveString;
			_stringRows.AddChild(row);
		}
		UpdateAddStringButtons();
	}

	private void OnStringPitchChanged()
	{
		if (_synchronizing) return;
		var rows = _stringRows.GetChildren().OfType<StringTuningRow>().ToArray();
		_tuningInvalid = rows.Any(row => !row.Selector.IsValid);
		UpdateAddStringButtons();
		if (_tuningInvalid) { UpdateValidation(); return; }
		_selectedTuning = rows.Select(row => row.Selector.MidiPitch).ToArray();
		UpdateAddStringButtons(); OnTuningChanged();
	}

	private void UpdateAddStringButtons()
	{
		bool disabled = _tuningInvalid || _selectedTuning.Length == 0 || _selectedTuning.Length >= 24;
		_addHighString.Disabled = disabled || _selectedTuning.Length > 0 && _selectedTuning[0] + 5 > 127;
		_addLowString.Disabled = disabled || _selectedTuning.Length > 0 && _selectedTuning[^1] - 7 < 0;
	}
	public void RefreshRetuningOptions()
	{
		if (_retuningPanel is null || !_isEditing || !(_selectedKind is TrackKind.Guitar or TrackKind.Bass)) return;
		var changed = TuningChanged;
		_retuningPanel.Visible = changed;
		if (!changed) { UpdateValidation(); return; }
		if (_evaluateRetuning is null)
		{
			_retuningFeedback.Text = "Retuning options have not been checked yet.";
			UpdateValidation();
			return;
		}
		var options = _evaluateRetuning((int[])_selectedTuning.Clone());
		var previousMode = SelectedRetuningMode;
		_retuningMode.SetItemDisabled(0, !options.CanKeepFingering);
		_retuningMode.SetItemDisabled(1, !options.CanKeepPitch);
		var selectedIndex = previousMode == TabRetuningMode.KeepPitch && options.CanKeepPitch ? 1
			: options.CanKeepFingering ? 0 : options.CanKeepPitch ? 1 : -1;
		_retuningMode.Select(selectedIndex);
		_retuningFeedback.Text = selectedIndex == 0
			? "String and fret positions stay the same; sounding pitches change."
			: selectedIndex == 1
				? "Sounding pitches stay the same; string and fret positions may change. Moving notes onto the same string can shorten overlapping playback."
				: $"No retuning mode is available. Fingering: {options.FingeringError} Pitch: {options.PitchError}";
		UpdateValidation();
	}

	private void UpdateRetuningFeedback()
	{
		if (!_retuningPanel.Visible || _evaluateRetuning is null) return;
		_retuningFeedback.Text = SelectedRetuningMode == TabRetuningMode.KeepPitch
			? "Sounding pitches stay the same; string and fret positions may change. Moving notes onto the same string can shorten overlapping playback."
			: "String and fret positions stay the same; sounding pitches change.";
		UpdateValidation();
	}

	private void UpdateKindDisplay()
	{
		var selectedInfo = _selectedKind is TrackKind.Guitar or TrackKind.Bass
			? $"{_selectedKind} track · tuning {FormatTuning(SelectedTuning)}"
			: $"{_selectedKind} track";
		_kindInfo.Text = Tr("Choose an instrument, then confirm to create the track. Its type cannot be changed later.") + "\n" + selectedInfo;
	}

	private void UpdateValidation()
	{
		if (_okButton is null) return;
		var validName = !string.IsNullOrWhiteSpace(_name.Text);
		var isTunable = _selectedKind is TrackKind.Guitar or TrackKind.Bass;
		var validTuning = !_tuningInvalid && (!isTunable || (_isEditing && _originalTuning.Length == 0 && _selectedTuning.Length == 0) ||
			(_selectedTuning.Length is >= 1 and <= 24 && _selectedTuning.All(pitch => pitch is >= 0 and <= 127)));
		var retuningValid = !_retuningPanel.Visible || (_evaluateRetuning is not null && _retuningMode.Selected >= 0);
		_okButton.Disabled = !validName || !validTuning || !retuningValid;
		_tuningHelp.Text = _tuningInvalid ? "Fix each invalid pitch before changing string order or applying." : _isEditing && _originalTuning.Length == 0 && _selectedTuning.Length == 0
			? "No tuning is configured for this track. Initialize it to add strings."
			: "String 1 is shown first. Drag to reorder; use arrows to move strings. Pitch range: C-1 through G9.";
		_tuningHelp.Modulate = validTuning ? Colors.White : new Color(1f, 0.45f, 0.4f);
	}

	private void ShowColorSelector()
	{
		if (_colorSelector is null)
		{
			var selectorScene = GD.Load<PackedScene>("res://Interface/Tracks/Scenes/PresetColorsSelector.tscn");
			_colorSelector = selectorScene.Instantiate<Window>();
			AddChild(_colorSelector);
			_colorSelector.Connect("color_selected", Callable.From<Color>(OnPresetColorSelected));
		}
		_colorSelector.PopupCentered();
	}

	private void OnPresetColorSelected(Color color)
	{
		_selectedColor = color;
		_colorCustomized = true;
		_colorSwatch.Color = Opaque(color);
	}

	private void ApplyDefaultColor(int category)
	{
		_selectedColor = DefaultColorForCategory(category);
		_configuredAlpha = 0xff;
		_colorSwatch.Color = _selectedColor;
	}

	/// <summary>Returns the tag-inspired default color used for a new track in a GM family.</summary>
	public static Color DefaultColorForProgram(int program) => DefaultColorForCategory(CategoryForProgram(Math.Clamp(program, 0, 127)));

	public static Color DefaultColorForCategory(int category)
	{
		Color[] colors =
		[
			new Color("e7b84b"), // Piano
			new Color("e7834c"), // Chromatic percussion
			new Color("c95e79"), // Organ
			new Color("d95e59"), // Guitar
			new Color("55b879"), // Bass
			new Color("4ba7a0"), // Strings
			new Color("568cca"), // Ensemble and choir
			new Color("3979bd"), // Brass
			new Color("8272c8"), // Reeds
			new Color("a76bc0"), // Pipe
			new Color("c363a5"), // Synth lead
			new Color("bc7088"), // Synth pad
			new Color("a97866"), // Synth effects
			new Color("778f61"), // Ethnic
			new Color("8c877d"), // Percussive effects
			new Color("b06f53"), // Sound effects
			new Color("efa348")  // Percussion kit
		];
		return colors[Math.Clamp(category, 0, colors.Length - 1)];
	}

	private void EnsureReady()
	{
		if (_instrumentPicker is null) throw new InvalidOperationException("TrackSettingsDialog must be in the scene tree before it can be configured.");
	}

	private static TrackKind KindForProgram(int program) => program switch
	{
		>= 24 and <= 31 => TrackKind.Guitar,
		>= 32 and <= 39 => TrackKind.Bass,
		<= 23 => TrackKind.Keyboard,
		_ => TrackKind.Other
	};

	private static int CategoryForProgram(int program) => program / 8;
	public static string DefaultImageIdForProgram(int program) => program switch
	{
		< 0 or > 127 => "placeholder",
		< 8 => "piano",
		8 => "piano",
		9 or 11 or 12 or 13 => "xylophone",
		< 20 => "piano",
		21 or 23 => "accordion",
		< 24 => "piano",
		24 or 25 => "acoustic-guitar",
		< 32 => "guitar",
		< 40 => "e-bass",
		40 or 41 or 48 or 49 => "violin",
		42 or 43 => "cello",
		46 => "harp",
		56 or 59 => "trumpet",
		57 => "trombone",
		60 => "french-horn",
		>= 64 and <= 67 => "saxophone",
		71 => "clarinet",
		72 or 73 => "flute",
		105 => "banjo",
		_ => "placeholder"
	};
	private static string DefaultName(int program) => GeneralMidiInstruments.GetName(program);

	private static string FormatTuning(IEnumerable<int> pitches) => string.Join(", ", pitches.Select(NotePitchSelector.FormatPitch));
	private static Color Opaque(Color color) => new(color.R, color.G, color.B, 1f);
	private static uint PackColor(Color color, uint alpha) =>
		((uint)Math.Clamp((int)MathF.Round(color.R * 255f), 0, 255) << 24) |
		((uint)Math.Clamp((int)MathF.Round(color.G * 255f), 0, 255) << 16) |
		((uint)Math.Clamp((int)MathF.Round(color.B * 255f), 0, 255) << 8) | (alpha & 0xff);
	private static Color Unpack(uint rgba) => new(((rgba >> 24) & 0xff) / 255f, ((rgba >> 16) & 0xff) / 255f, ((rgba >> 8) & 0xff) / 255f, (rgba & 0xff) / 255f);
}
