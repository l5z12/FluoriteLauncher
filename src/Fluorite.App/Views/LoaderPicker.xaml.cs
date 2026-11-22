using Fluorite.App.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Fluorite.App.Views;

/// <summary>
/// Mod loader + loader version picker. The host supplies <see cref="ViewModel"/> (no default instance: swapping view
/// models re-assigns the ComboBox ItemsSource, which would reset selections).
/// </summary>
public sealed partial class LoaderPicker : UserControl
{
    public static readonly DependencyProperty ViewModelProperty = DependencyProperty.Register(
        nameof(ViewModel), typeof(LoaderPickerViewModel), typeof(LoaderPicker),
        new PropertyMetadata(null, (d, _) => ((LoaderPicker)d).Bindings?.Update()));

    public LoaderPicker()
    {
        InitializeComponent();
    }

    public LoaderPickerViewModel? ViewModel
    {
        get => (LoaderPickerViewModel?)GetValue(ViewModelProperty);
        set => SetValue(ViewModelProperty, value);
    }
}
