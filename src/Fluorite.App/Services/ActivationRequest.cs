using Microsoft.Windows.AppLifecycle;
using Windows.ApplicationModel.Activation;

namespace Fluorite.App.Services;

/// <summary>
/// A normalized activation: command line (<c>--page</c>, <c>--launch</c> [<c>--world</c>|<c>--server</c>], <c>--install</c>),
/// protocol (<c>fluorite://launch/&lt;id&gt;[?world=…|?server=…]</c>, <c>fluorite://instance/&lt;id&gt;</c>,
/// <c>fluorite://page/&lt;tag&gt;</c>, <c>fluorite://install?…</c>), or file (<c>.mrpack</c>, modpack <c>.zip</c>).
/// </summary>
public sealed record ActivationRequest(string? Page = null, string? LaunchInstanceId = null, string? OpenInstanceId = null, IReadOnlyList<string>? Files = null)
{
    /// <summary>A modpack to install: URL, packwiz <c>pack.toml</c>, or <c>modrinth:id[@version]</c>-style short form.</summary>
    public string? InstallLocation { get; init; }

    /// <summary>With <see cref="LaunchInstanceId"/>: open this world or join this server once the game starts.</summary>
    public Core.Launch.QuickPlay? QuickPlay { get; init; }

    public static Core.Launch.QuickPlay? QuickPlayFrom(string? world, string? server) =>
        !string.IsNullOrWhiteSpace(world) ? new Core.Launch.QuickPlay(Core.Launch.QuickPlayKind.Singleplayer, world)
        : !string.IsNullOrWhiteSpace(server) ? new Core.Launch.QuickPlay(Core.Launch.QuickPlayKind.Multiplayer, server.Trim())
        : null;

    /// <summary>The <c>fluorite://launch</c> link for an instance, optionally straight into a world or server.</summary>
    public static Uri LaunchUri(string instanceId, Core.Launch.QuickPlay? quickPlay = null)
    {
        var query = quickPlay switch
        {
            { Kind: Core.Launch.QuickPlayKind.Singleplayer } q => "?world=" + Uri.EscapeDataString(q.Target),
            { Kind: Core.Launch.QuickPlayKind.Multiplayer } q => "?server=" + Uri.EscapeDataString(q.Target),
            _ => "",
        };
        return new Uri($"{Platform.Windows.Shell.ShellIntegration.Scheme}://launch/{Uri.EscapeDataString(instanceId)}{query}");
    }

    /// <summary>Jump list / shortcut arguments for the same.</summary>
    public static string LaunchArguments(string instanceId, Core.Launch.QuickPlay? quickPlay = null) => quickPlay switch
    {
        { Kind: Core.Launch.QuickPlayKind.Singleplayer } q => $"--launch {Quote(instanceId)} --world {Quote(q.Target)}",
        { Kind: Core.Launch.QuickPlayKind.Multiplayer } q => $"--launch {Quote(instanceId)} --server {Quote(q.Target)}",
        _ => $"--launch {Quote(instanceId)}",
    };

    private static string Quote(string s) => "\"" + s.Replace("\"", "", StringComparison.Ordinal) + "\"";

    public bool IsEmpty => Page is null && LaunchInstanceId is null && OpenInstanceId is null && InstallLocation is null && (Files is null || Files.Count == 0);

    public static ActivationRequest FromCommandLine(IReadOnlyList<string> args)
    {
        string? page = null, launch = null, install = null, world = null, server = null;
        var files = new List<string>();
        for (var i = 0; i < args.Count; i++)
        {
            var a = args[i];
            if (a == "--page" && i + 1 < args.Count)
                page = args[++i];
            else if (a == "--launch" && i + 1 < args.Count)
                launch = args[++i];
            else if (a == "--world" && i + 1 < args.Count)
                world = args[++i];
            else if (a == "--server" && i + 1 < args.Count)
                server = args[++i];
            else if (a == "--install" && i + 1 < args.Count)
                install = args[++i];
            else if (StartupFlags.ValueSwitches.Contains(a) && i + 1 < args.Count)
                i++; // --root, --lang, --theme: their value isn't a file to open.
            else if ((a.StartsWith(Platform.Windows.Shell.ShellIntegration.Scheme + ":", StringComparison.OrdinalIgnoreCase) || a.StartsWith("curseforge:", StringComparison.OrdinalIgnoreCase))
                && Uri.TryCreate(a, UriKind.Absolute, out var uri))
                return FromUri(uri);
            else if ((a.EndsWith(".mrpack", StringComparison.OrdinalIgnoreCase) || a.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)) && File.Exists(a))
                files.Add(a);
        }
        return new ActivationRequest(page, launch, null, files)
        {
            InstallLocation = install,
            QuickPlay = launch is null ? null : QuickPlayFrom(world, server),
        };
    }

    public static ActivationRequest FromUri(Uri uri)
    {
        // fluorite://launch/<id> → Host = "launch", AbsolutePath = "/<id>"
        var target = Uri.UnescapeDataString(uri.AbsolutePath.Trim('/'));
        if (uri.Scheme.Equals("curseforge", StringComparison.OrdinalIgnoreCase))
        {
            // curseforge://install?addonId=<project>&fileId=<file>
            var q = Query(uri);
            return q.TryGetValue("addonId", out var addon)
                ? new ActivationRequest { InstallLocation = q.TryGetValue("fileId", out var file) ? $"curseforge:{addon}@{file}" : $"curseforge:{addon}" }
                : new ActivationRequest();
        }
        return uri.Host.ToLowerInvariant() switch
        {
            "launch" when target.Length > 0 => new ActivationRequest(LaunchInstanceId: target)
            {
                QuickPlay = Query(uri) is var q ? QuickPlayFrom(q.GetValueOrDefault("world"), q.GetValueOrDefault("server")) : null,
            },
            "instance" when target.Length > 0 => new ActivationRequest(OpenInstanceId: target),
            "page" when target.Length > 0 => new ActivationRequest(Page: target),
            "install" => new ActivationRequest { InstallLocation = InstallFrom(Query(uri)) },
            _ => new ActivationRequest(),
        };
    }

    /// <summary>
    /// <c>fluorite://install?url=…</c>, <c>?modrinth=&lt;id&gt;[&amp;version=…]</c>, <c>?curseforge=&lt;id&gt;[&amp;file=…]</c>,
    /// <c>?ftb=&lt;id&gt;[&amp;version=…]</c>, <c>?technic=&lt;slug&gt;</c>.
    /// </summary>
    private static string? InstallFrom(Dictionary<string, string> q)
    {
        if (q.TryGetValue("url", out var url))
            return url;
        foreach (var (key, versionKey) in new[] { ("modrinth", "version"), ("curseforge", "file"), ("ftb", "version"), ("technic", "build") })
        {
            if (q.TryGetValue(key, out var id))
                return q.TryGetValue(versionKey, out var v) ? $"{key}:{id}@{v}" : $"{key}:{id}";
        }
        return null;
    }

    private static Dictionary<string, string> Query(Uri uri)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var part in uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var eq = part.IndexOf('=', StringComparison.Ordinal);
            if (eq > 0)
                result[Uri.UnescapeDataString(part[..eq])] = Uri.UnescapeDataString(part[(eq + 1)..].Replace('+', ' '));
        }
        return result;
    }

    public static ActivationRequest FromAppActivation(AppActivationArguments args) => args.Kind switch
    {
        ExtendedActivationKind.Launch when args.Data is ILaunchActivatedEventArgs l =>
            FromCommandLine(Program.SplitArguments(l.Arguments)),
        ExtendedActivationKind.Protocol when args.Data is IProtocolActivatedEventArgs p => FromUri(p.Uri),
        ExtendedActivationKind.File when args.Data is IFileActivatedEventArgs f =>
            new ActivationRequest(Files: f.Files.Select(x => x.Path).ToList()),
        _ => new ActivationRequest(),
    };
}
