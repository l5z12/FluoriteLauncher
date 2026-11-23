using Fluorite.App.Services;
using Fluorite.App.ViewModels;
using Fluorite.Modding.Metadata;
using Fluorite.Modding.Modpacks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;

namespace Fluorite.App.Views;

/// <summary>The Install modpack dialog's body: the pack, what it runs on, the instance's name and group, optional files, notices.</summary>
public sealed partial class ModpackConfirmPanel : UserControl
{
    private const int IconSize = 64;
    private readonly string _packName;

    /// <param name="groups">The Library's groups, for the group box.</param>
    public ModpackConfirmPanel(PackManifest m, IReadOnlyList<string> groups)
    {
        InitializeComponent();
        _packName = m.Name;

        PackName.Text = m.Name;
        InitialText.Text = string.IsNullOrEmpty(m.Name) ? "?" : m.Name[..1].ToUpperInvariant();
        Byline.Text = string.Join(" · ", new[] { m.Version, string.IsNullOrWhiteSpace(m.Author) ? null : Loc.Format("Browse_ByAuthor", m.Author) }
            .Where(s => !string.IsNullOrWhiteSpace(s)));
        Byline.Visibility = Byline.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        Summary.Text = m.Summary?.Trim() ?? "";
        Summary.Visibility = Summary.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        ToolTipService.SetToolTip(Summary, Summary.Text.Length > 160 ? Summary.Text : null); // Cut at three lines.

        MinecraftText.Text = m.MinecraftVersion;
        if (m.LoaderUid is { } loader)
            LoaderText.Text = $"{InstanceItem.LoaderName(loader)} {m.LoaderVersion}".Trim();
        else
            LoaderFact.Visibility = Visibility.Collapsed;
        var download = m.Files.Count(f => f.Client != PackSide.Unsupported);
        FilesText.Text = m.Overrides.Count > 0
            ? Loc.Format("ModpackConfirm_FilesToDownload", download) + " · " + Loc.Format("ModpackConfirm_FilesInPack", m.Overrides.Count)
            : Loc.Format("ModpackConfirm_FilesToDownload", download);

        NameBox.Text = m.Name;
        NameBox.PlaceholderText = m.Name; // Emptied: the pack's name.
        GroupBox.ItemsSource = new System.Collections.ObjectModel.ObservableCollection<string>(groups); // AOT: a concrete list type.

        var optional = m.Files.Count(f => f.Client == PackSide.Optional);
        if (optional > 0)
        {
            OptionalBox.Content = Loc.Format("Modpack_IncludeOptional", optional);
            OptionalBox.Visibility = Visibility.Visible;
        }
        var blocked = m.Files.Count(f => f.IsBlocked);
        if (blocked > 0)
            Notices.Children.Add(Dialogs.Bar(Loc.Format("Modpack_ManualNeeded", blocked), InfoBarSeverity.Informational));
        if (m.Warnings.Count > 0)
            Notices.Children.Add(Dialogs.Bar(string.Join("\n", m.Warnings.Take(8)), InfoBarSeverity.Warning));
        Notices.Visibility = Notices.Children.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

        _ = LoadIconAsync(m);
    }

    /// <summary>What to call the instance: as typed, or the pack's name when left empty.</summary>
    public string InstanceName => string.IsNullOrWhiteSpace(NameBox.Text) ? _packName : NameBox.Text.Trim();

    /// <summary>The Library group picked or typed; null for none.</summary>
    public string? Group => string.IsNullOrWhiteSpace(GroupBox.Text) ? null : GroupBox.Text.Trim();

    public bool IncludeOptional => OptionalBox.Visibility == Visibility.Collapsed || OptionalBox.IsChecked != false;

    /// <summary>The name, selected: typing replaces it, Enter installs.</summary>
    public void FocusName()
    {
        NameBox.Focus(FocusState.Programmatic);
        NameBox.SelectAll();
    }

    /// <summary>The icon the instance will get: the project's (online), one inside the archive, or a Prism built-in.</summary>
    private async Task LoadIconAsync(PackManifest m)
    {
        if (m.IconUrl is { } url && Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme == "https")
        {
            IconImage.Source = new BitmapImage(uri) { DecodePixelWidth = IconSize, DecodePixelType = DecodePixelType.Logical };
            return;
        }
        if (m.IconEntry is { } entry && m.ArchivePath is { } archive && !entry.EndsWith(".svg", StringComparison.OrdinalIgnoreCase))
        {
            var bytes = await Task.Run(() => ContentMetadataReader.ReadEntry(archive, entry));
            if (bytes is not null && await ImageLoader.FromBytesAsync(bytes, IconSize) is { } decoded)
            {
                IconImage.Source = decoded;
                InitialTile.Visibility = Visibility.Collapsed;
            }
            return;
        }
        if (InstanceItem.BuiltInIcon(m.IconKey) is { } builtIn)
        {
            IconImage.Source = InstanceItem.LoadIcon(builtIn); // Shipped with the app: no need to wait for it to open.
            InitialTile.Visibility = Visibility.Collapsed;
        }
    }

    private void IconImage_ImageOpened(object sender, RoutedEventArgs e) => InitialTile.Visibility = Visibility.Collapsed;
}
