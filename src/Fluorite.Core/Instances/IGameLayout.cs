namespace Fluorite.Core.Instances;

/// <summary>
/// A directory structure containing instances: Fluorite's own, an official <c>.minecraft</c>,
/// an HMCL/PCL isolated <c>.minecraft</c>, or a Prism/MultiMC data folder.
/// </summary>
public interface IGameLayout
{
    LayoutKind Kind { get; }
    string Root { get; }
    Task<IReadOnlyList<GameInstance>> GetInstancesAsync(CancellationToken ct = default);
    Task SaveAsync(GameInstance instance, CancellationToken ct = default);
}
