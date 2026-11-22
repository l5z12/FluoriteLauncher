using Fluorite.Core.Instances;
using Fluorite.Core.Versions;

namespace Fluorite.App.Services;

/// <summary>
/// Whether an instance's Minecraft can open a world on start (<c>--quickPlaySingleplayer</c>, 23w14a and later). Joining a
/// server works on every version from 1.6 (older ones use <c>--server</c>/<c>--port</c>).
/// </summary>
public sealed class QuickPlaySupport(VersionRepository versions)
{
    private static readonly DateTimeOffset s_firstSnapshot = new(2023, 4, 5, 0, 0, 0, TimeSpan.Zero);

    public async Task<bool> CanOpenWorldsAsync(GameInstance instance)
    {
        if (instance.Manifest.MinecraftVersion is not { Length: > 0 } mc)
            return true; // Unknown: offer it; the launch logs a note if the game can't.
        try
        {
            var manifest = await versions.GetManifestAsync();
            return manifest.Versions.FirstOrDefault(v => v.Id == mc)?.ParsedReleaseTime is not { } released || released >= s_firstSnapshot;
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or InvalidDataException or TaskCanceledException or System.Text.Json.JsonException)
        {
            return true;
        }
    }
}
