using System.Reflection;
using Fluorite.Core.Instances;
using Fluorite.Platform.Windows.Discord;

namespace Fluorite.App.Services;

/// <summary>
/// Shows the game being played on Discord: the instance, its Minecraft version and loader, and the time played.
/// Needs a Discord application id (build property <c>FluoriteDiscordAppId</c> or the <c>FLUORITE_DISCORD_APP_ID</c>
/// environment variable); without one, or with the setting off, it does nothing.
/// </summary>
public sealed class DiscordService : IDisposable
{
    private readonly GameRunner _runner;
    private readonly SettingsService _settings;
    private readonly Fluorite.Modding.Loaders.LoaderService _loaders;
    private readonly DiscordPresence? _presence;

    public DiscordService(GameRunner runner, SettingsService settings, Fluorite.Modding.Loaders.LoaderService loaders)
    {
        _runner = runner;
        _settings = settings;
        _loaders = loaders;
        if (ApplicationId() is { } id)
            _presence = new DiscordPresence(id);
        runner.GameStarted += (_, _) => Update();
        runner.GameEnded += (_, _) => Update();
    }

    /// <summary>Whether this build can show Rich Presence at all (the setting is hidden otherwise).</summary>
    public bool IsAvailable => _presence is not null;

    private static string? ApplicationId()
    {
        var id = Environment.GetEnvironmentVariable("FLUORITE_DISCORD_APP_ID") is { Length: > 0 } env
            ? env
            : typeof(DiscordService).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>().FirstOrDefault(a => a.Key == "FluoriteDiscordAppId")?.Value;
        return id is { Length: > 0 } && id.All(char.IsAsciiDigit) ? id : null;
    }

    /// <summary>Shows the most recently started running game, or clears the activity.</summary>
    public void Update()
    {
        if (_presence is null)
            return;
        var game = _settings.Current.ShowDiscordPresence
            ? _runner.Games.Where(g => g.IsRunning && g.StartedAt is not null).MaxBy(g => g.StartedAt)
            : null;
        var activity = game is null ? null : new DiscordActivity(
            Loc.Format("Discord_Playing", game.Name),
            Describe(game.Instance),
            game.StartedAt!.Value,
            LargeImage: "logo",
            LargeText: "Fluorite");
        _ = _presence.SetAsync(activity);
    }

    private string Describe(GameInstance instance)
    {
        var version = instance.Manifest.MinecraftVersion;
        var loader = ComponentResolver.PrimaryLoader(instance.Manifest);
        return loader is null
            ? Loc.Format("Discord_Version", version)
            : Loc.Format("Discord_VersionLoader", version, _loaders.Get(loader.Uid).DisplayName);
    }

    public void Dispose() => _presence?.Dispose();
}
