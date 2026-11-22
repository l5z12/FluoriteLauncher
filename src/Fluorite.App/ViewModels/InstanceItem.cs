using CommunityToolkit.Mvvm.ComponentModel;
using Fluorite.App.Services;
using Fluorite.Core.Instances;

namespace Fluorite.App.ViewModels;

/// <summary>Display wrapper for an instance in lists.</summary>
public sealed partial class InstanceItem(GameInstance instance) : ObservableObject
{
    public GameInstance Instance { get; } = instance;
    public string Name => Instance.Name;

    /// <summary>What Narrator reads for this instance's Play button ("Play Survival").</summary>
    public string PlayName => Loc.Format("Palette_Play", Instance.Name);

    public string VersionText => DescribeVersion(Instance.Manifest, Instance.LaunchVersionId);

    /// <summary>The list view's second line: the version, then which launcher's folder it's in.</summary>
    public string DetailText => IsExternal ? $"{VersionText}  ·  {SourceLabel}" : VersionText;

    /// <summary>"1.20.1 · Fabric 0.16.10 + OptiFine HD_U_I6".</summary>
    public static string DescribeVersion(InstanceManifest manifest, string? fallback = null)
    {
        var loader = ComponentResolver.PrimaryLoader(manifest);
        var mc = manifest.MinecraftVersion ?? fallback ?? "?";
        var text = loader is null ? mc : $"{mc} · {LoaderName(loader.Uid)} {loader.Version}";
        if (ComponentResolver.OptiFineAsMod(manifest) is { } of)
            text += $" + OptiFine {of.Version}";
        return text;
    }

    public static string LoaderName(string uid) => uid switch
    {
        ComponentUids.FabricLoader => "Fabric",
        ComponentUids.QuiltLoader => "Quilt",
        ComponentUids.Forge => "Forge",
        ComponentUids.NeoForge => "NeoForge",
        ComponentUids.LegacyFabric => "Legacy Fabric",
        ComponentUids.LiteLoader => "LiteLoader",
        ComponentUids.OptiFine => "OptiFine",
        _ => uid,
    };

    /// <summary>"Last played 2 hours ago"; the exact time is in <see cref="LastPlayedTooltip"/>.</summary>
    public string LastPlayedText => Instance.Manifest.Stats.LastPlayed is { } t
        ? Loc.Format("Instance_LastPlayed", Formats.Ago(t))
        : Loc.Get("Instance_NeverPlayed");

    public string? LastPlayedTooltip => Instance.Manifest.Stats.LastPlayed is { } t ? Formats.When(t) : null;

    /// <summary>"3 h 12 min played"; empty under a minute.</summary>
    public string PlayTimeText => Formats.Duration(TimeSpan.FromSeconds(Instance.Manifest.Stats.TotalPlaySeconds)) is { Length: > 0 } d
        ? Loc.Format("Instance_PlayTime", d)
        : "";

    public string Initial => string.IsNullOrEmpty(Name) ? "?" : Name[..1].ToUpperInvariant();

    /// <summary>"Prism Launcher" etc. for instances used in place from another launcher's folder; empty for Fluorite's.</summary>
    public string SourceLabel => Instance.Launcher is { } l ? Loc.Get("Launcher_" + l) : "";

    public bool IsExternal => Instance.IsExternal;

    /// <summary>The Library group, or null. "" also means none: it's how another launcher's group is removed in Fluorite.</summary>
    public string? Group => string.IsNullOrWhiteSpace(Instance.Manifest.Group) ? null : Instance.Manifest.Group.Trim();

    /// <summary>
    /// The instance's icon: its file (modpack icon, Prism custom icon — which may be SVG…), else the Prism built-in
    /// it names (<see cref="InstanceManifest.IconKey"/>), else none (the initial shows).
    /// </summary>
    public Microsoft.UI.Xaml.Media.ImageSource? Icon => _icon ??=
        Instance.Manifest.Icon is { } path && File.Exists(path) ? LoadIcon(new Uri(path))
        : BuiltInIcon(Instance.Manifest.IconKey) is { } builtIn ? LoadIcon(builtIn)
        : null;
    private Microsoft.UI.Xaml.Media.ImageSource? _icon;

    /// <summary>When false, lists show the brand-coloured tile with <see cref="Initial"/> instead (icons may be transparent).</summary>
    public bool HasIcon => Icon is not null;

    /// <summary>
    /// The instance page's tab when it was left for another page (a mod's project page, say), so Back returns to it:
    /// the page is made anew each visit. The back stack keeps this item as the page's parameter.
    /// </summary>
    public string? ReturnTab { get; set; }

    /// <summary>After the icon changed (Change icon…).</summary>
    public void RefreshIcon()
    {
        _icon = null;
        OnPropertyChanged(nameof(Icon));
        OnPropertyChanged(nameof(HasIcon));
    }

    public static Microsoft.UI.Xaml.Media.ImageSource LoadIcon(Uri uri) => uri.AbsolutePath.EndsWith(".svg", StringComparison.OrdinalIgnoreCase)
        ? new Microsoft.UI.Xaml.Media.Imaging.SvgImageSource(uri) { RasterizePixelWidth = 96, RasterizePixelHeight = 96 }
        : new Microsoft.UI.Xaml.Media.Imaging.BitmapImage(uri) { DecodePixelWidth = 96, DecodePixelType = Microsoft.UI.Xaml.Media.Imaging.DecodePixelType.Logical };

    /// <summary>The built-in icons' names: the current set A–Z, then MultiMC's older "_legacy" ones.</summary>
    public static IReadOnlyList<string> BuiltInIcons =>
        s_builtIn.Value.Keys.OrderBy(k => k.EndsWith("_legacy", StringComparison.Ordinal)).ThenBy(k => k, StringComparer.Ordinal).ToList();

    /// <summary>Prism's built-in instance icons, shipped in Assets/InstanceIcons (GPL-3.0, see THIRD-PARTY-NOTICES.md).</summary>
    private static readonly Lazy<Dictionary<string, string>> s_builtIn = new(() =>
    {
        var dir = Path.Combine(AppContext.BaseDirectory, "Assets", "InstanceIcons");
        try
        {
            return Directory.Exists(dir)
                ? Directory.EnumerateFiles(dir).ToDictionary(f => Path.GetFileNameWithoutExtension(f), f => Path.GetFileName(f), StringComparer.Ordinal)
                : [];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    });

    public static Uri? BuiltInIcon(string? key) =>
        key is not null && s_builtIn.Value.TryGetValue(key, out var file) ? new Uri("ms-appx:///Assets/InstanceIcons/" + file) : null;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PlayLabel))]
    public partial bool IsRunning { get; set; }

    /// <summary>The Play button reads "Running" while it's disabled for that reason.</summary>
    public string PlayLabel => IsRunning ? Loc.Get("Launch_Running") : Loc.Get("Common_Play/Text");

    /// <summary>Picked in the Library (Ctrl+click, Shift+click, Ctrl+A) for a group move or delete.</summary>
    [ObservableProperty] public partial bool IsSelected { get; set; }

    /// <summary>Mod updates found (in the background, or by the Mods tab): the card's badge.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasUpdates), nameof(UpdatesText))]
    public partial int UpdateCount { get; set; }

    public bool HasUpdates => UpdateCount > 0;

    /// <summary>"3 updates".</summary>
    public string UpdatesText => UpdateCount == 1 ? Loc.Get("Library_UpdateOne") : Loc.Format("Library_Updates", UpdateCount);

    public void Refresh()
    {
        OnPropertyChanged(nameof(Name));
        OnPropertyChanged(nameof(VersionText));
        OnPropertyChanged(nameof(LastPlayedText));
        OnPropertyChanged(nameof(LastPlayedTooltip));
        OnPropertyChanged(nameof(PlayTimeText));
    }
}
