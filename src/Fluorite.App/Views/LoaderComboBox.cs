using System.ComponentModel;
using Fluorite.App.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Fluorite.App.Views;

/// <summary>
/// ComboBox of <see cref="LoaderOption"/>s whose items are disabled (greyed out, not pickable) while the loader has no
/// build for the chosen Minecraft version. Availability arrives after the containers exist, so each option's changes
/// are followed and pushed to its container.
/// </summary>
public sealed partial class LoaderComboBox : ComboBox
{
    protected override void PrepareContainerForItemOverride(DependencyObject element, object item)
    {
        base.PrepareContainerForItemOverride(element, item);
        if (element is ComboBoxItem container && item is LoaderOption option)
        {
            container.IsEnabled = option.IsSelectable;
            option.PropertyChanged -= Option_PropertyChanged;
            option.PropertyChanged += Option_PropertyChanged;
        }
    }

    protected override void ClearContainerForItemOverride(DependencyObject element, object item)
    {
        if (item is LoaderOption option)
            option.PropertyChanged -= Option_PropertyChanged;
        base.ClearContainerForItemOverride(element, item);
    }

    private void Option_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(LoaderOption.IsSelectable) && sender is LoaderOption option && ContainerFromItem(option) is ComboBoxItem container)
            container.IsEnabled = option.IsSelectable;
    }
}
