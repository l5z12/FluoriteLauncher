using CommunityToolkit.Mvvm.ComponentModel;
using Fluorite.App.Services;
using Fluorite.Core.Instances;
using Fluorite.Core.Settings;

namespace Fluorite.App.ViewModels;

/// <summary>Another launcher's folder in Settings: show its instances, allow writing its metadata, remove.</summary>
public sealed partial class ExternalRootItem(ExternalRoot root, OtherLaunchersService service) : ObservableObject
{
    public ExternalRoot Root { get; } = root;
    public string Title => Label(Root);

    /// <summary>"Prism Launcher"; the id is added only when it tells two folders of one launcher apart ("prism-2").</summary>
    public static string Label(ExternalRoot root)
    {
        var name = Loc.Get("Launcher_" + root.Launcher);
        var defaultId = InstanceRegistry.ProposeId(root.Launcher, []);
        return string.Equals(root.Id, defaultId, StringComparison.OrdinalIgnoreCase) ? name : $"{name} ({root.Id})";
    }
    public string Path => Root.Path;
    public bool CanWriteBack => Root.Kind == LayoutKind.MultiMC;

    public bool Enabled
    {
        get => Root.Enabled;
        set
        {
            if (Root.Enabled == value)
                return;
            Root.Enabled = value;
            OnPropertyChanged();
            service.Save();
        }
    }

    public bool AllowWriteBack
    {
        get => Root.AllowWriteBack;
        set
        {
            if (Root.AllowWriteBack == value)
                return;
            Root.AllowWriteBack = value;
            OnPropertyChanged();
            service.Save();
        }
    }
}
