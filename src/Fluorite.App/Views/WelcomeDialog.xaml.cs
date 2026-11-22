using Fluorite.App.Services;
using Fluorite.Core.Instances;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Fluorite.App.Views;

/// <summary>A launcher found on this PC; folders that can be used in place get a "show in Library" choice.</summary>
public sealed class DetectedItem(DetectedLauncher detected, string name, string detail)
{
    public DetectedLauncher Detected { get; } = detected;
    public string Name { get; } = name;
    public string Path => Detected.Path;
    public string Detail { get; } = detail;

    /// <summary>The card's description: what's there, then where.</summary>
    public string Summary => Detail + "\n" + Path;
    public bool CanUseInPlace => Detected.Layout is not null;

    /// <summary>Preselected when the folder has instances (reading them changes nothing in that folder).</summary>
    public bool UseInPlace { get; set; } = detected.Layout is not null && detected.InstanceCount > 0;
}

/// <summary>First-run welcome: where data lives and which other launchers were found on this PC.</summary>
public sealed partial class WelcomeDialog : ContentDialog
{
    private readonly List<DetectedItem> _detected;

    public WelcomeDialog() : this(again: false)
    {
    }

    /// <param name="again">Shown again after the first run (<c>--welcome</c>): nothing is preselected, so clicking
    /// through doesn't bring back a folder that was removed since.</param>
    public WelcomeDialog(bool again)
    {
        InitializeComponent();
        DataFolderText.Text = AppPaths.DataRoot;

        // The language on screen now: this run's --lang, else the setting.
        _shownLanguage = StartupFlags.Current.Language ?? App.Get<SettingsService>().Current.Language;
        var index = ViewModels.SettingsViewModel.Languages.ToList().FindIndex(l => string.Equals(l, _shownLanguage, StringComparison.OrdinalIgnoreCase));
        _loading = true;
        LanguageBox.SelectedIndex = Math.Max(0, index);
        _loading = false;

        var launchers = App.Get<OtherLaunchersService>();
        _detected = LauncherDetection.Detect()
            .Where(d => !launchers.IsAdded(d.Path))
            .Select(d => new DetectedItem(d,
                Loc.Get("Launcher_" + d.Launcher),
                d.Layout is LayoutKind.Official or LayoutKind.Isolated
                    ? Loc.Format("Detected_Versions", d.InstanceCount)
                    : Loc.Format("Detected_Instances", d.InstanceCount)))
            .ToList();
        if (again)
        {
            foreach (var d in _detected)
                d.UseInPlace = false;
        }
        DetectedList.ItemsSource = new System.Collections.ObjectModel.ObservableCollection<DetectedItem>(_detected); // AOT: a concrete list type.
        DetectedPanel.Visibility = _detected.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private readonly string? _shownLanguage;
    private bool _loading;

    /// <summary>A language was picked: it applies at startup, so the caller restarts Fluorite (back to this screen).</summary>
    public bool RestartForLanguage { get; private set; }

    private void LanguageBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || LanguageBox.SelectedIndex < 0)
            return;
        var language = ViewModels.SettingsViewModel.Languages[LanguageBox.SelectedIndex];
        if (string.Equals(language, _shownLanguage, StringComparison.OrdinalIgnoreCase))
            return;
        var settings = App.Get<SettingsService>();
        settings.Current.Language = language;
        settings.Save();
        RestartForLanguage = true;
        Hide();
    }

    /// <summary>Adds the folders the user chose to keep showing in the Library.</summary>
    public void ApplyChoices()
    {
        var launchers = App.Get<OtherLaunchersService>();
        foreach (var d in _detected.Where(d => d.CanUseInPlace && d.UseInPlace && !launchers.IsAdded(d.Path)))
            launchers.Add(d.Detected.Layout!.Value, d.Detected.Launcher, d.Path);
    }
}
