using Fluorite.App.Services;
using Fluorite.App.ViewModels;
using Fluorite.Modding.Metadata;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace Fluorite.App.Views;

/// <summary>
/// "Mod info": what a mod jar declares about itself (id, version, authors, side, what it depends on, with a tick or
/// cross for this instance, what it bundles) and the file itself. Values can be selected and copied.
/// </summary>
public static class ModInfoDialog
{
    public static async Task ShowAsync(ContentItemViewModel item, ContentListViewModel list)
    {
        var file = item.File;
        var panel = new StackPanel { Spacing = 12, Width = 620 };

        // Heading: icon, name, version and file.
        var iconTile = new Border { Width = 56, Height = 56, CornerRadius = new CornerRadius(8), Background = Brush("CardBackgroundFillColorSecondaryBrush") };
        var iconGrid = new Grid();
        iconGrid.Children.Add(new FontIcon { Glyph = "", FontSize = 22, Foreground = Brush("TextFillColorTertiaryBrush") });
        iconGrid.Children.Add(new Image { Source = item.Icon, Stretch = Stretch.UniformToFill });
        iconTile.Child = iconGrid;
        var titles = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        titles.Children.Add(new TextBlock { Text = item.Name, Style = (Style)Application.Current.Resources["SubtitleTextBlockStyle"], TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true });
        titles.Children.Add(Dialogs.Text(string.Join("  ·  ", new[] { item.Version, item.FileName }.Where(s => !string.IsNullOrEmpty(s))), secondary: true));
        var heading = new Grid { ColumnSpacing = 16 };
        heading.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        heading.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        heading.Children.Add(iconTile);
        Grid.SetColumn(titles, 1);
        heading.Children.Add(titles);
        panel.Children.Add(heading);

        if (item.Issue is { } issue)
            panel.Children.Add(new InfoBar { IsOpen = true, IsClosable = false, Severity = item.IssueIsError ? InfoBarSeverity.Error : InfoBarSeverity.Warning, Message = issue });
        if (item.IsManifestEdited)
            panel.Children.Add(new InfoBar { IsOpen = true, IsClosable = false, Severity = InfoBarSeverity.Informational, Message = Loc.Get("ModInfo_Edited") });

        var targets = list.InstanceTargets;
        foreach (var mod in file.Metadata.Mods)
        {
            if (file.Metadata.Mods.Count > 1)
                panel.Children.Add(Dialogs.Header(mod.Name ?? mod.Id));
            var facts = new List<(string, string?)>
            {
                (Loc.Get("ModInfo_Loader"), mod.Loader switch
                {
                    ModLoaderKind.LegacyForge => "Forge (mcmod.info)",
                    ModLoaderKind.NeoForge => "NeoForge",
                    _ => mod.Loader.ToString(),
                }),
                (Loc.Get("ModInfo_Id"), mod.Id),
                (Loc.Get("ModInfo_Version"), mod.Version),
                (Loc.Get("ModInfo_Authors"), mod.Authors.Count > 0 ? string.Join(", ", mod.Authors) : null),
                (Loc.Get("ModInfo_Side"), mod.Environment switch
                {
                    "client" => Loc.Get("ModInfo_SideClient"),
                    "server" => Loc.Get("ModInfo_SideServer"),
                    "*" => Loc.Get("ModInfo_SideBoth"),
                    _ => null,
                }),
                (Loc.Get("ModInfo_Provides"), mod.Provides.Count > 0 ? string.Join(", ", mod.Provides) : null),
                (Loc.Get("ModInfo_License"), mod.License),
            };
            panel.Children.Add(Facts(facts));
            if (mod.Description is { Length: > 0 } description)
                panel.Children.Add(new TextBlock { Text = ContentMetadataReader.StripFormatting(description).Trim(), TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true });

            if (mod.Links.Count > 0)
            {
                panel.Children.Add(Dialogs.Header(Loc.Get("ModInfo_Links")));
                panel.Children.Add(Rows(mod.Links.OrderBy(l => LinkOrder.IndexOf(l.Kind) is var i and >= 0 ? i : LinkOrder.Count).Select(l => (LinkLabel(l.Kind), LinkText(l)))));
            }

            if (mod.Dependencies.Count > 0)
            {
                panel.Children.Add(Dialogs.Header(Loc.Get("ModInfo_Dependencies")));
                var rows = new StackPanel { Spacing = 6 };
                foreach (var dep in mod.Dependencies.OrderBy(d => d.Kind).ThenBy(d => d.Id == "minecraft" ? 0 : targets.Any(t => t.DependencyId == d.Id) ? 1 : 2))
                    rows.Children.Add(DependencyRow(dep, mod.Loader, targets, list));
                panel.Children.Add(rows);
            }
        }
        if (file.Metadata.Mods.Count == 0)
            panel.Children.Add(Dialogs.Text(Loc.Get("ModInfo_NoMetadata"), secondary: true));

        if (file.Metadata.Nested.Count > 0)
        {
            var nested = new TextBlock { TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true, Foreground = Brush("TextFillColorSecondaryBrush") };
            nested.Text = string.Join("\n", file.Metadata.Nested.OrderBy(n => n.Name ?? n.Id, StringComparer.CurrentCultureIgnoreCase)
                .Select(n => $"{n.Name ?? n.Id}  ({n.Id}{(n.Version is { } v ? " " + v : "")})"));
            panel.Children.Add(new Expander
            {
                Header = Loc.Format("ModInfo_Bundled", file.Metadata.Nested.Count),
                Content = nested,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Left,
            });
        }

        panel.Children.Add(Dialogs.Header(Loc.Get("ModInfo_File")));
        panel.Children.Add(Facts(
        [
            (Loc.Get("ModInfo_FileName"), item.FileName),
            (Loc.Get("ModInfo_Size"), Formats.Size(file.Size)),
            (Loc.Get("ModInfo_Source"), item.SourceText),
            ("SHA-1", file.Record.Sha1),
        ]));

        var canEdit = item.IsMod;
        var dialog = Dialogs.Create(Loc.Get("ModInfo_Title"), Dialogs.Scroll(panel, 560), canEdit ? Loc.Get("ModInfo_EditManifest") : null, Loc.Get("Common_Close"),
            list.ProjectFor(item) is not null ? Loc.Get("ModInfo_OpenProject") : null);
        dialog.DefaultButton = ContentDialogButton.Close;
        Dialogs.FocusCloseOnOpen(dialog);
        switch (await Dialogs.ShowAsync(dialog))
        {
            case ContentDialogResult.Primary:
                await ManifestEditorDialog.ShowAsync(item, list);
                break;
            case ContentDialogResult.Secondary when list.ProjectFor(item) is { } nav:
                App.Window?.NavigateToProject(nav);
                break;
        }
    }

    /// <summary>One dependency: a tick when this instance has it (in a version it takes), a cross when it doesn't.</summary>
    private static Grid DependencyRow(ModDependency dep, ModLoaderKind loader, IReadOnlyList<(string DependencyId, string Label, string Version)> targets, ContentListViewModel list)
    {
        var target = targets.FirstOrDefault(t => t.DependencyId == dep.Id);
        // Mods count by every id they provide, bundled jars included (Fabric API's modules, say). Java isn't a mod: shown, not judged.
        bool? met = target.DependencyId is not null ? VersionPredicate.Matches(dep.VersionRange, target.Version, loader)
            : dep.Id == "java" ? null
            : list.HasModId(dep.Id);
        var wanted = dep.Kind is DependencyKind.Required or DependencyKind.Optional;
        var (glyph, brush) = (met, wanted) switch
        {
            (null, _) => ("", "TextFillColorTertiaryBrush"),
            (true, true) => ("", "SystemFillColorSuccessBrush"),
            (false, true) when dep.Kind == DependencyKind.Optional => ("", "TextFillColorTertiaryBrush"),
            (false, true) => ("", "SystemFillColorCriticalBrush"),
            (true, false) => ("", "SystemFillColorCautionBrush"), // An incompatible mod is present.
            (false, false) => ("", "SystemFillColorSuccessBrush"),
        };
        var row = new Grid { ColumnSpacing = 10 };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.Children.Add(new FontIcon { Glyph = glyph, FontSize = 14, Foreground = Brush(brush), VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 3, 0, 0) });
        var name = target.DependencyId is not null ? $"{target.Label} ({dep.Id})" : dep.Id;
        var text = new TextBlock { TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true };
        text.Inlines.Add(new Microsoft.UI.Xaml.Documents.Run { Text = name, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
        text.Inlines.Add(new Microsoft.UI.Xaml.Documents.Run { Text = "  " + (dep.VersionRange ?? Loc.Get("ModInfo_AnyVersion")) });
        if (target.DependencyId is not null)
            text.Inlines.Add(new Microsoft.UI.Xaml.Documents.Run { Text = "  " + Loc.Format("ModInfo_Have", target.Version), Foreground = Brush("TextFillColorTertiaryBrush") });
        Grid.SetColumn(text, 1);
        row.Children.Add(text);
        var kind = new TextBlock
        {
            Text = Loc.Get("ModInfo_Dep" + dep.Kind),
            Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"],
            Foreground = Brush("TextFillColorSecondaryBrush"),
            VerticalAlignment = VerticalAlignment.Center,
        };
        Grid.SetColumn(kind, 2);
        row.Children.Add(kind);
        return row;
    }

    /// <summary>The usual ones first, in this order; a mod's own extra keys (Patreon, Twitter…) after them, as it lists them.</summary>
    private static readonly List<string> LinkOrder = [ModLink.Homepage, ModLink.Sources, ModLink.Issues, "wiki", "discord", "email", "irc"];

    private static string LinkLabel(string kind) => kind switch
    {
        ModLink.Homepage => Loc.Get("ModInfo_LinkHomepage"),
        ModLink.Sources => Loc.Get("ModInfo_LinkSources"),
        ModLink.Issues => Loc.Get("ModInfo_LinkIssues"),
        "wiki" => Loc.Get("ModInfo_LinkWiki"),
        "email" => Loc.Get("ModInfo_LinkEmail"),
        "discord" => "Discord",
        "irc" => "IRC",
        _ => char.ToUpperInvariant(kind[0]) + kind[1..], // Any key a mod made up: "patreon" reads "Patreon".
    };

    /// <summary>
    /// The link as the mod wrote it, clickable when it's a web page or a mail address. Anything else (an
    /// <c>irc://</c> channel, a bare Discord handle) stays text to select and copy: it opens nowhere useful.
    /// </summary>
    private static TextBlock LinkText(ModLink link)
    {
        var value = link.Value;
        var text = new TextBlock { TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true };
        if (LinkTarget(link) is not { } uri)
        {
            text.Text = value;
            return text;
        }
        var shown = uri.Scheme == Uri.UriSchemeMailto && value.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase) ? value["mailto:".Length..] : value;
        var hyperlink = new Microsoft.UI.Xaml.Documents.Hyperlink { NavigateUri = uri };
        hyperlink.Inlines.Add(new Microsoft.UI.Xaml.Documents.Run { Text = shown });
        text.Inlines.Add(hyperlink);
        return text;
    }

    private static Uri? LinkTarget(ModLink link)
    {
        var value = link.Value;
        if (value.Contains(' ', StringComparison.Ordinal))
            return null;
        // A mail address, with or without mailto: (under "email", or any key). "@name" is a handle, not an address.
        if (value.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase))
            return Uri.TryCreate(value, UriKind.Absolute, out var mail) ? mail : null;
        if (value.Contains('@', StringComparison.Ordinal) && !value.Contains('/', StringComparison.Ordinal) && !value.Contains(':', StringComparison.Ordinal))
            return value.IndexOf('@', StringComparison.Ordinal) > 0 && Uri.TryCreate("mailto:" + value, UriKind.Absolute, out var address) ? address : null;
        if (Uri.TryCreate(value, UriKind.Absolute, out var uri))
            return uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp ? uri : null;
        // "github.com/me/mod", "discord.gg/abc": a web address written without its scheme.
        return value.Contains('.', StringComparison.Ordinal) && Uri.TryCreate("https://" + value, UriKind.Absolute, out var web) ? web : null;
    }

    /// <summary>Label/value rows; empty values are left out.</summary>
    private static Grid Facts(IEnumerable<(string Label, string? Value)> facts) =>
        Rows(facts.Where(f => !string.IsNullOrWhiteSpace(f.Value))
            .Select(f => (f.Label, new TextBlock { Text = f.Value, TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true })));

    private static Grid Rows(IEnumerable<(string Label, TextBlock Value)> rows)
    {
        var grid = new Grid { ColumnSpacing = 16, RowSpacing = 4 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var row = 0;
        foreach (var (label, v) in rows)
        {
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var l = new TextBlock { Text = label, Foreground = Brush("TextFillColorSecondaryBrush") };
            Grid.SetRow(l, row);
            Grid.SetRow(v, row);
            Grid.SetColumn(v, 1);
            grid.Children.Add(l);
            grid.Children.Add(v);
            row++;
        }
        return grid;
    }

    private static Brush Brush(string key) => (Brush)Application.Current.Resources[key];
}
