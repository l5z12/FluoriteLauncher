using Fluorite.Core.Instances;

namespace Fluorite.Core.Tests;

public sealed class InstanceSnapshotTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "fluorite-snap-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    [Fact]
    public async Task Restore_puts_back_mods_configs_and_loader_but_leaves_worlds()
    {
        var game = Path.Combine(_root, "game");
        Write(game, "mods/a.jar", "a1");
        Write(game, "config/x.toml", "old");
        Write(game, "saves/World/level.dat", "world1");
        var instance = Instance(game, "1.21.1");
        var store = new InstanceSnapshots(Path.Combine(_root, "snapshots"));

        var snapshot = await store.CreateAsync(instance, "Before", automatic: false, ct: TestContext.Current.CancellationToken);

        File.Delete(Path.Combine(game, "mods", "a.jar"));
        Write(game, "mods/b.jar", "b1");
        Write(game, "config/x.toml", "new");
        Write(game, "saves/World/level.dat", "world2");
        Write(game, "kubejs/startup.js", "added later");
        instance.Manifest.Components[0].Version = "1.21.4";

        var components = await store.RestoreAsync(instance, snapshot, TestContext.Current.CancellationToken);

        Assert.Equal("a1", File.ReadAllText(Path.Combine(game, "mods", "a.jar")));
        Assert.False(File.Exists(Path.Combine(game, "mods", "b.jar")));
        Assert.Equal("old", File.ReadAllText(Path.Combine(game, "config", "x.toml")));
        Assert.False(Directory.Exists(Path.Combine(game, "kubejs")));
        Assert.Equal("world2", File.ReadAllText(Path.Combine(game, "saves", "World", "level.dat")));
        Assert.Equal("1.21.1", Assert.Single(components).Version);
        Assert.Equal(1, Assert.Single(store.List(instance)).Manifest.ModCount);
    }

    [Fact]
    public async Task Prune_keeps_the_newest_automatic_and_every_manual_one()
    {
        var game = Path.Combine(_root, "game");
        Write(game, "mods/a.jar", "a");
        var instance = Instance(game, "1.21.1");
        var store = new InstanceSnapshots(Path.Combine(_root, "snapshots"));
        await store.CreateAsync(instance, "mine", automatic: false, ct: TestContext.Current.CancellationToken);
        for (var i = 0; i < 4; i++)
        {
            await store.CreateAsync(instance, $"auto {i}", automatic: true, ct: TestContext.Current.CancellationToken);
            await Task.Delay(5, TestContext.Current.CancellationToken); // Distinct folder names (milliseconds).
        }

        store.Prune(instance, keep: 2);

        var left = store.List(instance);
        Assert.Equal(["auto 3", "auto 2", "mine"], left.Select(s => s.Manifest.Label));
    }

    private static void Write(string game, string relative, string text)
    {
        var path = Path.Combine(game, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
    }

    private GameInstance Instance(string game, string minecraft) => new()
    {
        Id = "test",
        Layout = LayoutKind.Fluorite,
        Directory = game,
        GameDirectory = game,
        Store = new GameDirectories(Path.Combine(_root, "store")),
        Manifest = new InstanceManifest { Name = "Test", Components = [new ComponentRef { Uid = ComponentUids.Minecraft, Version = minecraft }] },
        LaunchVersionId = minecraft,
        MetadataDirectory = Path.Combine(game, ".fluorite"),
    };
}
