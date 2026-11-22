using System.Text.Json;
using Fluorite.Core.Json;
using Fluorite.Core.Net;
using Fluorite.Core.Versions;

namespace Fluorite.Core.Assets;

public sealed record AssetPlan(string IndexId, AssetIndex Index, IReadOnlyList<DownloadItem> Downloads);

/// <summary>Downloads asset indexes/objects and materializes legacy <c>virtual</c> and pre-1.6 <c>resources</c> layouts.</summary>
public sealed class AssetManager(GameDirectories dirs, DownloadEngine downloader, MirrorTable mirrors)
{
    public const string ResourcesHost = "https://resources.download.minecraft.net/";

    public async Task<AssetPlan> PlanAsync(ResolvedVersion version, CancellationToken ct = default)
    {
        var id = version.AssetIndexId;
        var indexPath = Path.Combine(dirs.AssetIndexes, id + ".json");

        if (version.AssetIndex is { Url: { Length: > 0 } url } reference)
        {
            await downloader.DownloadAllAsync(
                [new DownloadItem(mirrors.Candidates(url), indexPath, reference.Sha1, reference.Size) { DisplayName = $"asset index {id}" }],
                ct: ct).ConfigureAwait(false);
        }
        else if (!File.Exists(indexPath))
        {
            throw new FileNotFoundException($"Asset index '{id}' is missing and the version provides no URL.", indexPath);
        }

        AssetIndex index;
        await using (var fs = File.OpenRead(indexPath))
        {
            index = await JsonSerializer.DeserializeAsync(fs, FluoriteJsonContext.Default.AssetIndex, ct).ConfigureAwait(false)
                ?? new AssetIndex();
        }

        var items = new List<DownloadItem>(index.Objects.Count);
        foreach (var (name, obj) in index.Objects)
        {
            if (obj.Hash.Length < 2)
                continue;
            var rel = obj.Hash[..2] + "/" + obj.Hash;
            items.Add(new DownloadItem(mirrors.Candidates(ResourcesHost + rel), dirs.AssetObject(obj.Hash), obj.Hash, obj.Size)
            {
                DisplayName = name,
            });
        }
        return new AssetPlan(id, index, items);
    }

    /// <summary>
    /// Returns the directory to use for <c>${game_assets}</c> after copying objects into the legacy layouts when required.
    /// </summary>
    public string MaterializeLegacyLayouts(AssetPlan plan, string gameDirectory)
    {
        if (plan.Index.MapToResources)
        {
            var target = Path.Combine(gameDirectory, "resources");
            CopyObjects(plan, target);
            return target;
        }
        if (plan.Index.Virtual)
        {
            var target = Path.Combine(dirs.Assets, "virtual", plan.IndexId);
            CopyObjects(plan, target);
            return target;
        }
        return dirs.Assets;
    }

    private void CopyObjects(AssetPlan plan, string targetRoot)
    {
        foreach (var (name, obj) in plan.Index.Objects)
        {
            var target = Path.GetFullPath(Path.Combine(targetRoot, name.Replace('/', Path.DirectorySeparatorChar)));
            if (!target.StartsWith(Path.GetFullPath(targetRoot), StringComparison.OrdinalIgnoreCase))
                continue; // Path traversal guard.
            var fi = new FileInfo(target);
            if (fi.Exists && fi.Length == obj.Size)
                continue;
            Directory.CreateDirectory(fi.DirectoryName!);
            File.Copy(dirs.AssetObject(obj.Hash), target, overwrite: true);
        }
    }
}
