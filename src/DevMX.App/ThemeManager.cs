using System.Windows;
using System.Windows.Media;

namespace DevMX.App;

/// <summary>
/// Static theme manager that swaps the active palette dictionary at runtime.
/// </summary>
public static class ThemeManager
{
    private const string ControlsSource = "Themes/Controls.xaml";
    private const string DarkSource = "Themes/Dark.xaml";
    private const string LightSource = "Themes/Light.xaml";

    /// <summary>Raised after a theme switch completes.</summary>
    public static event Action? ThemeChanged;

    /// <summary>Apply the named theme ("dark" or "light"). Unknown values default to dark.</summary>
    public static void Apply(string theme)
    {
        Application.Current.Dispatcher.Invoke(() =>
        {
            var merged = Application.Current.Resources.MergedDictionaries;

            // Remove any existing palette dictionaries (keep Controls.xaml)
            var toRemove = merged.Where(d =>
            {
                if (d.Source == null) return false;
                var uri = d.Source.OriginalString;
                return uri.Contains("Dark.xaml") || uri.Contains("Light.xaml");
            }).ToList();

            foreach (var dict in toRemove)
            {
                merged.Remove(dict);
            }

            // Add the selected palette
            string source = theme switch
            {
                "light" => LightSource,
                _ => DarkSource // default to dark
            };

            var palette = new ResourceDictionary { Source = new Uri(source, UriKind.Relative) };
            // Insert after Controls.xaml (index 0)
            if (merged.Count > 0)
            {
                merged.Insert(1, palette);
            }
            else
            {
                merged.Add(palette);
            }

            ThemeChanged?.Invoke();
        });
    }
}
