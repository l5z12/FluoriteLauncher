using CommunityToolkit.Mvvm.ComponentModel;
using Fluorite.App.Services;
using Fluorite.Core.Text;
using Fluorite.Modding.Metadata;

namespace Fluorite.App.ViewModels;

/// <summary>
/// Size on disk and file count of a file or everything in a folder, and how many of them are turned off
/// (<c>.disabled</c>) and their size, which most formats leave out.
/// </summary>
public readonly record struct ExportTally(long Bytes, int Files, int Disabled, long DisabledBytes)
{
    public static ExportTally operator +(ExportTally a, ExportTally b) =>
        new(a.Bytes + b.Bytes, a.Files + b.Files, a.Disabled + b.Disabled, a.DisabledBytes + b.DisabledBytes);
}

/// <summary>
/// A file or folder of the game directory in the Export dialog's tree: ticked, unticked, or (a folder) partly. A
/// folder's own files and folders are read when it's first opened.
/// </summary>
public sealed partial class ExportNode(string name, string path, bool isDirectory, int depth, ExportNode? parent, bool defaultIncluded) : ObservableObject
{
    public string Name { get; } = name;

    /// <summary>Relative to the game directory, with <c>/</c>.</summary>
    public string Path { get; } = path;

    public bool IsDirectory { get; } = isDirectory;
    public int Depth { get; } = depth;
    public ExportNode? Parent { get; } = parent;

    /// <summary>Ticked unless the user says otherwise (worlds and personal settings aren't).</summary>
    public bool DefaultIncluded { get; } = defaultIncluded;

    /// <summary>Null until the folder has been read (or for a file).</summary>
    public List<ExportNode>? Children { get; set; }

    /// <summary>Ticked (true), not (false), or a folder with only some of it ticked (null).</summary>
    [ObservableProperty]
    public partial bool? State { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Chevron), nameof(ExpandName))]
    public partial bool IsExpanded { get; set; }

    /// <summary>Null while it's still being measured.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SizeText), nameof(SizeTooltip))]
    public partial ExportTally? Tally { get; set; }

    /// <summary>"mods/" for a folder. The name as the content tabs show it (<see cref="Readable"/>).</summary>
    public string DisplayName => IsDirectory ? Readable(Name) + "/" : Readable(Name);

    /// <summary>
    /// A file name as the content tabs read it: repaired when another launcher garbled its Chinese (UTF-8 read as
    /// Windows-1252: "é›¶é›¾" → "零雾"), without the § codes packs put in for the game's list; the name as it is when
    /// that leaves nothing but the extension. The file keeps its name, in the folder and in the pack.
    /// </summary>
    public static string Readable(string name) =>
        ContentMetadataReader.PlainText(Mojibake.Repair(name)).Trim() is var readable && readable.Length > System.IO.Path.GetExtension(readable).Length
            ? readable
            : name;

    /// <summary>What a well-known top-level entry is ("Worlds", "Your game settings"); empty for the rest.</summary>
    public string Description => Depth == 0 ? Describe(Name) : "";

    public double IndentWidth => Depth * 22;

    public string Chevron => IsExpanded ? "" : "";

    /// <summary>The chevron's name for Narrator: "Expand config/" or "Collapse config/".</summary>
    public string ExpandName => Loc.Format(IsExpanded ? "Export_Collapse" : "Export_Expand", DisplayName);

    public string SizeText => Tally is { } t ? Formats.Size(t.Bytes) : "…";

    /// <summary>"142 files" for a folder.</summary>
    public string? SizeTooltip => Tally is { } t && IsDirectory ? Loc.Format("Export_FileCount", Formats.Count(t.Files)) : null;

    private static string Describe(string name) => name.ToLowerInvariant() switch
    {
        "config" => Loc.Get("Export_DescConfig"),
        "defaultconfigs" => Loc.Get("Export_DescDefaultConfigs"),
        "saves" => Loc.Get("Export_DescSaves"),
        "options.txt" => Loc.Get("Export_DescOptions"),
        "optionsof.txt" => Loc.Get("Export_DescOptiFine"),
        "optionsshaders.txt" => Loc.Get("Export_DescShaderOptions"),
        "servers.dat" or "servers.dat_old" => Loc.Get("Export_DescServers"),
        "kubejs" => Loc.Get("Export_DescKubeJs"),
        "resourcepacks" => Loc.Get("Export_DescResourcePacks"),
        "shaderpacks" => Loc.Get("Export_DescShaderPacks"),
        "journeymap" or "xaeroworldmap" or "xaerowaypoints" => Loc.Get("Export_DescMaps"),
        "replay_recordings" => Loc.Get("Export_DescReplays"),
        "schematics" => Loc.Get("Export_DescSchematics"),
        _ => "",
    };
}
