using System.CommandLine;
using System.Diagnostics;
using Fluorite.Core.Instances;
using Fluorite.Modding.Content;
using Fluorite.Modding.Platforms;
using Fluorite.Modding.Util;

namespace Fluorite.Cli;

/// <summary><c>fluorite content …</c>: search, install (with dependencies), list, update and check instance content.</summary>
internal static class ContentCommands
{
    public static Command Build(Func<ParseResult, CliServices> services)
    {
        var kindOption = new Option<ContentKind>("--kind") { DefaultValueFactory = _ => ContentKind.Mod, Recursive = true };
        var platformOption = new Option<ContentPlatform>("--platform") { DefaultValueFactory = _ => ContentPlatform.Modrinth };
        var instanceOption = new Option<string>("--instance") { Required = true, Description = "Instance id (folder name)" };
        var worldOption = new Option<string?>("--world") { Description = "World for data packs" };
        var limitOption = new Option<int>("--limit") { DefaultValueFactory = _ => 10 };

        var content = new Command("content", "Mods, resource packs, shaders and data packs");

        // ── search ──────────────────────────────────────────────────────
        var queryArg = new Argument<string>("query") { DefaultValueFactory = _ => "" };
        var mcOption = new Option<string?>("--mc");
        var loaderOption = new Option<string?>("--loader") { Description = "fabric|quilt|forge|neoforge|…" };
        var search = new Command("search") { queryArg, platformOption, mcOption, loaderOption, limitOption };
        search.SetAction(async (pr, ct) =>
        {
            using var s = services(pr);
            var platform = s.Content.Get(pr.GetValue(platformOption));
            var target = new ContentTarget(pr.GetValue(kindOption), pr.GetValue(mcOption), pr.GetValue(loaderOption) is { } l ? [l] : []);
            var r = await platform.SearchAsync(new ContentSearchQuery(target, pr.GetValue(queryArg), ContentSort.Relevance, 0, pr.GetValue(limitOption)), ct);
            foreach (var p in r.Hits)
                Console.WriteLine($"  {p.Id,-10} {p.Slug,-32} {p.Downloads,12:N0}  {p.Title} — {p.Summary}");
            Console.WriteLine($"{r.TotalHits:N0} result(s)");
            return 0;
        });
        content.Subcommands.Add(search);

        // ── install ─────────────────────────────────────────────────────
        var projectArg = new Argument<string[]>("project") { Description = "Project id(s) or slug(s)", Arity = ArgumentArity.OneOrMore };
        var versionOption = new Option<string?>("--version") { Description = "Exact version/file id (default: best compatible)" };
        var install = new Command("install", "Install projects (and required dependencies) into an instance, one by one") { projectArg, platformOption, instanceOption, versionOption, worldOption };
        install.SetAction(async (pr, ct) =>
        {
            using var s = services(pr);
            var instance = await RequireInstanceAsync(s, pr.GetValue(instanceOption)!, ct);
            var kind = pr.GetValue(kindOption);
            var location = new ContentLocation(kind, pr.GetValue(worldOption));
            var target = ContentService.TargetFor(instance.Manifest, kind);
            var platform = s.Content.Get(pr.GetValue(platformOption));
            var manager = s.Content.For(instance);
            var failures = 0;
            var sw = Stopwatch.StartNew();

            foreach (var projectId in pr.GetValue(projectArg)!)
            {
                try
                {
                    // "project@version" pins an exact version (e.g. replaying a modpack one mod at a time).
                    var (pid, pinned) = projectId.Split('@', 2) is [var idPart, var versionPart] ? (idPart, versionPart) : (projectId, pr.GetValue(versionOption));
                    var project = await platform.GetProjectAsync(pid, ct);
                    var version = pinned is { } vid
                        ? await platform.GetVersionAsync(project.Id, vid, ct)
                        : ContentService.PickBest(await platform.GetVersionsAsync(project.Id, target, ct), target);
                    if (version is null)
                    {
                        Console.WriteLine($"  ✗ {project.Title}: no version for {target.MinecraftVersion} {string.Join('/', target.Loaders)}");
                        failures++;
                        continue;
                    }
                    var installedIds = await InstalledIdsAsync(manager, kind, ct);
                    var plan = await s.Content.PlanAsync(manager, target, version, location, project, installedIds, ct);
                    var blocked = await s.Content.ExecuteAsync(manager, plan, null, ct);
                    if (plan.Files.Count == 0 && plan.AlreadyInstalled.Count > 0)
                    {
                        Console.WriteLine($"  = {project.Title} {version.VersionNumber} already installed");
                        continue;
                    }
                    var deps = plan.Dependencies.Select(d => d.Title).ToList();
                    Console.WriteLine($"  ✓ {project.Title} {version.VersionNumber}{(deps.Count > 0 ? $" (+ {string.Join(", ", deps)})" : "")}");
                    foreach (var u in plan.Unresolved)
                        Console.WriteLine($"      unresolved dependency: {u}");
                    foreach (var i in plan.Incompatible)
                        Console.WriteLine($"      incompatible with installed: {i}");
                    foreach (var b in blocked)
                        Console.WriteLine($"      manual download needed: {b.Title} → {b.File.ManualDownloadUrl}");
                }
                catch (Exception ex) when (ex is HttpRequestException or InvalidDataException or Core.Net.DownloadFailedException or IOException or UnauthorizedAccessException)
                {
                    Console.WriteLine($"  ✗ {projectId}: {ex.Message}");
                    failures++;
                }
            }
            Console.WriteLine($"Done in {sw.Elapsed.TotalSeconds:F1}s, {failures} failure(s).");
            return failures == 0 ? 0 : 1;
        });
        content.Subcommands.Add(install);

        // ── list ────────────────────────────────────────────────────────
        var list = new Command("list", "Scan, identify and list an instance's content") { instanceOption, worldOption };
        list.SetAction(async (pr, ct) =>
        {
            using var s = services(pr);
            var instance = await RequireInstanceAsync(s, pr.GetValue(instanceOption)!, ct);
            var manager = s.Content.For(instance);
            var location = new ContentLocation(pr.GetValue(kindOption), pr.GetValue(worldOption));
            var sw = Stopwatch.StartNew();
            var files = await manager.ScanAsync(location, ct);
            var scanned = sw.Elapsed;
            var identified = await s.Content.IdentifyAsync(manager, files, ct);
            files = await manager.ScanAsync(location, ct);
            foreach (var f in files)
            {
                var src = f.Record.Source is { } r ? $"{r.Platform}:{r.ProjectId}" : "local";
                var loaders = string.Join('/', f.Metadata.Loaders);
                Console.WriteLine($"  {(f.Enabled ? " " : "-")} {f.DisplayName,-36} {f.Version,-24} {loaders,-16} {src,-22} {f.FileName}");
            }
            Console.WriteLine($"{files.Count} file(s); scanned in {scanned.TotalSeconds:F1}s, {identified} newly identified.");
            return 0;
        });
        content.Subcommands.Add(list);

        // ── updates ─────────────────────────────────────────────────────
        var applyOption = new Option<bool>("--apply") { Description = "Install the updates" };
        var updates = new Command("updates", "Check for (and optionally apply) updates") { instanceOption, applyOption, worldOption };
        updates.SetAction(async (pr, ct) =>
        {
            using var s = services(pr);
            var instance = await RequireInstanceAsync(s, pr.GetValue(instanceOption)!, ct);
            var kind = pr.GetValue(kindOption);
            var manager = s.Content.For(instance);
            var location = new ContentLocation(kind, pr.GetValue(worldOption));
            var target = ContentService.TargetFor(instance.Manifest, kind);
            var files = await manager.ScanAsync(location, ct);
            await s.Content.IdentifyAsync(manager, files, ct);
            files = await manager.ScanAsync(location, ct);
            var found = await s.Content.CheckUpdatesAsync(files, target, ct);
            foreach (var u in found)
                Console.WriteLine($"  {u.File.DisplayName,-36} {u.File.Version,-24} → {u.Latest.VersionNumber}");
            Console.WriteLine($"{found.Count} update(s) available.");
            if (found.Count == 0 || !pr.GetValue(applyOption))
                return 0;

            var plan = await s.Content.PlanUpdatesAsync(manager, target, found, await InstalledIdsAsync(manager, kind, ct), ct);
            var blocked = await s.Content.ExecuteAsync(manager, plan, new ConsoleDownloadProgress(), ct);
            Console.WriteLine($"Updated {plan.Files.Count - blocked.Count} file(s); {blocked.Count} need a manual download.");
            return 0;
        });
        content.Subcommands.Add(updates);

        // ── check ───────────────────────────────────────────────────────
        var check = new Command("check", "Pre-launch mod checks") { instanceOption };
        check.SetAction(async (pr, ct) =>
        {
            using var s = services(pr);
            var instance = await RequireInstanceAsync(s, pr.GetValue(instanceOption)!, ct);
            var files = await s.Content.For(instance).ScanAsync(new ContentLocation(ContentKind.Mod), ct);
            var issues = ModChecker.Check(files, instance.Manifest);
            foreach (var i in issues)
                Console.WriteLine($"  {i.Severity,-7} {i.Kind,-24} {i.Subject} [{i.File}] {i.Other} {i.Detail}");
            Console.WriteLine($"{issues.Count} issue(s) in {files.Count} mod file(s).");
            return issues.Any(i => i.Severity == ModIssueSeverity.Error) ? 2 : 0;
        });
        content.Subcommands.Add(check);

        // ── identify ────────────────────────────────────────────────────
        var fileArg = new Argument<string>("file");
        var identify = new Command("identify", "Hash a file and look it up on both platforms") { fileArg };
        identify.SetAction(async (pr, ct) =>
        {
            using var s = services(pr);
            var hashes = await ContentHashes.ComputeAsync(pr.GetValue(fileArg)!, ct);
            Console.WriteLine($"  sha1     {hashes.Sha1}\n  murmur2  {hashes.Murmur2}");
            foreach (var p in s.Content.Platforms)
            {
                var hits = await p.IdentifyAsync([hashes], ct);
                Console.WriteLine(hits.Count == 0
                    ? $"  {p.Platform}: not found"
                    : $"  {p.Platform}: project {hits[0].Version.ProjectId}, version {hits[0].Version.Id} ({hits[0].Version.Name})");
            }
            var meta = Fluorite.Modding.Metadata.ContentMetadataReader.Read(pr.GetValue(fileArg)!);
            foreach (var m in meta.Mods)
                Console.WriteLine($"  mod {m.Id} {m.Version} [{m.Loader}] deps: {string.Join(", ", m.Dependencies.Select(d => $"{d.Id}:{d.Kind}{(d.VersionRange is null ? "" : "@" + d.VersionRange)}"))}");
            Console.WriteLine($"  nested: {string.Join(", ", meta.Nested.Select(n => n.Id))}");
            return 0;
        });
        content.Subcommands.Add(identify);

        return content;
    }

    /// <summary>By id (folder name) or display name.</summary>
    public static async Task<GameInstance> RequireInstanceAsync(CliServices s, string id, CancellationToken ct)
    {
        var all = await s.Registry.GetInstancesAsync(ct);
        return all.FirstOrDefault(i => string.Equals(i.Id, id, StringComparison.OrdinalIgnoreCase))
            ?? all.FirstOrDefault(i => string.Equals(i.Name, id, StringComparison.OrdinalIgnoreCase))
            ?? throw new ArgumentException($"No instance '{id}' under {s.Root}.");
    }

    private static async Task<IReadOnlySet<string>> InstalledIdsAsync(ContentManager manager, ContentKind kind, CancellationToken ct)
    {
        if (kind != ContentKind.Mod)
            return new HashSet<string>();
        var files = await manager.ScanAsync(new ContentLocation(ContentKind.Mod), ct);
        return files.Where(f => f.Enabled).SelectMany(f => f.Metadata.ProvidedIds).ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    private sealed class ConsoleDownloadProgress : IProgress<Core.Net.DownloadProgress>
    {
        public void Report(Core.Net.DownloadProgress value) =>
            Console.Write($"\r  {value.CompletedFiles}/{value.TotalFiles} files   ");
    }
}
