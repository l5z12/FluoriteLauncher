namespace Fluorite.Core.Java;

/// <summary>One entry of Mojang's java-runtime <c>all.json</c> (platform → component → entries).</summary>
public sealed class MojangRuntimeEntry
{
    public MojangRuntimeAvailability? Availability { get; set; }
    public MojangRuntimeFileRef? Manifest { get; set; }
    public MojangRuntimeVersion? Version { get; set; }
}

public sealed class MojangRuntimeAvailability
{
    public int Group { get; set; }
    public int Progress { get; set; }
}

public sealed class MojangRuntimeFileRef
{
    public string? Sha1 { get; set; }
    public long? Size { get; set; }
    public string Url { get; set; } = "";
}

public sealed class MojangRuntimeVersion
{
    public string? Name { get; set; }
    public string? Released { get; set; }
}

/// <summary>A component's per-file manifest.</summary>
public sealed class MojangRuntimeFileManifest
{
    public Dictionary<string, MojangRuntimeFile> Files { get; set; } = [];
}

public sealed class MojangRuntimeFile
{
    /// <summary><c>file</c>, <c>directory</c> or <c>link</c>.</summary>
    public string Type { get; set; } = "file";
    public bool Executable { get; set; }
    public string? Target { get; set; }
    public Dictionary<string, MojangRuntimeFileRef>? Downloads { get; set; }
}
