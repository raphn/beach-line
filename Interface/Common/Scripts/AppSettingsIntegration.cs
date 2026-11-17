using Godot;

public partial class DrumScorePresenter
{
    private AppSettingsDialog _settingsDialog;
    private PopupMenu _settingsMenu;

    private void InitializeAppSettings()
    {
        var settingsScene = GD.Load<PackedScene>("res://Interface/AppSettings/AppSettingsDialog.tscn");
        _settingsDialog = settingsScene.Instantiate<AppSettingsDialog>();
        _settingsDialog.Visible = false;
        AddChild(_settingsDialog);
        var menuBar = GetNode<MenuBar>("%MenuBar");
        var menu = _settingsMenu = new PopupMenu { Name = "Settings" };
        menuBar.AddChild(menu);
        menu.AddItem("Preferences…");
        menu.IdPressed += _ => _settingsDialog.PopupCentered();
        RefreshSettingsMenuTranslation();
    }
    private void RefreshSettingsMenuTranslation()
    {
        if (_settingsMenu is null) return;
        _settingsMenu.Name = Tr("Settings");
        _settingsMenu.SetItemText(0, Tr("Preferences…"));
    }
}

