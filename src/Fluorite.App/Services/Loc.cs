using Microsoft.Windows.ApplicationModel.Resources;

namespace Fluorite.App.Services;

/// <summary>Localized strings for code-behind/view models (XAML uses x:Uid).</summary>
public static class Loc
{
    private static readonly ResourceLoader s_loader = new();

    public static string Get(string key)
    {
        try
        {
            var value = s_loader.GetString(key);
            return string.IsNullOrEmpty(value) ? key : value;
        }
        catch (Exception)
        {
            return key;
        }
    }

    public static string Format(string key, params object?[] args) =>
        string.Format(System.Globalization.CultureInfo.CurrentCulture, Get(key), args);
}
