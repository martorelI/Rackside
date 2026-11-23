using Rackside.App.Ui;
using Rackside.Core.Settings;
using static Rackside.App.Ui.Text;

namespace Rackside.App.Pages;

/// <summary>Settings: the language (applied on restart); everything else is in settings.json.</summary>
internal sealed class SettingsPage : Page
{
    public SettingsPage(MainForm shell) : base(shell, S("Settings"), S("Language, data folders and the settings file."))
    {
        var list = new CardList();
        var lang = list.Add(new Card(S("Language"), S("Applied when the application restarts.")));
        var combo = Make.Combo(["English", "Español"], App.Settings.Language == "es" ? 1 : 0, 200);
        var save = Make.Primary(S("Save and restart"), (_, _) =>
        {
            var code = combo.SelectedIndex == 1 ? "es" : "en";
            try
            {
                AppSettings.SaveLanguage(App.SettingsPath, code);
            }
            catch (Exception ex) when (ex is FormatException or IOException or UnauthorizedAccessException)
            {
                Dialogs.Error(FindForm(), F("{0} is not valid JSON: fix it or delete it so it is created again.", App.SettingsPath) + "\r\n" + ex.Message);
                return;
            }
            Application.Restart();
        });
        save.Margin = new Padding(10, 0, 0, 0);
        lang.Add(Make.Row(combo, save));

        var file = list.Add(new Card(S("Settings file"), S("Data folder and subfolders, preferred COM port, push defaults (banners, time zone, VTY idle timeout) and extra commands allowed in the restore. Edit it with the application closed.")));
        file.Add(Make.Note(App.SettingsPath, Theme.TextColor));
        file.Add(Make.Row(
            Make.Secondary(S("Open settings.json"), (_, _) => OpenPath(App.SettingsPath)),
            Make.Secondary(S("Open the data folder"), (_, _) => { Directory.CreateDirectory(App.Paths.Data); OpenPath(App.Paths.Data); })));

        var paths = list.Add(new Card(S("Folders in use")));
        var grid = new Fields();
        var p = App.Paths;
        foreach (var (k, v) in new[] { ("Data", p.Data), ("Restores", p.Restores), ("FactoryResets", p.FactoryResets), ("Upgrades", p.Upgrades), ("Backups", p.Backups), ("SSHFingerprints", p.Fingerprints), ("Inventory", p.InventoryCsv), ("Pushes", p.Pushes) })
        {
            grid.Add(k, Make.Label(v));
        }
        paths.Add(grid);

        if (App.Settings.Warnings.Count > 0)
        {
            var warn = list.Add(new Card(S("Problems in settings.json"), S("The default value is used for each one.")));
            foreach (var (template, args) in App.Settings.Warnings)
            {
                warn.Add(Make.Note("• " + F(template, args), Theme.Danger));
            }
        }
        SetBody(list);
    }
}
