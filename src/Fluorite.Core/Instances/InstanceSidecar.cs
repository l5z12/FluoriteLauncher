using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Fluorite.Core.Instances;

/// <summary>
/// Fluorite's own data about another launcher's instance, stored under Fluorite's data root (never in the other
/// launcher's folder): settings the user changed in Fluorite, play time recorded by Fluorite, and the ids of loader
/// profiles Fluorite installed into its own store for it.
/// </summary>
public sealed class InstanceSidecar
{
    public int FormatVersion { get; set; } = 1;
    public string? Name { get; set; }
    public string? Group { get; set; }
    public string? Notes { get; set; }

    /// <summary>The icon picked in Fluorite: an image file's path, or "" for none (the letter tile). Null: the launcher's.</summary>
    public string? Icon { get; set; }

    /// <summary>A built-in icon picked in Fluorite (<see cref="PrismIcons.BuiltIn"/>); wins over <see cref="Icon"/>.</summary>
    public string? IconKey { get; set; }

    /// <summary>Only the settings that differ from the other launcher's own (null = use theirs).</summary>
    public InstanceSettings Settings { get; set; } = new();

    /// <summary>Play recorded by Fluorite, added on top of what the other launcher recorded.</summary>
    public InstanceStats Stats { get; set; } = new();

#pragma warning disable CA2227 // Serialised.
    /// <summary><c>uid@version</c> → profile id Fluorite installed for it.</summary>
    public Dictionary<string, string> ProfileIds { get; set; } = [];
#pragma warning restore CA2227

    public static string FolderName(string localId)
    {
        var safe = new StringBuilder();
        foreach (var c in localId)
            safe.Append(c < 128 && (char.IsLetterOrDigit(c) || c is '-' or '_' or '.') ? c : '_');
        var hash = Convert.ToHexStringLower(SHA1.HashData(Encoding.UTF8.GetBytes(localId)))[..8];
        var name = safe.ToString().Trim('.', '_');
        return (name.Length > 40 ? name[..40] : name) + "-" + hash;
    }

    public static InstanceSidecar Load(string directory)
    {
        var path = Path.Combine(directory, "sidecar.json");
        try
        {
            return File.Exists(path)
                ? JsonSerializer.Deserialize(File.ReadAllBytes(path), SidecarJsonContext.Default.InstanceSidecar) ?? new InstanceSidecar()
                : new InstanceSidecar();
        }
        catch (JsonException)
        {
            return new InstanceSidecar();
        }
    }

    public async Task SaveAsync(string directory, CancellationToken ct = default)
    {
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "sidecar.json");
        var tmp = path + ".tmp";
        await using (var fs = File.Create(tmp))
            await JsonSerializer.SerializeAsync(fs, this, SidecarJsonContext.Default.InstanceSidecar, ct).ConfigureAwait(false);
        File.Move(tmp, path, overwrite: true);
    }

    /// <summary>Overlays this sidecar on a manifest built from the other launcher's metadata.</summary>
    public void ApplyTo(InstanceManifest manifest)
    {
        manifest.Name = Name ?? manifest.Name;
        manifest.Group = Group ?? manifest.Group;
        manifest.Notes = Notes ?? manifest.Notes;
        if (IconKey is not null)
            (manifest.Icon, manifest.IconKey) = (null, IconKey);
        else if (Icon == "")
            (manifest.Icon, manifest.IconKey) = (null, null);
        else if (Icon is not null && File.Exists(Icon)) // A deleted file leaves the launcher's own icon showing.
            (manifest.Icon, manifest.IconKey) = (Icon, null);
        InstanceSettingsDiff.Overlay(manifest.Settings, Settings);

        var stats = manifest.Stats;
        stats.TotalPlaySeconds += Stats.TotalPlaySeconds;
        stats.LaunchCount += Stats.LaunchCount;
        if (Stats.LastPlayed is { } fl && (stats.LastPlayed is null || fl > stats.LastPlayed))
        {
            stats.LastPlayed = fl;
            stats.LastSessionSeconds = Stats.LastSessionSeconds;
        }

        foreach (var c in manifest.Components)
        {
            if (c.ProfileId is null && ProfileIds.TryGetValue($"{c.Uid}@{c.Version}", out var id))
                c.ProfileId = id;
        }
    }

    /// <summary>Records what <paramref name="effective"/> has on top of <paramref name="native"/> (the other launcher's view).</summary>
    public static InstanceSidecar Diff(InstanceManifest effective, InstanceManifest native, InstanceSidecar previous, bool includeStats = true)
    {
        var sameIcon = string.Equals(effective.Icon, native.Icon, StringComparison.OrdinalIgnoreCase) && effective.IconKey == native.IconKey;
        var s = new InstanceSidecar
        {
            Name = effective.Name != native.Name ? effective.Name : null,
            Group = effective.Group != native.Group ? effective.Group : null,
            Notes = effective.Notes != native.Notes ? effective.Notes : null,
            Icon = sameIcon || effective.IconKey is not null ? null : effective.Icon ?? "",
            IconKey = sameIcon ? null : effective.IconKey,
            Settings = InstanceSettingsDiff.Diff(effective.Settings, native.Settings),
            ProfileIds = new Dictionary<string, string>(previous.ProfileIds),
        };
        foreach (var c in effective.Components)
        {
            if (c.ProfileId is { } id && native.Components.FirstOrDefault(n => n.Uid == c.Uid)?.ProfileId != id)
                s.ProfileIds[$"{c.Uid}@{c.Version}"] = id;
        }
        if (includeStats)
        {
            var e = effective.Stats;
            var n = native.Stats;
            s.Stats = new InstanceStats
            {
                TotalPlaySeconds = Math.Max(0, e.TotalPlaySeconds - n.TotalPlaySeconds),
                LaunchCount = Math.Max(0, e.LaunchCount - n.LaunchCount),
                LastPlayed = e.LastPlayed is { } lp && (n.LastPlayed is null || lp > n.LastPlayed) ? lp : null,
                LastSessionSeconds = e.LastSessionSeconds,
            };
        }
        return s;
    }
}

/// <summary>Property-wise comparison of <see cref="InstanceSettings"/> (explicit, so it stays trim-safe).</summary>
public static class InstanceSettingsDiff
{
    public static void Overlay(InstanceSettings target, InstanceSettings over)
    {
        target.JavaPath = over.JavaPath ?? target.JavaPath;
        target.MaxMemoryMb = over.MaxMemoryMb ?? target.MaxMemoryMb;
        target.MinMemoryMb = over.MinMemoryMb ?? target.MinMemoryMb;
        target.JvmArguments = over.JvmArguments ?? target.JvmArguments;
        target.GameArguments = over.GameArguments ?? target.GameArguments;
        target.Environment = over.Environment ?? target.Environment;
        target.WindowWidth = over.WindowWidth ?? target.WindowWidth;
        target.WindowHeight = over.WindowHeight ?? target.WindowHeight;
        target.Fullscreen = over.Fullscreen ?? target.Fullscreen;
        target.PreLaunchCommand = over.PreLaunchCommand ?? target.PreLaunchCommand;
        target.PostExitCommand = over.PostExitCommand ?? target.PostExitCommand;
        target.WrapperCommand = over.WrapperCommand ?? target.WrapperCommand;
        target.DefaultAccountId = over.DefaultAccountId ?? target.DefaultAccountId;
        target.Arm64Mode = over.Arm64Mode ?? target.Arm64Mode;
    }

    public static InstanceSettings Diff(InstanceSettings effective, InstanceSettings native) => new()
    {
        JavaPath = effective.JavaPath != native.JavaPath ? effective.JavaPath : null,
        MaxMemoryMb = effective.MaxMemoryMb != native.MaxMemoryMb ? effective.MaxMemoryMb : null,
        MinMemoryMb = effective.MinMemoryMb != native.MinMemoryMb ? effective.MinMemoryMb : null,
        JvmArguments = effective.JvmArguments != native.JvmArguments ? effective.JvmArguments : null,
        GameArguments = effective.GameArguments != native.GameArguments ? effective.GameArguments : null,
        Environment = SameMap(effective.Environment, native.Environment) ? null : effective.Environment,
        WindowWidth = effective.WindowWidth != native.WindowWidth ? effective.WindowWidth : null,
        WindowHeight = effective.WindowHeight != native.WindowHeight ? effective.WindowHeight : null,
        Fullscreen = effective.Fullscreen != native.Fullscreen ? effective.Fullscreen : null,
        PreLaunchCommand = effective.PreLaunchCommand != native.PreLaunchCommand ? effective.PreLaunchCommand : null,
        PostExitCommand = effective.PostExitCommand != native.PostExitCommand ? effective.PostExitCommand : null,
        WrapperCommand = effective.WrapperCommand != native.WrapperCommand ? effective.WrapperCommand : null,
        DefaultAccountId = effective.DefaultAccountId != native.DefaultAccountId ? effective.DefaultAccountId : null,
        Arm64Mode = effective.Arm64Mode != native.Arm64Mode ? effective.Arm64Mode : null,
    };

    private static bool SameMap(Dictionary<string, string>? a, Dictionary<string, string>? b) =>
        (a is null || a.Count == 0) && (b is null || b.Count == 0)
        || (a is not null && b is not null && a.Count == b.Count && a.All(kv => b.TryGetValue(kv.Key, out var v) && v == kv.Value));
}

[JsonSourceGenerationOptions(WriteIndented = true, PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingDefault)]
[JsonSerializable(typeof(InstanceSidecar))]
internal sealed partial class SidecarJsonContext : JsonSerializerContext;
