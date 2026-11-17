#nullable enable
using Godot;
using System;
using System.Collections.Generic;

/// <summary>Reusable application settings window for language and keyboard shortcuts.</summary>
public partial class AppSettingsDialog : Window
{
	private TabContainer _tabs = null!;
	private OptionButton _locale = null!;
	private Label _languageLabel = null!;
	private Label _languageHelp = null!;
	private VBoxContainer _shortcuts = null!;
	private Label _feedback = null!;
	private Button _reset = null!;
	private readonly Dictionary<string, Button> _bindingButtons = new(StringComparer.Ordinal);
	private string? _capturingAction;
	private string _feedbackKey = "";
	private bool _refreshingLocale;
	private PackedScene _shortcutRowScene = null!;

	public override void _Ready()
	{
		_tabs = GetNode<TabContainer>("%SettingsTabs");
		_locale = GetNode<OptionButton>("%Locale");
		_languageLabel = GetNode<Label>("%LanguageLabel");
		_languageHelp = GetNode<Label>("%LanguageHelp");
		_shortcuts = GetNode<VBoxContainer>("%ShortcutsList");
		_feedback = GetNode<Label>("%ShortcutFeedback");
		_reset = GetNode<Button>("%ResetShortcuts");
		_shortcutRowScene = GD.Load<PackedScene>("res://Interface/AppSettings/ShortcutRow.tscn");
		_locale.ItemSelected += OnLocaleSelected;
		_reset.Pressed += OnResetPressed;
		RefreshAll();
		CloseRequested += HideSafely;
		VisibilityChanged += OnVisibilityChanged;
	}

	private void RefreshAll()
	{
		if (_locale is null) return;
		Title = Tr("Settings");
		_tabs.SetTabTitle(0, Tr("Language"));
		_tabs.SetTabTitle(1, Tr("Shortcuts"));
		_locale.SetItemText(0, Tr("English"));
		_locale.SetItemText(1, Tr("Português (Brasil)"));
		_languageLabel.Text = Tr("Interface language");
		_languageHelp.Text = Tr("Language changes apply immediately.");
		_refreshingLocale = true;
		_locale.Select(AppPreferences.Locale.StartsWith("pt", StringComparison.OrdinalIgnoreCase) ? 1 : 0);
		_refreshingLocale = false;
		_reset.Text = Tr("Reset shortcuts");
		_feedback.Text = _feedbackKey.Length > 0 ? TranslateFeedback(_feedbackKey) : _capturingAction is null ? Tr("Select an action, then press one key.") : Tr("Press a key for this shortcut. Escape cancels.");
		foreach (var pair in _bindingButtons)
			pair.Value.Text = _capturingAction == pair.Key ? Tr("Press a key…") : FormatBinding(AppPreferences.GetBinding(pair.Key));
	}

	private void RefreshShortcuts()
	{
		foreach (var child in _shortcuts.GetChildren()) child.QueueFree();
		_bindingButtons.Clear();
		foreach (var (action, label) in AppPreferences.Actions)
		{
			var row = _shortcutRowScene.Instantiate<Control>();
			_shortcuts.AddChild(row);
			row.GetNode<Label>("%ActionLabel").Text = Tr(label);
			var button = row.GetNode<Button>("%BindingButton");
			button.Pressed += () => BeginCapture(action);
			_bindingButtons[action] = button;
		}
		RefreshAll();
	}

	private void BeginCapture(string action)
	{
		_capturingAction = action;
		_feedbackKey = "";
		_feedback.Text = Tr("Press a key for this shortcut. Escape cancels.");
		RefreshAll();
	}

	public override void _Input(InputEvent inputEvent)
	{
		if (!Visible || _capturingAction is null || inputEvent is not InputEventKey key || !key.Pressed || key.Echo) return;
		GetViewport().SetInputAsHandled();
		if (key.Keycode == Key.Escape && !key.CtrlPressed && !key.AltPressed && !key.ShiftPressed && !key.MetaPressed)
		{
			_capturingAction = null;
			_feedbackKey = "Shortcut change cancelled.";
			RefreshAll();
			return;
		}
		if (key.Keycode is Key.Ctrl or Key.Alt or Key.Shift or Key.Meta)
			return;

		var binding = new InputEventKey
		{
			Keycode = key.Keycode,
			PhysicalKeycode = key.PhysicalKeycode,
			CtrlPressed = key.CtrlPressed,
			AltPressed = key.AltPressed,
			ShiftPressed = key.ShiftPressed,
			MetaPressed = key.MetaPressed,
		};
		var action = _capturingAction;
		_capturingAction = null;
		if (!AppPreferences.TrySetBinding(action, binding, out var error))
		{
			_feedbackKey = string.IsNullOrWhiteSpace(error) ? "That key is already assigned." : error;
			_feedback.Text = _feedbackKey;
			RefreshAll();
			return;
		}
		_feedbackKey = "Shortcut saved.";
		_feedback.Text = Tr(_feedbackKey);
		RefreshAll();
	}

	private void OnLocaleSelected(long index)
	{
		if (_refreshingLocale) return;
		AppPreferences.SetLocale(index == 1 ? "pt_BR" : "en");
		RefreshShortcuts();
	}

	private void OnResetPressed()
	{
		AppPreferences.ResetBindings();
		_capturingAction = null;
		_feedbackKey = "Default shortcuts restored.";
		RefreshShortcuts();
	}

	public override void _Notification(int what)
	{
		if (what == NotificationTranslationChanged && IsInsideTree()) RefreshAll();
	}

	private void OnVisibilityChanged()
	{
		if (!Visible) _capturingAction = null;
		else RefreshShortcuts();
	}

	private static string TranslateFeedback(string message)
	{
		const string assignedPrefix = "Already assigned to: ";
		if (message.StartsWith(assignedPrefix, StringComparison.Ordinal))
			return string.Format(TranslationServer.Translate("Already assigned to: {0}"), TranslationServer.Translate(message[assignedPrefix.Length..]));
		return TranslationServer.Translate(message);
	}

	private void HideSafely()
	{
		_capturingAction = null;
		Hide();
	}

	private string FormatBinding(InputEventKey? key)
	{
		if (key is null) return Tr("Unassigned");
		var parts = new List<string>();
		if (key.CtrlPressed) parts.Add("Ctrl");
		if (key.AltPressed) parts.Add("Alt");
		if (key.ShiftPressed) parts.Add("Shift");
		if (key.MetaPressed) parts.Add("Meta");
		parts.Add(key.Keycode == Key.None ? key.PhysicalKeycode.ToString() : key.Keycode.ToString());
		return string.Join(" + ", parts);
	}
}
