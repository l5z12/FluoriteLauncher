using Fluorite.Core.Instances;
using Fluorite.Modding.Content;
using Fluorite.Modding.Loaders;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace Fluorite.App.Services;

/// <summary>What fixing mod problems did.</summary>
/// <param name="Fixed">Problems cleared.</param>
/// <param name="Left">Problems still there: changes left out, or no way out found.</param>
/// <param name="Changed">Something in the instance changed (false: there was nothing Fluorite could do).</param>
/// <param name="LoaderNote">The loader's change ("Fabric Loader updated to 0.16.9"), if there was one.</param>
public sealed record ModFixOutcome(int Fixed, int Left, bool Changed, string? LoaderNote)
{
    /// <summary>"Fixed 3 problems." (with how many are left, and the loader's change).</summary>
    public string Summary
    {
        get
        {
            var text = Fixed switch
            {
                0 => Loc.Get("ModFix_DoneNone"),
                1 => Loc.Get("ModFix_DoneOne"),
                _ => Loc.Format("ModFix_DoneMany", Fixed),
            };
            if (Left > 0)
                text += " " + Loc.Format("ModFix_DoneLeft", Left);
            return LoaderNote is null ? text : $"{text} · {LoaderNote}";
        }
    }
}

/// <summary>
/// Fixing mod problems: looks for changes behind a wait dialog (downloading and reading what they bring), shows them
/// to keep or leave out with what would still be wrong, then makes them (after a snapshot) and announces it.
/// </summary>
public sealed class ModFixService(ContentService content, ContentInstallService installer, SnapshotService snapshots)
{
    /// <summary>Settings › Fix mod problems: Fix is offered (Mods tab, before launching, after a crash) only when on.</summary>
    public static bool Enabled => App.Get<SettingsService>().Current.FixModProblems;

    /// <param name="keys">The problems to fix (<see cref="ModIssue.Key"/>s); null for every one that isn't dismissed.</param>
    /// <param name="forLaunch">Asked before playing: the game isn't running yet, and confirming plays.</param>
    /// <param name="includeDismissed">With no <paramref name="keys"/>, dismissed problems too (after a crash they caused).</param>
    /// <returns>Null when the user cancelled, or it failed (and said so).</returns>
    public async Task<ModFixOutcome?> FixAsync(GameInstance instance, IReadOnlyCollection<string>? keys = null, bool forLaunch = false, bool includeDismissed = false)
    {
        var title = Loc.Get("ModFix_Title");
        if (!forLaunch && App.Get<GameRunner>().Find(instance) is not null)
        {
            await Dialogs.ShowMessageAsync(title, Loc.Get("ModFix_StopFirst")); // Its mods are open (and Windows won't rename them).
            return null;
        }
        var manager = content.For(instance);
        ModFixPlan plan;
        try
        {
            var only = keys?.ToHashSet(StringComparer.Ordinal);
            var dismissed = only is null && !includeDismissed ? await manager.GetDismissedIssuesAsync(instance.Id) : null;
            bool Include(ModIssue issue) => only?.Contains(issue.Key) ?? dismissed?.Contains(issue.Key) != true;
            var loaders = LoadersFor(instance);
            plan = await Dialogs.BusyAsync(title, Loc.Get("ModFix_Looking"), (progress, ct) =>
                Task.Run(() => content.PlanFixesAsync(manager, instance.Manifest, Include, loaders, progress, ct), ct));
        }
        catch (OperationCanceledException)
        {
            return null;
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or UnauthorizedAccessException or InvalidDataException)
        {
            await Dialogs.ShowMessageAsync(title, Loc.Format("ModFix_Failed", ex.Message));
            return null;
        }

        if (plan.Fixes.Count == 0)
        {
            await Dialogs.ShowMessageAsync(title, Loc.Get(plan.Targets.Count == 0 ? "ModFix_NothingToFix" : "ModFix_NoFixes"));
            return new ModFixOutcome(0, plan.Targets.Count, Changed: false, null);
        }
        if (!await ReviewAsync(plan, forLaunch))
        {
            plan.Discard();
            return null;
        }

        // Something to go back to, as before updates.
        await snapshots.TakeBeforeUpdateAsync(instance, Loc.Get("Snapshot_BeforeFix"));
        IReadOnlyList<(ContentFile File, Exception Error)> stillOn;
        try
        {
            stillOn = await Task.Run(() => ContentService.ApplyFixesAsync(manager, plan));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            plan.Discard();
            installer.NotifyChanged(instance); // Some of it may be done.
            await Dialogs.ShowMessageAsync(title, Loc.Format("ModFix_ApplyFailed", ex.Message));
            return null;
        }
        var failures = stillOn.Select(f => Loc.Format("ModFix_NotTurnedOff", f.File.DisplayName, f.Error.Message)).ToList();
        string? loaderNote = null;
        if (plan.Loader is { } change)
        {
            loaderNote = await installer.ApplyLoaderAsync(instance, change);
            if (loaderNote is null)
                failures.Add(Loc.Format("ModFix_LoaderNotSaved", ContentInstallService.LoaderName(change.Uid)));
        }
        installer.NotifyChanged(instance);

        // Counted from the instance as it is now, so a change that didn't go through isn't counted as done.
        IReadOnlyList<ModIssue> left;
        try
        {
            left = await Task.Run(() => plan.RecheckAsync(instance.Manifest));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            left = plan.Remaining();
        }
        if (failures.Count > 0)
            await Dialogs.ShowMessageAsync(title, Loc.Format("ModFix_ApplyFailed", "\n" + string.Join("\n", failures)));
        return new ModFixOutcome(plan.FixedCount(left), left.Count, Changed: true, loaderNote);
    }

    /// <summary>The loaders by uid, when the instance's loader may change (not another launcher's read-only instance).</summary>
    private static Func<string, ILoaderProvider?>? LoadersFor(GameInstance instance)
    {
        if (!instance.CanEditComponents)
            return null;
        var providers = App.Get<LoaderService>().Providers;
        return uid => uid == ComponentUids.OptiFine ? null : providers.FirstOrDefault(p => p.Uid == uid);
    }

    // ── The review ──────────────────────────────────────────────────────

    /// <summary>
    /// Every change, ticked, with the problems it's for; a pick of which mod goes where there's a choice; and, as
    /// they're changed, what would still be wrong.
    /// </summary>
    private static async Task<bool> ReviewAsync(ModFixPlan plan, bool forLaunch)
    {
        var panel = new StackPanel { Spacing = 14, MinWidth = 500 };
        var dialog = Dialogs.Create(Loc.Get("ModFix_Title"), Dialogs.Scroll(panel, 500), Loc.Get("ModFix_ApplyOne"));
        var left = new StackPanel { Spacing = 6 };
        void Refresh()
        {
            var count = plan.Fixes.Count(f => f.Selected);
            dialog.IsPrimaryButtonEnabled = count > 0;
            dialog.PrimaryButtonText = forLaunch ? Loc.Get("ModFix_ApplyAndPlay") : count == 1 ? Loc.Get("ModFix_ApplyOne") : Loc.Format("ModFix_ApplyMany", count);
            left.Children.Clear();
            var remaining = plan.Remaining();
            if (remaining.Count == 0)
            {
                left.Children.Add(Dialogs.Bar(Loc.Get("ModFix_AllFixed"), InfoBarSeverity.Success));
                return;
            }
            left.Children.Add(Dialogs.Header(Loc.Get("ModFix_Left")));
            left.Children.Add(Dialogs.Bullets(remaining.Select(ContentInstallService.DescribeForReview).Distinct()));
        }

        panel.Children.Add(Dialogs.Text(Loc.Get("ModFix_Intro"), secondary: true));
        foreach (var fix in plan.Fixes.OrderBy(f => Order(f.Kind)))
            panel.Children.Add(Row(fix, Refresh));
        if (plan.Fixes.Any(f => f.Kind is ModFixKind.Disable or ModFixKind.KeepOne))
            panel.Children.Add(Caption(Loc.Get("ModFix_OffNote"), indent: false));
        panel.Children.Add(left);
        Refresh();
        return await Dialogs.ShowAsync(dialog) == ContentDialogResult.Primary;
    }

    /// <summary>The loader first (it decides what loads), then what comes in, what changes, and what goes.</summary>
    private static int Order(ModFixKind kind) => kind switch
    {
        ModFixKind.AddLoader => 0,
        ModFixKind.LoaderVersion => 1,
        ModFixKind.Install => 2,
        ModFixKind.Replace => 3,
        ModFixKind.Enable => 4,
        ModFixKind.KeepOne => 5,
        _ => 6,
    };

    /// <summary>A change: its tick and what it does (with a pick when there's a choice), and under it the problems it's for.</summary>
    private static StackPanel Row(ModFix fix, Action changed)
    {
        var box = new CheckBox { IsChecked = fix.Selected, MinWidth = 0 };
        box.Checked += (_, _) =>
        {
            fix.Selected = true;
            changed();
        };
        box.Unchecked += (_, _) =>
        {
            fix.Selected = false;
            changed();
        };
        var row = new StackPanel { Spacing = 2 };
        if (fix.Kind is ModFixKind.Disable or ModFixKind.KeepOne && fix.Choices.Count > 1)
        {
            // "Turn off [OptiFine ▾]", "Keep [sodium-0.6.0.jar ▾] and turn off the other copy".
            box.Content = Loc.Get(fix.Kind == ModFixKind.Disable ? "ModFix_TurnOff" : "ModFix_Keep");
            var pick = new ComboBox
            {
                ItemsSource = fix.Choices.Select(c => ChoiceName(fix, c)).ToList(),
                SelectedIndex = fix.Choice,
                MinWidth = 160,
                MaxWidth = 320,
                VerticalAlignment = VerticalAlignment.Center,
            };
            AutomationProperties.SetName(pick, Loc.Get(fix.Kind == ModFixKind.Disable ? "ModFix_WhichOff" : "ModFix_WhichKeep"));
            pick.SelectionChanged += (_, _) =>
            {
                if (pick.SelectedIndex < 0)
                    return;
                fix.Choice = pick.SelectedIndex;
                changed();
            };
            var line = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            line.Children.Add(box);
            line.Children.Add(pick);
            if (fix.Kind == ModFixKind.KeepOne)
            {
                var others = fix.Choices.Count - 1;
                line.Children.Add(new TextBlock
                {
                    Text = others == 1 ? Loc.Get("ModFix_KeepRestOne") : Loc.Format("ModFix_KeepRestMany", others),
                    VerticalAlignment = VerticalAlignment.Center,
                });
            }
            row.Children.Add(line);
        }
        else
        {
            box.Content = new TextBlock { Text = Title(fix), TextWrapping = TextWrapping.Wrap };
            row.Children.Add(box);
        }
        row.Children.Add(Caption(For(fix), indent: true));
        return row;
    }

    private static string Title(ModFix fix)
    {
        switch (fix.Kind)
        {
            case ModFixKind.Install when fix.Download is { } d:
                return Loc.Format("ModFix_Install", d.Title, d.Version.VersionNumber);
            case ModFixKind.Replace when fix.Download is { } d && fix.File is { } old:
                var newer = old.Record.Source?.Published is { } at ? d.Version.Published > at
                    : !Modding.Metadata.LooseVersion.TryParse(old.Version, out var was) || !Modding.Metadata.LooseVersion.TryParse(d.Version.VersionNumber, out var now) || now > was;
                return old.Version is { Length: > 0 } from
                    ? Loc.Format(newer ? "ModFix_Update" : "ModFix_Switch", old.DisplayName, from, d.Version.VersionNumber)
                    : Loc.Format(newer ? "ModFix_UpdateTo" : "ModFix_SwitchTo", old.DisplayName, d.Version.VersionNumber);
            case ModFixKind.Enable when fix.File is { } file:
                return Loc.Format("ModFix_Enable", file.DisplayName);
            case ModFixKind.Disable when fix.Choices.Count > 0:
                return Loc.Format("ModFix_Disable", fix.Choices[fix.Choice].DisplayName);
            case ModFixKind.LoaderVersion when fix.Loader is { } change:
                return Loc.Format("ModFix_LoaderUpdate", ContentInstallService.LoaderName(change.Uid), change.From, change.To);
            case ModFixKind.AddLoader when fix.Loader is { } change:
                return Loc.Format("ModFix_LoaderAdd", ContentInstallService.LoaderName(change.Uid), change.To);
            default:
                return fix.Kind.ToString();
        }
    }

    /// <summary>What the change is for: its problems (the first few), and when the loader is installed.</summary>
    private static string For(ModFix fix)
    {
        const int Shown = 3;
        var lines = fix.Issues.Select(ContentInstallService.DescribeForReview).Distinct().ToList();
        var text = Loc.Format("ModFix_For", string.Join("\n", lines.Take(Shown)));
        if (lines.Count > Shown)
            text += "\n" + Loc.Format("ModFix_AndMore", lines.Count - Shown);
        if (fix.Loader is not null)
            text += "\n" + Loc.Get("ModFix_LoaderNote");
        return text;
    }

    /// <summary>Copies go by file (they share a name) with their version; the two sides of a conflict by name.</summary>
    private static string ChoiceName(ModFix fix, ContentFile file)
    {
        if (fix.Kind == ModFixKind.KeepOne || fix.Choices.Count(c => c.DisplayName == file.DisplayName) > 1)
            return file.Version is { Length: > 0 } v ? $"{file.FileName} ({v})" : file.FileName;
        return file.DisplayName;
    }

    /// <summary>Secondary text; under a change, lined up with its text rather than its tick.</summary>
    private static TextBlock Caption(string text, bool indent) => new()
    {
        Text = text,
        TextWrapping = TextWrapping.Wrap,
        Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"],
        Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
        Margin = new Thickness(indent ? 28 : 0, 0, 0, 0),
    };
}
