using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace AltTabEverywhere;

/// <summary>
/// Every setting in settings.json, editable without touching the file. Built in code, created only
/// when opened and dropped when closed (see <see cref="Open"/>), so it costs nothing while idle.
/// Each change is written straight away, one key at a time, so keys edited by hand are kept; the
/// file watcher then applies it like any other edit.
/// </summary>
internal sealed class SettingsWindow : Window
{
    public const string WindowTitle = "AltTab Everywhere settings";

    private readonly Settings _s;
    private readonly StackPanel _page = new() { Margin = new Thickness(24, 8, 24, 24) };
    private readonly StackPanel _shortcutRows = new();
    private readonly List<ShortcutRow> _shortcuts = new();

    /// <summary>The settings.json keys this window edits (a test checks every setting is here).</summary>
    internal HashSet<string> EditableKeys { get; } = new();

    /// <summary>
    /// Opens the settings window in its own short-lived process (this exe with --settings). The
    /// switcher process never loads the window's controls, styles or code, so closing the window
    /// gives everything back (in-process, about 22 MB of private memory stayed behind).
    /// </summary>
    public static void Launch()
    {
        try
        {
            string exe = Environment.ProcessPath ?? throw new InvalidOperationException("no process path");
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(exe, "--settings") { UseShellExecute = false });
        }
        catch (Exception ex)
        {
            Log.Error("open settings window", ex);
        }
    }

    /// <summary>Brings an already open settings window to the front. False if there is none.</summary>
    public static bool ActivateExisting()
    {
        IntPtr hwnd = Interop.Native.FindWindow(null, WindowTitle);
        if (hwnd == IntPtr.Zero) return false;
        if (Interop.Native.IsIconic(hwnd)) Interop.Native.ShowWindow(hwnd, Interop.Native.SW_RESTORE);
        Interop.Native.SetForegroundWindow(hwnd);
        return true;
    }

    public SettingsWindow(Settings current)
    {
        // A copy: edits go to the file, and the reload builds a fresh Settings.
        _s = SettingsStore.Deserialize(SettingsStore.Serialize(current));

#pragma warning disable WPF0001 // Fluent theme: native light/dark controls, loaded only with this window
        ThemeMode = current.Theme switch
        {
            AppTheme.Dark => System.Windows.ThemeMode.Dark,
            AppTheme.Light => System.Windows.ThemeMode.Light,
            _ => System.Windows.ThemeMode.System,
        };
#pragma warning restore WPF0001

        Title = WindowTitle;
        Width = 720;
        Height = 760;
        MinWidth = 520;
        MinHeight = 400;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        try
        {
            using var stream = typeof(SettingsWindow).Assembly.GetManifestResourceStream("AltTabEverywhere.app.ico");
            if (stream != null) Icon = BitmapFrame.Create(stream, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
        }
        catch (Exception ex) { Log.Info($"settings icon: {ex.Message}"); }

        Build();
        Content = new ScrollViewer { Content = _page, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape) Close(); };
    }

    // ---------------------------------------------------------------- page

    private void Build()
    {
        _page.Children.Add(new TextBlock
        {
            Text = "Changes are saved and applied straight away. Every option is also in settings.json.",
            Opacity = 0.75,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 8, 0, 4),
        });
        var openFile = new Button { Content = "Open settings.json", HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 4, 0, 0) };
        openFile.Click += (_, _) => TrayIcon.OpenSettingsFile();
        _page.Children.Add(openFile);

        Section("Shortcuts");
        Note("Each shortcut opens the switcher. Options set to \"Default\" use the values further down.");
        _page.Children.Add(_shortcutRows);
        EditableKeys.Add("shortcuts");
        foreach (var shortcut in _s.Shortcuts) AddShortcutRow(shortcut);
        var add = new Button { Content = "Add shortcut", HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 8, 0, 0) };
        add.Click += (_, _) =>
        {
            AddShortcutRow(new Shortcut { Keys = "" });
            _shortcuts[^1].Keys.Focus();
        };
        _page.Children.Add(add);

        Section("When and where it appears");
        Choice("Show on", "Which monitors show the switcher.", "showOn", _s.ShowOn,
            (ShowOn.AllMonitors, "Every monitor"),
            (ShowOn.MonitorUnderCursor, "The monitor under the mouse"),
            (ShowOn.MonitorWithActiveWindow, "The monitor with the active window"));
        Toggle("Stay open after Alt is released", "Pick with Enter, a click or the arrow keys; Esc closes. A quick tap still switches straight away.",
            "stayOpenAfterAltRelease", _s.StayOpenAfterAltRelease);
        Number("Show delay (ms)", "Wait this long before showing the switcher, so quick taps switch without it appearing. 0–2000.",
            "showDelayMs", _s.ShowDelayMs, 0, 2000);

        Section("Which windows are listed");
        Choice("List windows from", "", "listWindowsFrom", _s.ListWindowsFrom,
            (ListWindowsFrom.AllMonitors, "Every monitor"),
            (ListWindowsFrom.ThisMonitor, "Only the monitor the switcher is on"));
        Choice("Virtual desktops", "", "virtualDesktops", _s.VirtualDesktops,
            (VirtualDesktops.Current, "Current desktop only"),
            (VirtualDesktops.All, "All desktops"));
        Choice("Group windows", "Stacked shows one tile per app; Alt+` cycles through its windows.", "groupWindows", _s.GroupWindows,
            (GroupWindows.Separate, "One tile per window"),
            (GroupWindows.Stacked, "One tile per app (stacked)"));
        Lines("Excluded apps", "One per line: an executable name (notepad.exe or notepad), a full path, or an app ID.", "excludedApps", _s.ExcludedApps);
        Lines("Excluded titles", "One per line. Windows whose title contains any of these (ignoring case) are never listed.", "excludedTitles", _s.ExcludedTitles);

        Section("Finding windows");
        Toggle("Type to search", "Typing while the switcher is open filters the tiles by title and app name.", "typeToSearch", _s.TypeToSearch);
        Choice("Search mode", "", "searchMode", _s.SearchMode,
            (SearchMode.Fuzzy, "Fuzzy (\"wtm\" finds Windows Terminal)"),
            (SearchMode.Contains, "Contains (each word as typed)"));
        Choice("Number keys 1–9", "", "numberKeys", _s.NumberKeys,
            (NumberKeys.Switch, "Switch to that tile"),
            (NumberKeys.Select, "Select that tile"),
            (NumberKeys.Off, "Off (type them into the search)"));
        Toggle("Show tile numbers", "Show 1–9 on the first nine tiles.", "showTileNumbers", _s.ShowTileNumbers);
        Toggle("Hover selects", "Moving the mouse over a tile selects it.", "hoverSelects", _s.HoverSelects);

        Section("Closing windows from the switcher");
        Toggle("Delete key closes the selected window", "", "deleteKeyCloses", _s.DeleteKeyCloses);
        Toggle("Middle-click closes a window", "", "middleClickCloses", _s.MiddleClickCloses);
        Toggle("Show a close button on the tile under the mouse", "", "showCloseButton", _s.ShowCloseButton);

        Section("Appearance");
        Choice("Theme", "", "theme", _s.Theme,
            (AppTheme.System, "Same as Windows"),
            (AppTheme.Dark, "Dark"),
            (AppTheme.Light, "Light"));
        Colour("Accent colour", "Selection border. Empty uses Windows' accent colour.", "accentColor", _s.AccentColor, emptyValue: "system");
        Colour("Background colour", "Empty uses the theme's background.", "backgroundColor", _s.BackgroundColor, emptyValue: "");
        Colour("Text colour", "Empty uses the theme's text colour.", "textColor", _s.TextColor, emptyValue: "");
        Toggle("Live previews", "Off shows app icons only, which uses less memory.", "showThumbnails", _s.ShowThumbnails);
        Number("Tile width", "80–600. Tiles shrink a little when there are many windows, then the grid scrolls.", "tileWidth", _s.TileWidth, 80, 600);
        Number("Preview height", "50–400.", "thumbnailHeight", _s.ThumbnailHeight, 50, 400);

        Section("System");
        Toggle("Start with Windows", "Per-user startup entry; no admin rights needed.", "startWithWindows", _s.StartWithWindows);
    }

    // ---------------------------------------------------------------- building blocks

    private void Section(string title) => _page.Children.Add(new TextBlock
    {
        Text = title,
        FontSize = 20,
        FontWeight = FontWeights.SemiBold,
        Margin = new Thickness(0, 28, 0, 6),
    });

    private void Note(string text) => _page.Children.Add(new TextBlock
    {
        Text = text,
        Opacity = 0.75,
        TextWrapping = TextWrapping.Wrap,
        Margin = new Thickness(0, 0, 0, 6),
    });

    /// <summary>A row: label and description on the left, the control on the right.</summary>
    private void Row(string label, string description, FrameworkElement control)
    {
        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 16, 0) };
        text.Children.Add(new TextBlock { Text = label, TextWrapping = TextWrapping.Wrap });
        if (description.Length > 0)
            text.Children.Add(new TextBlock { Text = description, Opacity = 0.7, FontSize = 12, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 0) });
        control.VerticalAlignment = VerticalAlignment.Center;
        AutomationName(control, label);

        var grid = new Grid { Margin = new Thickness(0, 6, 0, 6) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(control, 1);
        grid.Children.Add(text);
        grid.Children.Add(control);
        _page.Children.Add(grid);
    }

    private void Toggle(string label, string description, string key, bool value)
    {
        EditableKeys.Add(key);
        var box = new CheckBox { IsChecked = value, MinWidth = 0 };
        // Checked/Unchecked, not Click: screen readers toggle through UI Automation, which raises no Click.
        box.Checked += (_, _) => SettingsStore.SetValue(key, true);
        box.Unchecked += (_, _) => SettingsStore.SetValue(key, false);
        Row(label, description, box);
    }

    private void Choice<T>(string label, string description, string key, T value, params (T Value, string Text)[] options) where T : struct, Enum
    {
        EditableKeys.Add(key);
        var combo = ComboFor(value, options);
        combo.SelectionChanged += (_, _) =>
        {
            if (combo.SelectedItem is ComboBoxItem { Tag: T chosen }) SettingsStore.SetValue(key, SettingsStore.ToNode(chosen));
        };
        Row(label, description, combo);
    }

    private static ComboBox ComboFor<T>(T value, (T Value, string Text)[] options) where T : struct, Enum
    {
        var combo = new ComboBox { MinWidth = 260 };
        foreach (var (option, text) in options)
        {
            var item = new ComboBoxItem { Content = text, Tag = option };
            combo.Items.Add(item);
            if (EqualityComparer<T>.Default.Equals(option, value)) combo.SelectedItem = item;
        }
        return combo;
    }

    private void Number(string label, string description, string key, double value, double min, double max)
    {
        EditableKeys.Add(key);
        var box = new TextBox { Text = value.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture), Width = 100, TextAlignment = TextAlignment.Right };
        void Commit()
        {
            if (double.TryParse(box.Text, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var number))
            {
                number = Math.Clamp(number, min, max);
                box.Text = number.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture);
                if (Math.Abs(number - value) < 0.001) return;
                value = number;
                SettingsStore.SetValue(key, number);
            }
            else box.Text = value.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture);
        }
        box.LostKeyboardFocus += (_, _) => Commit();
        box.KeyDown += (_, e) => { if (e.Key == Key.Enter) Commit(); };
        Row(label, description, box);
    }

    /// <summary>A "#RRGGBB" box with a swatch; empty means "use the theme" (<paramref name="emptyValue"/> is written).</summary>
    private void Colour(string label, string description, string key, string value, string emptyValue)
    {
        EditableKeys.Add(key);
        string Shown(string v) => v.Equals("system", StringComparison.OrdinalIgnoreCase) ? "" : v;
        var box = new TextBox { Text = Shown(value), Width = 100 };
        var swatch = new Border { Width = 22, Height = 22, CornerRadius = new CornerRadius(4), BorderThickness = new Thickness(1), Margin = new Thickness(0, 0, 8, 0) };
        swatch.SetResourceReference(Border.BorderBrushProperty, SystemColors.ControlDarkBrushKey);
        void Paint() => swatch.Background = Theme.TryParseColor(box.Text, out var c) ? new SolidColorBrush(c) : Brushes.Transparent;
        Paint();
        string last = Shown(value);
        void Commit()
        {
            string text = box.Text.Trim();
            if (text.Length > 0 && !Theme.TryParseColor(text, out _)) { box.Text = last; Paint(); return; }
            if (text.Length > 0 && !text.StartsWith('#')) text = "#" + text;
            box.Text = text;
            Paint();
            if (text == last) return;
            last = text;
            SettingsStore.SetValue(key, text.Length == 0 ? emptyValue : text.ToUpperInvariant());
        }
        box.TextChanged += (_, _) => Paint();
        box.LostKeyboardFocus += (_, _) => Commit();
        box.KeyDown += (_, e) => { if (e.Key == Key.Enter) Commit(); };
        var panel = new StackPanel { Orientation = Orientation.Horizontal };
        panel.Children.Add(swatch);
        panel.Children.Add(box);
        AutomationName(box, label);
        Row(label, description, panel);
    }

    /// <summary>A list setting edited as one entry per line.</summary>
    private void Lines(string label, string description, string key, List<string> value)
    {
        EditableKeys.Add(key);
        var box = new TextBox
        {
            Text = string.Join(Environment.NewLine, value),
            AcceptsReturn = true,
            TextWrapping = TextWrapping.NoWrap,
            MinHeight = 64,
            Width = 260,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        };
        string last = box.Text;
        box.LostKeyboardFocus += (_, _) =>
        {
            if (box.Text == last) return;
            last = box.Text;
            var lines = box.Text.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).ToList();
            SettingsStore.SetValue(key, SettingsStore.ToNode(lines));
        };
        Row(label, description, box);
    }

    private static void AutomationName(DependencyObject element, string name) =>
        System.Windows.Automation.AutomationProperties.SetName(element, name);

    // ---------------------------------------------------------------- shortcuts

    private void AddShortcutRow(Shortcut shortcut)
    {
        var row = new ShortcutRow(shortcut, SaveShortcuts, RemoveShortcut);
        _shortcuts.Add(row);
        _shortcutRows.Children.Add(row.Root);
    }

    private void RemoveShortcut(ShortcutRow row)
    {
        _shortcuts.Remove(row);
        _shortcutRows.Children.Remove(row.Root);
        SaveShortcuts();
    }

    /// <summary>Writes the whole list; rows whose keys don't parse are left out until they do.</summary>
    private void SaveShortcuts()
    {
        var list = _shortcuts.Where(r => r.IsValid).Select(r => r.Shortcut).ToList();
        SettingsStore.SetValue("shortcuts", SettingsStore.ToNode(list));
    }

    /// <summary>One shortcut: its keys, which windows it lists, and optional overrides.</summary>
    private sealed class ShortcutRow
    {
        public Shortcut Shortcut { get; }
        public Border Root { get; }
        public TextBox Keys { get; }
        public bool IsValid => ShortcutKeys.TryParse(Shortcut.Keys, out _);

        private readonly TextBlock _error;
        private readonly Action _save;

        public ShortcutRow(Shortcut shortcut, Action save, Action<ShortcutRow> remove)
        {
            Shortcut = shortcut;
            _save = save;

            Keys = new TextBox { Text = shortcut.Keys, Width = 160 };
            AutomationName(Keys, "Keys");
            _error = new TextBlock { Text = "Not a valid key combination", Foreground = Brushes.IndianRed, FontSize = 12, Visibility = Visibility.Collapsed, Margin = new Thickness(0, 4, 0, 0) };
            string last = shortcut.Keys;
            void CommitKeys()
            {
                string text = Keys.Text.Trim();
                bool ok = ShortcutKeys.TryParse(text, out var parsed);
                _error.Visibility = ok || text.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
                if (!ok || text == last) return;
                Keys.Text = parsed.ToString();
                last = Keys.Text;
                Shortcut.Keys = Keys.Text;
                _save();
            }
            if (shortcut.Keys.Length > 0 && !IsValid) _error.Visibility = Visibility.Visible;
            Keys.LostKeyboardFocus += (_, _) => CommitKeys();
            Keys.KeyDown += (_, e) => { if (e.Key == Key.Enter) CommitKeys(); };

            var windows = ComboFor(shortcut.Windows, new[] { (WindowScope.All, "All windows"), (WindowScope.CurrentApp, "Current app's windows") });
            windows.MinWidth = 190;
            AutomationName(windows, "Windows");
            windows.SelectionChanged += (_, _) =>
            {
                if (windows.SelectedItem is ComboBoxItem { Tag: WindowScope scope }) { Shortcut.Windows = scope; _save(); }
            };

            var removeButton = new Button { Content = "Remove", Margin = new Thickness(8, 0, 0, 0) };
            removeButton.Click += (_, _) => remove(this);

            var top = new StackPanel { Orientation = Orientation.Horizontal };
            top.Children.Add(Labelled("Keys", Keys));
            top.Children.Add(Labelled("Lists", windows));
            top.Children.Add(Labelled("", removeButton));

            var overrides = new WrapPanel { Margin = new Thickness(0, 8, 0, 0) };
            overrides.Children.Add(Override("Show on", shortcut.ShowOn, v => Shortcut.ShowOn = v,
                (ShowOn.AllMonitors, "Every monitor"), (ShowOn.MonitorUnderCursor, "Under the mouse"), (ShowOn.MonitorWithActiveWindow, "Active window's")));
            overrides.Children.Add(Override("List from", shortcut.ListWindowsFrom, v => Shortcut.ListWindowsFrom = v,
                (ListWindowsFrom.AllMonitors, "Every monitor"), (ListWindowsFrom.ThisMonitor, "This monitor")));
            overrides.Children.Add(Override("Desktops", shortcut.VirtualDesktops, v => Shortcut.VirtualDesktops = v,
                (VirtualDesktops.Current, "Current"), (VirtualDesktops.All, "All")));
            overrides.Children.Add(Override("Group", shortcut.GroupWindows, v => Shortcut.GroupWindows = v,
                (GroupWindows.Separate, "Separate"), (GroupWindows.Stacked, "Stacked")));
            overrides.Children.Add(StayOpenOverride());

            var body = new StackPanel();
            body.Children.Add(top);
            body.Children.Add(_error);
            body.Children.Add(overrides);
            Root = new Border
            {
                Padding = new Thickness(12),
                Margin = new Thickness(0, 6, 0, 0),
                CornerRadius = new CornerRadius(6),
                BorderThickness = new Thickness(1),
                Child = body,
            };
            Root.SetResourceReference(Border.BorderBrushProperty, SystemColors.ControlDarkBrushKey);
        }

        private static FrameworkElement Labelled(string label, FrameworkElement control)
        {
            var panel = new StackPanel { Margin = new Thickness(0, 0, 12, 0) };
            panel.Children.Add(new TextBlock { Text = label, FontSize = 12, Opacity = 0.7, Margin = new Thickness(0, 0, 0, 4) });
            panel.Children.Add(control);
            return panel;
        }

        /// <summary>A combo with "Default" (null: use the global setting) plus each value.</summary>
        private FrameworkElement Override<T>(string label, T? value, Action<T?> set, params (T Value, string Text)[] options) where T : struct, Enum
        {
            var combo = new ComboBox { MinWidth = 140 };
            AutomationName(combo, label);
            var defaultItem = new ComboBoxItem { Content = "Default", Tag = null };
            combo.Items.Add(defaultItem);
            combo.SelectedItem = defaultItem;
            foreach (var (option, text) in options)
            {
                var item = new ComboBoxItem { Content = text, Tag = option };
                combo.Items.Add(item);
                if (value is { } v && EqualityComparer<T>.Default.Equals(v, option)) combo.SelectedItem = item;
            }
            combo.SelectionChanged += (_, _) =>
            {
                set(combo.SelectedItem is ComboBoxItem { Tag: T chosen } ? chosen : null);
                _save();
            };
            var labelled = Labelled(label, combo);
            labelled.Margin = new Thickness(0, 0, 12, 6);
            return labelled;
        }

        private FrameworkElement StayOpenOverride()
        {
            var combo = new ComboBox { MinWidth = 140 };
            AutomationName(combo, "Stay open");
            foreach (var (tag, text) in new (bool?, string)[] { (null, "Default"), (true, "Yes"), (false, "No") })
            {
                var item = new ComboBoxItem { Content = text, Tag = tag };
                combo.Items.Add(item);
                if (Shortcut.StayOpenAfterAltRelease == tag) combo.SelectedItem = item;
            }
            combo.SelectionChanged += (_, _) =>
            {
                Shortcut.StayOpenAfterAltRelease = (combo.SelectedItem as ComboBoxItem)?.Tag as bool?;
                _save();
            };
            var labelled = Labelled("Stay open", combo);
            labelled.Margin = new Thickness(0, 0, 12, 6);
            return labelled;
        }
    }
}
