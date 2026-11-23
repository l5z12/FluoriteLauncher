using System.CommandLine;
using System.Diagnostics;
using Fluorite.Core.Instances;
using Fluorite.Core.Settings;
using Fluorite.Modding.Modpacks;

namespace Fluorite.Cli;

/// <summary><c>pack …</c> (modpacks) and <c>launchers …</c> (other launchers' folders) commands.</summary>
internal static class PackCommands
{
    public static IEnumerable<Command> Build(Func<ParseResult, CliServices> services)
    {
        yield return BuildPack(services);
        yield return BuildLaunchers(services);
    }

    private static string WorkDir(CliServices s) => Path.Combine(s.Root, "cache", "packs", Guid.NewGuid().ToString("N")[..8]);

    private static Command BuildPack(Func<ParseResult, CliServices> services)
    {
        var pack = new Command("pack", "Modpacks: import (.mrpack, CurseForge, Prism, packwiz, FTB, Technic), update, export");
        var locationArg = new Argument<string>("location") { Description = "File, URL, packwiz pack.toml, or modrinth:<id>[@ver] / curseforge:<id>[@file] / ftb:<id>[@ver] / technic:<slug>" };
        var nameOption = new Option<string?>("--name");
        var noOptional = new Option<bool>("--no-optional") { Description = "Skip files the pack marks optional" };

        var info = new Command("info", "Read a modpack without installing it") { locationArg };
        info.SetAction(async (pr, ct) =>
        {
            using var s = services(pr);
            var m = await s.Packs.LoadLocationAsync(pr.GetValue(locationArg)!, WorkDir(s), (IProgress<Fluorite.Core.Net.DownloadProgress>)new ConsoleProgress(), ct);
            Print(m);
            return 0;
        });

        var import = new Command("import", "Install a modpack as a new instance") { locationArg, nameOption, noOptional };
        import.SetAction(async (pr, ct) =>
        {
            using var s = services(pr);
            var sw = Stopwatch.StartNew();
            var work = WorkDir(s);
            var m = await s.Packs.LoadLocationAsync(pr.GetValue(locationArg)!, work, (IProgress<Fluorite.Core.Net.DownloadProgress>)new ConsoleProgress(), ct);
            Print(m);
            var instance = await ModpackInstaller.CreateInstanceAsync(s.Layout, m, pr.GetValue(nameOption), ct: ct);
            var progress = new ConsoleProgress(); IProgress<Fluorite.Core.Net.DownloadProgress> dl = progress;
            PackInstallResult result;
            try
            {
                result = await s.Packs.Installer.InstallAsync(m, instance, s.Content.For(instance), !pr.GetValue(noOptional), dl, ct);
            }
            catch
            {
                TryDelete(instance.Directory); // Don't leave a half-installed instance behind.
                throw;
            }
            progress.Done();
            TryDelete(work);
            Console.WriteLine($"Installed '{instance.Name}' as instance '{instance.Id}' in {sw.Elapsed.TotalSeconds:F1}s ({m.Files.Count} downloads, {m.Overrides.Count} overrides).");
            foreach (var b in result.Blocked)
                Console.WriteLine($"  needs manual download: {b.Path}  ←  {b.ManualDownloadUrl}");
            Console.WriteLine($"INSTANCE={instance.Id}");
            return result.Blocked.Count == 0 ? 0 : 3;
        });

        var instanceArg = new Argument<string>("instance");
        var applyOption = new Option<bool>("--apply");
        var overwriteOption = new Option<bool>("--overwrite-modified") { Description = "Also replace files you changed (default: keep them)" };
        var update = new Command("update", "Check an installed pack for a newer version, show the changes, and apply them with --apply") { instanceArg, applyOption, overwriteOption, noOptional };
        update.SetAction(async (pr, ct) =>
        {
            using var s = services(pr);
            var instance = await ContentCommands.RequireInstanceAsync(s, pr.GetValue(instanceArg)!, ct);
            var state = PackState.Load(instance.MetadataDirectory) ?? throw new InvalidOperationException($"'{instance.Name}' wasn't installed from a modpack.");
            Console.WriteLine($"{state.Name} {state.Version} ({state.Format})");
            var available = await s.Packs.CheckUpdateAsync(state, ct);
            if (available is null)
            {
                Console.WriteLine("Up to date.");
                return 0;
            }
            Console.WriteLine($"Update available: {available.VersionName}");
            var work = WorkDir(s);
            var m = await s.Packs.LoadUpdateAsync(state, available, work, (IProgress<Fluorite.Core.Net.DownloadProgress>)new ConsoleProgress(), ct);
            var plan = await ModpackInstaller.PlanUpdateAsync(instance, m, !pr.GetValue(noOptional), ct);
            if (plan.NewMinecraft is { } mc)
                Console.WriteLine($"  Minecraft → {mc}");
            if (plan.NewLoader is { } l)
                Console.WriteLine($"  loader → {l.Uid} {l.Version}");
            foreach (var c in plan.Changes)
            {
                if (pr.GetValue(overwriteOption) && c.Kind is PackChangeKind.Modified or PackChangeKind.Conflict)
                    c.Apply = true;
                Console.WriteLine($"  {(c.Apply ? "*" : " ")} {c.Kind,-9} {c.Path}");
            }
            Console.WriteLine($"{plan.Changes.Count(c => c.Apply)} change(s) to apply, {plan.Changes.Count(c => !c.Apply)} kept as you have them, {plan.Unchanged} unchanged.");
            if (!pr.GetValue(applyOption))
                return 0;
            var progress = new ConsoleProgress(); IProgress<Fluorite.Core.Net.DownloadProgress> dl = progress;
            var result = await s.Packs.Installer.ApplyUpdateAsync(plan, instance, s.Content.For(instance), i => s.Registry.SaveAsync(i, ct), progress: dl, ct: ct);
            progress.Done();
            TryDelete(work);
            foreach (var b in result.Blocked)
                Console.WriteLine($"  needs manual download: {b.Path}  ←  {b.ManualDownloadUrl}");
            Console.WriteLine($"Updated to {m.Version}.");
            return 0;
        });

        var formatOption = new Option<string>("--format") { Description = "mrpack|curseforge|prism|packwiz", Required = true };
        var outOption = new Option<string>("--out") { Required = true };
        var includeOption = new Option<string?>("--include") { Description = "Comma-separated top-level entries (default: everything except worlds and personal settings)" };
        var versionOption = new Option<string>("--pack-version") { DefaultValueFactory = _ => "1.0.0" };
        var export = new Command("export", "Export an instance as a modpack") { instanceArg, formatOption, outOption, includeOption, nameOption, versionOption };
        export.SetAction(async (pr, ct) =>
        {
            using var s = services(pr);
            var instance = await ContentCommands.RequireInstanceAsync(s, pr.GetValue(instanceArg)!, ct);
            var format = pr.GetValue(formatOption)!.ToLowerInvariant() switch
            {
                "mrpack" or "modrinth" => PackFormat.Modrinth,
                "curseforge" or "cf" => PackFormat.CurseForge,
                "prism" or "multimc" => PackFormat.MultiMC,
                "packwiz" => PackFormat.Packwiz,
                var other => throw new ArgumentException($"Unknown format '{other}'."),
            };
            var include = pr.GetValue(includeOption) is { } list
                ? list.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                : ModpackExporter.DefaultSelection(instance.GameDirectory).Where(c => c.IncludedByDefault).Select(c => c.Name).ToArray();
            var result = await s.Packs.Exporter.ExportAsync(instance, new PackExportOptions
            {
                Format = format,
                Name = pr.GetValue(nameOption) ?? instance.Name,
                Version = pr.GetValue(versionOption)!,
                Include = include,
            }, Path.GetFullPath(pr.GetValue(outOption)!), ct);
            foreach (var w in result.Warnings)
                Console.WriteLine($"  warning: {w}");
            Console.WriteLine($"Exported {result.Path}: {result.Referenced} file(s) referenced from Modrinth/CurseForge, {result.Bundled} bundled.");
            return 0;
        });

        var copy = new Command("copy", "Make a Fluorite copy of an instance from another launcher's folder") { instanceArg, nameOption };
        copy.SetAction(async (pr, ct) =>
        {
            using var s = services(pr);
            var source = await ContentCommands.RequireInstanceAsync(s, pr.GetValue(instanceArg)!, ct);
            var copied = await InstanceImporter.CopyAsync(s.Layout, source, pr.GetValue(nameOption), ct);
            Console.WriteLine($"Copied '{source.Name}' to instance '{copied.Id}'.");
            Console.WriteLine($"INSTANCE={copied.Id}");
            return 0;
        });

        pack.Subcommands.Add(info);
        pack.Subcommands.Add(import);
        pack.Subcommands.Add(update);
        pack.Subcommands.Add(export);
        pack.Subcommands.Add(copy);
        return pack;
    }

    private static Command BuildLaunchers(Func<ParseResult, CliServices> services)
    {
        var launchers = new Command("launchers", "Other launchers' folders (used in place, or imported)");

        var detect = new Command("detect", "Find other launchers on this PC");
        detect.SetAction(pr =>
        {
            foreach (var d in LauncherDetection.Detect())
                Console.WriteLine($"  {d.Launcher,-14} {(d.Layout?.ToString() ?? "import only"),-12} {d.InstanceCount,4}  {d.Path}");
            return 0;
        });

        var pathArg = new Argument<string>("path");
        var writeBack = new Option<bool>("--write-back") { Description = "Let Fluorite write Prism/MultiMC metadata (instance.cfg, mmc-pack.json)" };
        var idOption = new Option<string?>("--id");
        var add = new Command("add", "Use another launcher's folder in place") { pathArg, writeBack, idOption };
        add.SetAction(pr =>
        {
            using var s = services(pr);
            var detected = InstanceRegistry.Detect(pr.GetValue(pathArg)!) ?? throw new ArgumentException("Not a Prism/MultiMC, official or HMCL/PCL folder.");
            var roots = s.Settings.ExternalRoots;
            if (roots.Any(r => string.Equals(r.Path, detected.Path, StringComparison.OrdinalIgnoreCase)))
            {
                Console.WriteLine($"{detected.Path} is already added.");
                return 0;
            }
            var root = new ExternalRoot
            {
                Id = pr.GetValue(idOption) ?? InstanceRegistry.ProposeId(detected.Launcher, roots.Select(r => r.Id)),
                Path = detected.Path,
                Kind = detected.Kind,
                Launcher = detected.Launcher,
                AllowWriteBack = pr.GetValue(writeBack),
            };
            roots.Add(root);
            s.SaveSettings();
            Console.WriteLine($"Added '{root.Id}': {root.Launcher} ({root.Kind}) at {root.Path}");
            return 0;
        });

        var list = new Command("list", "Folders used in place");
        list.SetAction(pr =>
        {
            using var s = services(pr);
            foreach (var r in s.Settings.ExternalRoots)
                Console.WriteLine($"  {r.Id,-12} {r.Launcher,-14} {r.Kind,-9} {(r.Enabled ? "on " : "off")} {(r.AllowWriteBack ? "write-back" : "read-only "),-10} {r.Path}");
            return 0;
        });

        var rootIdArg = new Argument<string>("id");
        var remove = new Command("remove", "Stop using a folder (nothing is deleted)") { rootIdArg };
        remove.SetAction(pr =>
        {
            using var s = services(pr);
            var removed = s.Settings.ExternalRoots.RemoveAll(r => string.Equals(r.Id, pr.GetValue(rootIdArg), StringComparison.OrdinalIgnoreCase));
            s.SaveSettings();
            Console.WriteLine(removed > 0 ? "Removed." : "No such folder.");
            return 0;
        });

        var launcherArg = new Argument<OtherLauncher>("launcher") { Description = "CurseForge|ModrinthApp|GDLauncher|ATLauncher|FtbApp" };
        var scan = new Command("scan", "List another launcher's instances that can be imported") { launcherArg, pathArg };
        scan.SetAction(pr =>
        {
            foreach (var c in InstanceImporter.Scan(pr.GetValue(launcherArg), pr.GetValue(pathArg)!))
                Console.WriteLine($"  {c.Name,-32} {c.MinecraftVersion ?? "?",-10} {c.LoaderUid ?? "vanilla",-30} {c.LoaderVersion,-12} {c.GameDirectory}");
            return 0;
        });

        var nameArg = new Argument<string>("name");
        var mcOption = new Option<string?>("--mc");
        var loaderOption = new Option<string?>("--loader-uid");
        var loaderVersionOption = new Option<string?>("--loader-version");
        var importInstance = new Command("import", "Copy one of another launcher's instances into Fluorite") { launcherArg, pathArg, nameArg, mcOption, loaderOption, loaderVersionOption };
        importInstance.SetAction(async (pr, ct) =>
        {
            using var s = services(pr);
            var candidate = InstanceImporter.Scan(pr.GetValue(launcherArg), pr.GetValue(pathArg)!)
                .FirstOrDefault(c => string.Equals(c.Name, pr.GetValue(nameArg), StringComparison.OrdinalIgnoreCase))
                ?? throw new ArgumentException("No such instance.");
            var mc = pr.GetValue(mcOption) ?? candidate.MinecraftVersion ?? throw new ArgumentException("Pass --mc: the launcher's metadata doesn't say.");
            var instance = await InstanceImporter.ImportAsync(s.Layout, candidate, mc, pr.GetValue(loaderOption) ?? candidate.LoaderUid, pr.GetValue(loaderVersionOption) ?? candidate.LoaderVersion, ct: ct);
            Console.WriteLine($"Imported '{candidate.Name}' as instance '{instance.Id}'.");
            Console.WriteLine($"INSTANCE={instance.Id}");
            return 0;
        });

        launchers.Subcommands.Add(detect);
        launchers.Subcommands.Add(add);
        launchers.Subcommands.Add(list);
        launchers.Subcommands.Add(remove);
        launchers.Subcommands.Add(scan);
        launchers.Subcommands.Add(importInstance);
        return launchers;
    }

    private static void Print(PackManifest m)
    {
        Console.WriteLine($"{m.Name} {m.Version} ({m.Format}) — Minecraft {m.MinecraftVersion}{(m.LoaderUid is null ? "" : $", {m.LoaderUid} {m.LoaderVersion}")}");
        Console.WriteLine($"  {m.Files.Count} file(s) to download ({m.Files.Count(f => f.IsBlocked)} blocked, {m.Files.Count(f => f.Client == PackSide.Optional)} optional, {m.Files.Count(f => f.Client == PackSide.Unsupported)} server-only), {m.Overrides.Count} override(s)");
        foreach (var w in m.Warnings)
            Console.WriteLine($"  warning: {w}");
    }

    private static void TryDelete(string dir)
    {
        try
        {
            if (Directory.Exists(dir))
                Directory.Delete(dir, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}
