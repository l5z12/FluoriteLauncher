using Fluorite.Core.Nbt;

namespace Fluorite.Core.Worlds;

public enum GameMode
{
    Survival = 0,
    Creative = 1,
    Adventure = 2,
    Spectator = 3,
}

/// <summary>A singleplayer world: a folder under <c>saves/</c> with a <c>level.dat</c>.</summary>
public sealed record WorldInfo
{
    /// <summary>Folder name under <c>saves/</c>; what Quick Play and backups refer to.</summary>
    public required string FolderName { get; init; }

    public required string Path { get; init; }

    /// <summary>The name shown in game (<c>LevelName</c>); the folder name when level.dat has none.</summary>
    public required string Name { get; init; }

    public DateTimeOffset? LastPlayed { get; init; }
    public GameMode? GameMode { get; init; }
    public bool Hardcore { get; init; }
    public bool CheatsEnabled { get; init; }

    /// <summary>Minecraft version that last saved it (1.9+), e.g. <c>1.21.4</c>.</summary>
    public string? VersionName { get; init; }

    public int? DataVersion { get; init; }
    public bool IsSnapshot { get; init; }
    public long? Seed { get; init; }

    /// <summary><c>icon.png</c>, when the world has one.</summary>
    public string? IconPath { get; init; }

    /// <summary>Why level.dat couldn't be read; the world is still listed so it can be backed up or deleted.</summary>
    public string? Error { get; init; }

    /// <summary>Reads a world folder; never throws for a bad level.dat (see <see cref="Error"/>).</summary>
    public static WorldInfo Read(string directory)
    {
        var folder = System.IO.Path.GetFileName(directory.TrimEnd(System.IO.Path.DirectorySeparatorChar, System.IO.Path.AltDirectorySeparatorChar));
        var icon = System.IO.Path.Combine(directory, "icon.png");
        var iconPath = File.Exists(icon) ? icon : null;
        var levelDat = System.IO.Path.Combine(directory, "level.dat");
        NbtCompound data;
        try
        {
            data = NbtFile.Read(levelDat).GetCompound("Data") ?? throw new InvalidDataException("level.dat has no Data compound.");
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            return new WorldInfo
            {
                FolderName = folder,
                Path = directory,
                Name = folder,
                IconPath = iconPath,
                LastPlayed = File.Exists(levelDat) ? File.GetLastWriteTimeUtc(levelDat) : null,
                Error = ex.Message,
            };
        }

        var version = data.GetCompound("Version");
        var lastPlayed = data.GetLong("LastPlayed");
        return new WorldInfo
        {
            FolderName = folder,
            Path = directory,
            Name = data.GetString("LevelName") is { Length: > 0 } name ? name : folder,
            LastPlayed = lastPlayed is > 0 ? DateTimeOffset.FromUnixTimeMilliseconds(lastPlayed.Value) : File.GetLastWriteTimeUtc(levelDat),
            GameMode = data.GetInt("GameType") is { } gt and >= 0 and <= 3 ? (GameMode)gt : null,
            Hardcore = data.GetBool("hardcore") == true,
            CheatsEnabled = data.GetBool("allowCommands") == true,
            VersionName = version?.GetString("Name"),
            DataVersion = data.GetInt("DataVersion") ?? version?.GetInt("Id"),
            IsSnapshot = version?.GetBool("Snapshot") == true,
            // 1.16+ keeps the seed in WorldGenSettings; older worlds in RandomSeed.
            Seed = data.GetCompound("WorldGenSettings")?.GetLong("seed") ?? data.GetLong("RandomSeed"),
            IconPath = iconPath,
        };
    }
}

/// <summary>A world backup zip in <c>backups/</c>, named like Minecraft's own "Make Backup": <c>yyyy-MM-dd_HH-mm-ss_&lt;folder&gt;.zip</c>.</summary>
public sealed record WorldBackup(string Path, string WorldFolder, DateTimeOffset Created, long Size)
{
    public string FileName => System.IO.Path.GetFileName(Path);

    /// <summary>Made after a play session (pruned to the configured count), not by the user.</summary>
    public bool IsAutomatic { get; init; }
}
