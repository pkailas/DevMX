using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using ICSharpCode.AvalonEdit;
using ICSharpCode.AvalonEdit.Document;
using ICSharpCode.AvalonEdit.Rendering;
using ICSharpCode.AvalonEdit.Highlighting;

namespace DevMX.App;

/// <summary>
/// Attached properties for binding text content and syntax highlighting to an AvalonEdit TextEditor.
/// </summary>
public static class AvalonEditBehavior
{
    private static readonly Dictionary<TextEditor, DiffColorizer> _diffColorizers = new();
    private static readonly HashSet<TextEditor> _trackedEditors = new();

    static AvalonEditBehavior()
    {
        ThemeManager.ThemeChanged += OnThemeChanged;
    }

    private static void OnThemeChanged()
    {
        Application.Current.Dispatcher.Invoke(() =>
        {
            foreach (var editor in _trackedEditors.ToList())
            {
                ApplyEditorTheme(editor);
            }
        });
    }

    private static void ApplyEditorTheme(TextEditor editor)
    {
        var resources = Application.Current.Resources;
        if (resources["EditorBackgroundBrush"] is Brush bg)
            editor.Background = bg;
        if (resources["EditorForegroundBrush"] is Brush fg)
            editor.Foreground = fg;
        if (resources["DimBrush"] is Brush dim)
            editor.LineNumbersForeground = dim;
    }

    #region BoundText

    public static readonly DependencyProperty BoundTextProperty =
        DependencyProperty.RegisterAttached(
            "BoundText",
            typeof(string),
            typeof(AvalonEditBehavior),
            new PropertyMetadata(string.Empty, OnBoundTextChanged));

    public static void SetBoundText(DependencyObject element, string value)
        => element.SetValue(BoundTextProperty, value);

    public static string GetBoundText(DependencyObject element)
        => (string)element.GetValue(BoundTextProperty);

    private static void OnBoundTextChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is TextEditor editor)
        {
            editor.Text = (string)e.NewValue;
        }
    }

    private static void EditorAttach(object sender, RoutedEventArgs e)
    {
        if (sender is TextEditor editor)
        {
            _trackedEditors.Add(editor);
            ApplyEditorTheme(editor);
            editor.Unloaded += (s, ev) =>
            {
                _trackedEditors.Remove(editor);
                _diffColorizers.Remove(editor);
            };
        }
    }

    #endregion

    #region SyntaxExtension

    public static readonly DependencyProperty SyntaxExtensionProperty =
        DependencyProperty.RegisterAttached(
            "SyntaxExtension",
            typeof(string),
            typeof(AvalonEditBehavior),
            new PropertyMetadata(string.Empty, OnSyntaxExtensionChanged));

    public static void SetSyntaxExtension(DependencyObject element, string value)
        => element.SetValue(SyntaxExtensionProperty, value);

    public static string GetSyntaxExtension(DependencyObject element)
        => (string)element.GetValue(SyntaxExtensionProperty);

    private static void OnSyntaxExtensionChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is TextEditor editor)
        {
            var ext = (string)e.NewValue;
            if (!string.IsNullOrEmpty(ext))
            {
                var def = HighlightingManager.Instance.GetDefinitionByExtension(ext);
                editor.SyntaxHighlighting = def;
            }
            else
            {
                editor.SyntaxHighlighting = null;
            }
        }
    }

    #endregion

    #region IsDiff

    public static readonly DependencyProperty IsDiffProperty =
        DependencyProperty.RegisterAttached(
            "IsDiff",
            typeof(bool),
            typeof(AvalonEditBehavior),
            new PropertyMetadata(false, OnIsDiffChanged));

    public static void SetIsDiff(DependencyObject element, bool value)
        => element.SetValue(IsDiffProperty, value);

    public static bool GetIsDiff(DependencyObject element)
        => (bool)element.GetValue(IsDiffProperty);

    private static void OnIsDiffChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not TextEditor editor)
            return;

        // Use reflection to access TextView since it may be internal in some AvalonEdit versions
        var textViewProp = typeof(TextEditor).GetProperty("TextView",
            System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);
        if (textViewProp == null)
            return;

        var textView = textViewProp.GetValue(editor) as TextView;
        if (textView == null)
        {
            // TextView may not be initialized yet; hook Loaded event
            editor.Loaded += EditorLoaded;
            // Also track for theme changes
            if (!_trackedEditors.Contains(editor))
            {
                _trackedEditors.Add(editor);
                editor.Loaded += (s, e) =>
                {
                    _trackedEditors.Add(editor);
                    ApplyEditorTheme(editor);
                    editor.Unloaded += (s2, e2) =>
                    {
                        _trackedEditors.Remove(editor);
                        _diffColorizers.Remove(editor);
                    };
                };
            }
            return;
        }

        ApplyDiffColorizer(editor, textView, (bool)e.NewValue);
    }

    private static void EditorLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is TextEditor editor)
        {
            editor.Loaded -= EditorLoaded;
            var isDiff = GetIsDiff(editor);
            var textViewProp = typeof(TextEditor).GetProperty("TextView",
                System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);
            if (textViewProp?.GetValue(editor) is TextView textView)
            {
                ApplyDiffColorizer(editor, textView, isDiff);
            }
        }
    }

    /// <summary>Apply theme colors to an editor when attached via Loaded event.</summary>
    public static void ApplyThemeToEditor(TextEditor editor)
    {
        if (!_trackedEditors.Contains(editor))
        {
            _trackedEditors.Add(editor);
            editor.Loaded += EditorAttach;
            editor.Unloaded += (s, e) =>
            {
                _trackedEditors.Remove(editor);
                _diffColorizers.Remove(editor);
            };
        }
        ApplyEditorTheme(editor);
    }

    private static void ApplyDiffColorizer(TextEditor editor, TextView textView, bool isDiff)
    {
        var transformers = textView.LineTransformers;

        // Remove existing colorizer
        if (_diffColorizers.TryGetValue(editor, out var existing))
        {
            transformers.Remove(existing);
            _diffColorizers.Remove(editor);
        }

        // Add colorizer if diff mode
        if (isDiff)
        {
            var colorizer = new DiffColorizer();
            transformers.Add(colorizer);
            _diffColorizers[editor] = colorizer;
        }
    }

    #endregion
}

/// <summary>
/// Colorizes diff lines based on line prefix: + (green), - (red), @@ (blue).
/// Reads brushes from application resources for theme awareness.
/// </summary>
public class DiffColorizer : DocumentColorizingTransformer
{
    private Brush GetBrush(string key)
    {
        if (Application.Current.Resources[key] is Brush brush)
            return brush;
        // Fallback to dark theme colors
        return key switch
        {
            "DiffAddBrush" => new SolidColorBrush(Color.FromRgb(0x1E, 0x3A, 0x1E)),
            "DiffRemoveBrush" => new SolidColorBrush(Color.FromRgb(0x3A, 0x1E, 0x1E)),
            _ => new SolidColorBrush(Color.FromRgb(0x1E, 0x2A, 0x3A)),
        };
    }

    protected override void ColorizeLine(DocumentLine line)
    {
        var text = CurrentContext.Document.GetText(line);
        Brush? background = null;

        var trimmed = text.TrimStart();
        if (trimmed.StartsWith("+"))
            background = GetBrush("DiffAddBrush");
        else if (trimmed.StartsWith("-"))
            background = GetBrush("DiffRemoveBrush");
        else if (trimmed.StartsWith("@@"))
            background = GetBrush("DiffHunkBrush");

        if (background != null)
        {
            ChangeLinePart(line.Offset, line.Offset + line.Length, spinner =>
            {
                spinner.BackgroundBrush = background;
            });
        }
    }
}
