#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Godot;

/// <summary>Application-wide preferences, stored separately from score and workspace data.</summary>
public static class AppPreferences
{
    public static readonly (string Action, string Label)[] Actions =
    [
        ("new_score", "New score"), ("saved", "Save score"), ("undo_score", "Undo"),
        ("redo_score", "Redo"), ("copy_notes", "Copy notes"), ("paste_notes", "Paste notes"),
        ("play", "Play / stop"), ("play_from_start", "Play from start"),
        ("repeat_score", "Repeat full score"), ("repeat_measure", "Repeat selected measure"),
        ("repeat_sections", "Repeat selected sections")
    ];
    private const string Path = "user://app_settings.json";
    private static readonly Dictionary<string, InputEventKey> Defaults = new()
    {
        ["new_score"] = KeyEvent(Key.N, ctrl: true), ["saved"] = KeyEvent(Key.S, ctrl: true),
        ["undo_score"] = KeyEvent(Key.Z, ctrl: true), ["redo_score"] = KeyEvent(Key.Z, ctrl: true, shift: true),
        ["copy_notes"] = KeyEvent(Key.C, ctrl: true), ["paste_notes"] = KeyEvent(Key.V, ctrl: true),
        ["play"] = KeyEvent(Key.Space), ["play_from_start"] = KeyEvent(Key.Space, ctrl: true),
        ["repeat_score"] = KeyEvent(Key.Q), ["repeat_measure"] = KeyEvent(Key.W),
        ["repeat_sections"] = KeyEvent(Key.E)
    };
    private static readonly Dictionary<string, InputEventKey> Bindings = new();
    public static string Locale { get; private set; } = "en";
    public static event Action? LocaleChanged;
    public static event Action? BindingsChanged;

    private sealed class SavedSettings
    {
        public string Language { get; set; } = "en";
        public Dictionary<string, SavedKey> Shortcuts { get; set; } = new();
    }
    private sealed class SavedKey
    {
        public long Keycode { get; set; }
        public bool Ctrl { get; set; }
        public bool Shift { get; set; }
        public bool Alt { get; set; }
        public bool Meta { get; set; }
    }
    private static InputEventKey KeyEvent(Key key, bool ctrl = false, bool shift = false, bool alt = false, bool meta = false) =>
        new() { Keycode = key, CtrlPressed = ctrl, ShiftPressed = shift, AltPressed = alt, MetaPressed = meta };
    private static InputEventKey Copy(InputEventKey e) => KeyEvent(e.Keycode, e.CtrlPressed, e.ShiftPressed, e.AltPressed, e.MetaPressed);
    private static bool Same(InputEventKey a, InputEventKey b) => a.Keycode == b.Keycode && a.CtrlPressed == b.CtrlPressed &&
        a.ShiftPressed == b.ShiftPressed && a.AltPressed == b.AltPressed && a.MetaPressed == b.MetaPressed;
    private static bool Valid(InputEventKey e) => e.Keycode != Key.None && e.Keycode != Key.Escape &&
        e.Keycode != Key.Ctrl && e.Keycode != Key.Shift && e.Keycode != Key.Alt && e.Keycode != Key.Meta;

    public static void Initialize()
    {
        AppTranslations.Register();
        foreach (var (action, _) in Actions) Bindings[action] = Copy(Defaults[action]);
        try
        {
            var path = ProjectSettings.GlobalizePath(Path);
            if (File.Exists(path))
            {
                var saved = JsonSerializer.Deserialize<SavedSettings>(File.ReadAllText(path));
                if (saved != null)
                {
                    Locale = saved.Language == "pt_BR" ? "pt_BR" : "en";
                    // Preserve valid saved bindings first. Newly added actions must not make an older
                    // user's otherwise valid shortcut set fail wholesale on a default-key collision.
                    var candidates = new Dictionary<string, InputEventKey>(Bindings);
                    var storedActions = new HashSet<string>();
                    foreach (var (action, _) in Actions)
                        if (saved.Shortcuts != null && saved.Shortcuts.TryGetValue(action, out var key) && key != null)
                        {
                            candidates[action] = KeyEvent((Key)key.Keycode, key.Ctrl, key.Shift, key.Alt, key.Meta);
                            storedActions.Add(action);
                        }

                    var storedBindings = storedActions.Select(action => candidates[action]).ToArray();
                    if (storedBindings.All(Valid) && !HasDuplicateBindings(storedActions.Select(action => candidates[action])))
                    {
                        foreach (var (action, _) in Actions)
                        {
                            if (storedActions.Contains(action)) continue;
                            if (!candidates.Where(item => item.Key != action).Any(item => Same(item.Value, candidates[action]))) continue;
                            candidates[action] = FindAvailableBinding(action, candidates);
                        }
                    }

                    if (candidates.Values.All(Valid) && !HasDuplicateBindings(candidates.Values))
                        foreach (var item in candidates) Bindings[item.Key] = item.Value;
                }
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException or ArgumentException)
        { GD.PushWarning(string.Format(TranslationServer.Translate("Could not load application settings: {0}"), e.Message)); }
        TranslationServer.SetLocale(Locale);
        ApplyBindings();
        LocaleChanged?.Invoke();
        BindingsChanged?.Invoke();
    }
    public static InputEventKey GetBinding(string action) => Copy(Bindings.TryGetValue(action, out var binding) ? binding : Defaults[action]);

    private static bool HasDuplicateBindings(IEnumerable<InputEventKey> bindings)
    {
        var seen = new List<InputEventKey>();
        foreach (var binding in bindings)
        {
            if (seen.Any(existing => Same(existing, binding))) return true;
            seen.Add(binding);
        }
        return false;
    }

    private static InputEventKey FindAvailableBinding(string action, IReadOnlyDictionary<string, InputEventKey> candidates)
    {
        var preferred = Defaults[action];
        var alternatives = new List<InputEventKey>
        {
            KeyEvent(preferred.Keycode, ctrl: true, shift: true),
            KeyEvent(preferred.Keycode, alt: true, shift: true),
            KeyEvent(preferred.Keycode, ctrl: true, alt: true),
            KeyEvent(preferred.Keycode, meta: true, shift: true)
        };
        for (var key = Key.F1; key <= Key.F12; key++)
        {
            alternatives.Add(KeyEvent(key));
            alternatives.Add(KeyEvent(key, ctrl: true, shift: true));
        }
        return alternatives.FirstOrDefault(alternative => candidates
            .Where(item => item.Key != action).All(item => !Same(item.Value, alternative))) ?? preferred;
    }

    /// <summary>Formats a configured shortcut for user-facing help text.</summary>
    public static string FormatBinding(string action)
    {
        var key = GetBinding(action);
        var parts = new List<string>();
        if (key.CtrlPressed) parts.Add("Ctrl");
        if (key.AltPressed) parts.Add("Alt");
        if (key.ShiftPressed) parts.Add("Shift");
        if (key.MetaPressed) parts.Add("Meta");
        parts.Add(key.Keycode == Key.None ? key.PhysicalKeycode.ToString() : key.Keycode.ToString());
        return string.Join("+", parts);
    }
    public static void SetLocale(string locale)
    {
        Locale = locale == "pt_BR" ? "pt_BR" : "en";
        TranslationServer.SetLocale(Locale);
        Save();
        LocaleChanged?.Invoke();
    }
    public static bool TrySetBinding(string action, InputEventKey key, out string error)
    {
        if (!Bindings.ContainsKey(action) || !Valid(key)) { error = "Choose a key other than Escape or a modifier."; return false; }
        var conflict = Actions.FirstOrDefault(item => item.Action != action && Same(Bindings[item.Action], key));
        if (conflict.Action != null) { error = $"Already assigned to: {conflict.Label}"; return false; }
        Bindings[action] = Copy(key);
        ApplyBindings(); Save(); BindingsChanged?.Invoke(); error = ""; return true;
    }
    public static void ResetBindings()
    {
        foreach (var (action, _) in Actions) Bindings[action] = Copy(Defaults[action]);
        ApplyBindings(); Save(); BindingsChanged?.Invoke();
    }
    private static void ApplyBindings()
    {
        foreach (var (action, _) in Actions)
        {
            if (!InputMap.HasAction(action)) InputMap.AddAction(action);
            InputMap.ActionEraseEvents(action); InputMap.ActionAddEvent(action, Copy(Bindings[action]));
        }
        if (!InputMap.HasAction("stop")) InputMap.AddAction("stop");
        InputMap.ActionEraseEvents("stop"); InputMap.ActionAddEvent("stop", Copy(Bindings["play"]));
    }
    private static void Save()
    {
        try
        {
            var settings = new SavedSettings { Language = Locale };
            foreach (var (action, key) in Bindings)
                settings.Shortcuts[action] = new SavedKey { Keycode = (long)key.Keycode, Ctrl = key.CtrlPressed, Shift = key.ShiftPressed, Alt = key.AltPressed, Meta = key.MetaPressed };
            File.WriteAllText(ProjectSettings.GlobalizePath(Path), JsonSerializer.Serialize(settings));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        { GD.PushWarning(string.Format(TranslationServer.Translate("Could not save application settings: {0}"), e.Message)); }
    }
}
