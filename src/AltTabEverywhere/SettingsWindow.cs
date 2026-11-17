using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace AltTabEverywhere;

/// <summary>
/// Every setting in settings.json, editable without touching the file. Built in code and shown
/// in its own process (see <see cref="Launch"/>), so it costs the switcher nothing.
/// Each change is written straight away, one key at a time, so keys edited by hand are kept; the
/// switcher's file watcher then applies it like any other edit.
/// </summary>
internal sealed class SettingsWindow : Window
{
    public const string WindowTitle = "AltTab Everywhere settings";

    // Shared by the Behaviour tab and the per-shortcut options, so both use the same words.
    private static readonly (ShowOn, string)[] ShowOnOptions =
        { (ShowOn.AllMonitors, "All monitors"), (ShowOn.MonitorUnderCursor, "Under the mouse"), (ShowOn.MonitorWithActiveWindow, "With the active window") };
    private static readonly (ListWindowsFrom, string)[] ListFromOptions =
        { (ListWindowsFrom.AllMonitors, "All monitors"), (ListWindowsFrom.ThisMonitor, "This monitor") };
    private static readonly (VirtualDesktops, string)[] DesktopOptions =
        { (VirtualDesktops.Current, "Current desktop"), (VirtualDesktops.All, "All desktops") };
    private static readonly (GroupWindows, string)[] GroupOptions =
        { (GroupWindows.Separate, "One tile per window"), (GroupWindows.Stacked, "One tile per app") };
    private static readonly (SwitcherLayout, string)[] LayoutOptions =
        { (SwitcherLayout.Thumbnails, "Live previews"), (SwitcherLayout.Icons, "Large icons"), (SwitcherLayout.List, "Compact list"), (SwitcherLayout.IconsOnly, "Icons only") };
    private static readonly (SortOrder, string)[] SortOptions =
        { (SortOrder.Recent, "Recently used"), (SortOrder.Stacking, "Front to back on screen"), (SortOrder.App, "By app"), (SortOrder.Title, "By title"), (SortOrder.Monitor, "By monitor") };

    // Windows' accent palette, then neutrals (useful for backgrounds and text).
    private static readonly string[] Palette =
    {
        "FFB900", "FF8C00", "F7630C", "CA5010", "DA3B01", "EF6950", "D13438", "FF4343",
        "E74856", "E81123", "EA005E", "C30052", "E3008C", "BF0077", "C239B3", "9A0089",
        "0078D4", "0063B1", "8E8CD8", "6B69D6", "8764B8", "744DA9", "B146C2", "881798",
        "0099BC", "2D7D9A", "00B7C3", "038387", "00B294", "018574", "00CC6A", "10893E",
        "7A7574", "5D5A58", "68768A", "515C6B", "567C73", "486860", "498205", "107C10",
        "000000", "1C1C1C", "202020", "2D2D2D", "767676", "C8C8C8", "F3F3F3", "FFFFFF",
    };

    private Settings _s;
    private readonly TabControl _tabs = new() { Margin = new Thickness(12, 8, 12, 0), BorderThickness = new Thickness(0), Background = Brushes.Transparent };
    private readonly List<List<string>> _tabKeys = new(); // the settings keys on each tab, for "Reset tab"
    private StackPanel _page = new(); // the tab Build() is filling
    private List<string> _pageKeys = new();
    private StackPanel _shortcutRows = new();
    private readonly List<ShortcutRow> _shortcuts = new();
    private readonly List<Action> _onThemeChanged = new(); // colour swatches showing the theme's default
    private Popup? _openPopup;

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
        // A copy: edits go to the file, and are read back from it.
        _s = SettingsStore.Deserialize(SettingsStore.Serialize(current));

        Title = WindowTitle;
        Width = 760;
        Height = 640;
        MinWidth = 560;
        MinHeight = 400;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        try
        {
            using var stream = typeof(SettingsWindow).Assembly.GetManifestResourceStream("AltTabEverywhere.app.ico");
            if (stream != null) Icon = BitmapFrame.Create(stream, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
        }
        catch (Exception ex) { Log.Info($"settings icon: {ex.Message}"); }

        ApplyWindowTheme();
        Content = Layout();
        Build();
        // The Fluent theme may set its own (Mica) background when the window is shown; ours wins.
        Loaded += (_, _) => ApplyWindowTheme();
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key != Key.Escape) return;
            if (_openPopup is { IsOpen: true }) _openPopup.IsOpen = false;
            else Close();
            e.Handled = true;
        };
    }

    // ---------------------------------------------------------------- theme

    /// <summary>
    /// Uses the switcher's own colours: its background (not Mica, which tints with the wallpaper),
    /// text colour, light or dark controls, and a custom accent if one is set.
    /// </summary>
    private void ApplyWindowTheme()
    {
        var theme = Theme.Rebuild(_s); // this process's copy; the switcher builds its own
#pragma warning disable WPF0001 // Fluent theme: native light/dark controls, loaded only in this process
        var mode = theme.IsDark ? System.Windows.ThemeMode.Dark : System.Windows.ThemeMode.Light;
        if (ThemeMode != mode) ThemeMode = mode;
#pragma warning restore WPF0001
        Background = theme.Background;
        Foreground = theme.Text;

        // A custom accent on the checkboxes. The Fluent controls read these per-control keys (the general accent
        // brushes, and the text box focus line, are resolved inside the theme and stay Windows' accent).
        string[] fill = { "CheckBoxCheckBackgroundFillChecked", "CheckBoxCheckBackgroundStrokeChecked" };
        string[] hover = { "CheckBoxCheckBackgroundFillCheckedPointerOver", "CheckBoxCheckBackgroundStrokeCheckedPointerOver" };
        string[] pressed = { "CheckBoxCheckBackgroundFillCheckedPressed", "CheckBoxCheckBackgroundStrokeCheckedPressed" };
        string[] onAccent = { "CheckBoxCheckGlyphForeground", "CheckBoxCheckGlyphForegroundPressed" };
        foreach (var key in fill.Concat(hover).Concat(pressed).Concat(onAccent)) Resources.Remove(key);
        if (Theme.TryParseColor(_s.AccentColor, out var accent))
        {
            foreach (var key in fill) Resources[key] = Frozen(accent);
            foreach (var key in hover) Resources[key] = Frozen(Color.FromArgb(0xE6, accent.R, accent.G, accent.B));
            foreach (var key in pressed) Resources[key] = Frozen(Color.FromArgb(0xCC, accent.R, accent.G, accent.B));
            foreach (var key in onAccent) Resources[key] = Frozen(Theme.Luminance(accent) > 0.6 ? Colors.Black : Colors.White);
        }
        foreach (var refresh in _onThemeChanged) refresh();
    }

    private static SolidColorBrush Frozen(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }

    // ---------------------------------------------------------------- page

    /// <summary>The tabs, with a footer that stays in view whichever tab is open.</summary>
    private FrameworkElement Layout()
    {
        var note = new TextBlock
        {
            Text = "Changes are saved straight away.",
            Opacity = 0.7,
            TextWrapping = TextWrapping.Wrap,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 16, 0),
        };
        // Resets only the keys on the open tab.
        var reset = new Button { Content = "Reset tab to defaults", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) };
        reset.Click += (_, _) =>
        {
            if (_tabs.SelectedIndex >= 0 && _tabs.SelectedIndex < _tabKeys.Count) Reset(_tabKeys[_tabs.SelectedIndex]);
        };
        var openFile = new Button { Content = "Open settings.json", VerticalAlignment = VerticalAlignment.Center };
        openFile.Click += (_, _) => TrayIcon.OpenSettingsFile();

        var footer = new Grid { Margin = new Thickness(24, 10, 24, 14) };
        footer.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        footer.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        footer.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(reset, 1);
        Grid.SetColumn(openFile, 2);
        footer.Children.Add(note);
        footer.Children.Add(reset);
        footer.Children.Add(openFile);

        var root = new DockPanel();
        DockPanel.SetDock(footer, Dock.Bottom);
        root.Children.Add(footer);
        root.Children.Add(_tabs); // the last child fills the rest
        return root;
    }

    private void Build()
    {
        Section("Shortcuts");
        Note("Each shortcut opens the switcher. Its options default to the Behaviour and Appearance tabs.");
        _shortcutRows = new StackPanel();
        _page.Children.Add(_shortcutRows);
        Track("shortcuts");
        foreach (var shortcut in _s.Shortcuts) AddShortcutRow(shortcut);
        var add = new Button { Content = "Add shortcut", HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 4, 0, 0) };
        add.Click += (_, _) =>
        {
            AddShortcutRow(new Shortcut { Keys = "" });
            _shortcuts[^1].Keys.Focus();
        };
        _page.Children.Add(add);

        Section("Behaviour");
        Choice("Show on", "Monitors that show the switcher.", "showOn", _s.ShowOn, ShowOnOptions);
        Toggle("Stay open after releasing the shortcut", "Pick with Enter, a click or the arrows. Esc closes.",
            "stayOpenAfterAltRelease", _s.StayOpenAfterAltRelease);
        Number("Show delay (ms)", "Quick taps switch without showing it. 0–2000.", "showDelayMs", _s.ShowDelayMs, 0, 2000);
        Choice("List windows from", "", "listWindowsFrom", _s.ListWindowsFrom, ListFromOptions);
        Choice("Virtual desktops", "", "virtualDesktops", _s.VirtualDesktops, DesktopOptions);
        Choice("Group windows", "Pause on a stack, press ` or click its badge to see its windows.", "groupWindows", _s.GroupWindows, GroupOptions);
        Choice("Order", "Alt+Tab still starts on the window you used last.", "sortOrder", _s.SortOrder, SortOptions);
        Number("Open a stack after (ms)", "Pausing on a stack (or Back) this long opens it (or goes back). 0: off. 0–5000.",
            "stackOpenDelayMs", _s.StackOpenDelayMs, 0, 5000);
        Choice("Peek", "Outline the selected window where it is.", "peek", _s.Peek,
            (PeekMode.Outline, "Outline"),
            (PeekMode.Off, "Off"));
        Number("Peek delay (ms)", "Rest on a tile this long first. 0–5000.", "peekDelayMs", _s.PeekDelayMs, 0, 5000);
        Number("Peek outline width", "1–20.", "peekBorderWidth", _s.PeekBorderWidth, 1, 20);
        Colour("Peek colour", "Empty: accent colour.", "peekColor", _s.PeekColor, "", t => t.AccentColor);
        Toggle("Show desktop tile", "A last tile that clears the screen, like Win+D.", "showDesktopTile", _s.ShowDesktopTile);

        Section("Filters");
        Lines("Excluded apps", "One per line: name, path or app ID.", "excludedApps", _s.ExcludedApps);
        Lines("Excluded titles", "One per line. Matches part of a title.", "excludedTitles", _s.ExcludedTitles);

        Section("Navigation");
        Toggle("Type to search", "Typing filters the tiles.", "typeToSearch", _s.TypeToSearch);
        Choice("Search mode", "", "searchMode", _s.SearchMode,
            (SearchMode.Fuzzy, "Fuzzy (\"wtm\" finds Windows Terminal)"),
            (SearchMode.Contains, "Contains each word"));
        Choice("Number keys 1–9", "", "numberKeys", _s.NumberKeys,
            (NumberKeys.Switch, "Switch to that tile"),
            (NumberKeys.Select, "Select that tile"),
            (NumberKeys.Off, "Off (type into the search)"));
        Toggle("Show tile numbers", "", "showTileNumbers", _s.ShowTileNumbers);
        Toggle("Hover selects", "Pointing at a tile selects it.", "hoverSelects", _s.HoverSelects);
        Toggle("Screen reader announcements", "Narrator and others read each tile as it's selected.",
            "screenReaderAnnouncements", _s.ScreenReaderAnnouncements);

        Section("Actions");
        Toggle("Delete key closes the selected window", "", "deleteKeyCloses", _s.DeleteKeyCloses);
        Toggle("Middle-click closes a window", "", "middleClickCloses", _s.MiddleClickCloses);
        Toggle("Close button on hover", "", "showCloseButton", _s.ShowCloseButton);
        SwitcherKeyRow("Move here key", "Switch and bring the window to this monitor.", "moveHereKey", _s.MoveHereKey, "Ctrl+Enter", "Shift+Enter", "Insert", "F2");
        Toggle("Ctrl+click moves here", "Ctrl+click a tile to bring its window to that monitor.", "ctrlClickMovesHere", _s.CtrlClickMovesHere);
        SwitcherKeyRow("Minimize key", "Minimize (or restore) the selected window. Stays open.", "minimizeKey", _s.MinimizeKey, "Ctrl+Down", "F3");
        SwitcherKeyRow("Maximize key", "Switch to the window maximized (or restored).", "maximizeKey", _s.MaximizeKey, "Ctrl+Up", "F5");
        SwitcherKeyRow("Always on top key", "Keep the window above others, or stop.", "alwaysOnTopKey", _s.AlwaysOnTopKey, "Ctrl+T", "F6");
        SwitcherKeyRow("Quit app key", "Close every window of the selected app.", "quitAppKey", _s.QuitAppKey, "Ctrl+Shift+Q", "Ctrl+Q");
        Toggle("Right-click menu on tiles", "All of these actions, with their keys.", "tileMenu", _s.TileMenu);
        Toggle("Drop files on tiles", "Alt+Tab while dragging files, drop on a tile to open them in that app.", "dropFilesOnTiles", _s.DropFilesOnTiles);

        Section("Appearance");
        Choice("Layout", "Only live previews show window contents; the others use less memory.", "layout", _s.Layout, LayoutOptions);
        Choice("Theme", "", "theme", _s.Theme,
            (AppTheme.System, "Same as Windows"),
            (AppTheme.Dark, "Dark"),
            (AppTheme.Light, "Light"));
        Colour("Accent colour", "Selection border. Empty: Windows' accent.", "accentColor", _s.AccentColor, "system", t => t.AccentColor);
        Colour("Background colour", "Empty: theme default.", "backgroundColor", _s.BackgroundColor, "", t => ((SolidColorBrush)t.Background).Color);
        Colour("Text colour", "Empty: theme default.", "textColor", _s.TextColor, "", t => ((SolidColorBrush)t.Text).Color);
        Choice("High contrast", "Windows' contrast-theme colours override the colours above.", "highContrast", _s.HighContrast,
            (HighContrastMode.System, "With a Windows contrast theme"),
            (HighContrastMode.On, "Always"),
            (HighContrastMode.Off, "Never"));
        Toggle("Live previews in tiles", "Live previews layout. Off: app icons, less memory.", "showThumbnails", _s.ShowThumbnails);
        Toggle("Previews of minimized windows", "Their last image, as on the taskbar. Off: their icon.", "minimizedPreviews", _s.MinimizedPreviews);
        Number("Tile width", "Live previews layout. 80–600.", "tileWidth", _s.TileWidth, 80, 600);
        Number("Preview height", "Live previews layout. 50–400.", "thumbnailHeight", _s.ThumbnailHeight, 50, 400);
        Number("Icon size", "Large icons and icons only. 16–128.", "iconSize", _s.IconSize, 16, 128);
        Number("List width", "Compact list. 240–1200.", "listWidth", _s.ListWidth, 240, 1200);
        Toggle("Store app icons", "Use the icon from the app package, as in the Start menu.", "storeAppIcons", _s.StoreAppIcons);
        Toggle("Minimized badge", "A dash on minimized windows.", "minimizedBadge", _s.MinimizedBadge);
        Toggle("Admin badge", "A shield on windows running as administrator.", "adminBadge", _s.AdminBadge);
        Toggle("Not responding badge", "A warning sign on frozen windows.", "notRespondingBadge", _s.NotRespondingBadge);
        Toggle("Always on top badge", "A pin on windows kept above others.", "alwaysOnTopBadge", _s.AlwaysOnTopBadge);
        Toggle("Other desktop badge", "On windows from another virtual desktop.", "otherDesktopBadge", _s.OtherDesktopBadge);

        Section("System");
        Toggle("Start with Windows", "No admin rights needed.", "startWithWindows", _s.StartWithWindows);
        Toggle("Check for updates", "Once a day at startup, from GitHub. Off: never goes online.", "checkForUpdates", _s.CheckForUpdates);
    }

    /// <summary>Writes the default value of each key, then rebuilds the window from the file.</summary>
    private void Reset(IReadOnlyCollection<string> keys)
    {
        var defaults = (JsonObject)SettingsStore.ToNode(new Settings())!;
        SettingsStore.SetValues(keys.Select(k => (k, defaults[k]?.DeepClone())).ToList());
        _s = SettingsStore.LoadOnce();

        int tab = _tabs.SelectedIndex;
        _tabs.Items.Clear();
        _tabKeys.Clear();
        _shortcuts.Clear();
        _onThemeChanged.Clear();
        EditableKeys.Clear();
        Build();
        _tabs.SelectedIndex = Math.Max(0, tab);
        ApplyWindowTheme();
    }

    /// <summary>Saves one key, and keeps this window's copy (and its theme) in step with the file.</summary>
    private void Save(string key, JsonNode? value)
    {
        SettingsStore.SetValue(key, value);
        _s = SettingsStore.LoadOnce();
        if (key is "theme" or "accentColor" or "backgroundColor" or "textColor") ApplyWindowTheme();
        foreach (var row in _shortcuts) row.RefreshDefaults(_s);
    }

    // ---------------------------------------------------------------- building blocks

    /// <summary>Starts a new tab; the rows added after this go on it. Each tab scrolls on its own.</summary>
    private void Section(string title)
    {
        _page = new StackPanel { Margin = new Thickness(12, 12, 20, 16) };
        _pageKeys = new List<string>();
        _tabKeys.Add(_pageKeys);
        var tab = new TabItem
        {
            Header = title,
            Content = new ScrollViewer
            {
                Content = _page,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                Focusable = false, // otherwise pressing Alt shows a focus rectangle round the whole tab
            },
        };
        AutomationName(tab, title);
        _tabs.Items.Add(tab);
    }

    private void Track(string key)
    {
        EditableKeys.Add(key);
        _pageKeys.Add(key);
    }

    private void Note(string text) => _page.Children.Add(new TextBlock
    {
        Text = text,
        Opacity = 0.7,
        TextWrapping = TextWrapping.Wrap,
        Margin = new Thickness(0, 0, 0, 10),
    });

    /// <summary>A card like Windows Settings: label and description on the left, the control on the right.</summary>
    private void Row(string label, string description, FrameworkElement control)
    {
        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 16, 0) };
        text.Children.Add(new TextBlock { Text = label, TextWrapping = TextWrapping.Wrap });
        if (description.Length > 0)
            text.Children.Add(new TextBlock { Text = description, Opacity = 0.7, FontSize = 12, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 0) });
        control.VerticalAlignment = VerticalAlignment.Center;
        AutomationName(control, label);

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(control, 1);
        grid.Children.Add(text);
        grid.Children.Add(control);
        _page.Children.Add(Card(grid, new Thickness(16, 10, 16, 10)));
    }

    private static Border Card(UIElement child, Thickness padding)
    {
        var card = new Border
        {
            Child = child,
            Padding = padding,
            Margin = new Thickness(0, 0, 0, 4),
            CornerRadius = new CornerRadius(6),
            BorderThickness = new Thickness(1),
            MinHeight = 56,
        };
        card.SetResourceReference(Border.BackgroundProperty, "CardBackgroundFillColorDefaultBrush");
        card.SetResourceReference(Border.BorderBrushProperty, "CardStrokeColorDefaultBrush");
        return card;
    }

    private void Toggle(string label, string description, string key, bool value)
    {
        Track(key);
        var box = new CheckBox { IsChecked = value, MinWidth = 0 };
        // Checked/Unchecked, not Click: screen readers toggle through UI Automation, which raises no Click.
        box.Checked += (_, _) => Save(key, true);
        box.Unchecked += (_, _) => Save(key, false);
        Row(label, description, box);
    }

    private void Choice<T>(string label, string description, string key, T value, params (T Value, string Text)[] options) where T : struct, Enum
    {
        Track(key);
        var combo = ComboFor(value, options);
        combo.SelectionChanged += (_, _) =>
        {
            if (combo.SelectedItem is ComboBoxItem { Tag: T chosen }) Save(key, SettingsStore.ToNode(chosen));
        };
        Row(label, description, combo);
    }

    /// <summary>A key pressed inside the switcher (moveHereKey): pick a suggestion or type one; "Off" turns it off.</summary>
    private void SwitcherKeyRow(string label, string description, string key, string value, params string[] suggestions)
    {
        Track(key);
        var combo = new ComboBox { IsEditable = true, MinWidth = 240, Text = string.IsNullOrWhiteSpace(value) ? "Off" : value };
        foreach (var suggestion in suggestions.Append("Off").Distinct()) combo.Items.Add(suggestion);
        var error = new TextBlock { Text = "Use Shift or Ctrl with Enter, arrows or Delete; or Insert, F1–F24, a letter, Off", Foreground = Brushes.IndianRed, Visibility = Visibility.Collapsed, Margin = new Thickness(0, 2, 0, 0) };
        string last = combo.Text;
        void Commit()
        {
            string text = combo.Text.Trim();
            if (text == last) return;
            bool off = text.Length == 0 || text.Equals("off", StringComparison.OrdinalIgnoreCase);
            bool valid = off || SwitcherKey.TryParse(text, out _);
            error.Visibility = valid ? Visibility.Collapsed : Visibility.Visible;
            if (!valid) return;
            last = text;
            Save(key, JsonValue.Create(off ? "off" : text));
        }
        combo.SelectionChanged += (_, _) => Dispatcher.BeginInvoke(new Action(Commit)); // Text updates after the event
        combo.LostKeyboardFocus += (_, _) => Commit();
        combo.KeyDown += (_, e) => { if (e.Key == Key.Enter) Commit(); };
        var stack = new StackPanel();
        stack.Children.Add(combo);
        stack.Children.Add(error);
        AutomationName(combo, label);
        Row(label, description, stack);
    }

    private static ComboBox ComboFor<T>(T value, (T Value, string Text)[] options) where T : struct, Enum
    {
        var combo = new ComboBox { MinWidth = 240 };
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
        Track(key);
        var box = new TextBox { Text = value.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture), Width = 100, TextAlignment = TextAlignment.Right };
        void Commit()
        {
            if (double.TryParse(box.Text, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var number))
            {
                number = Math.Clamp(number, min, max);
                box.Text = number.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture);
                if (Math.Abs(number - value) < 0.001) return;
                value = number;
                Save(key, number);
            }
            else box.Text = value.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture);
        }
        box.LostKeyboardFocus += (_, _) => Commit();
        box.KeyDown += (_, e) => { if (e.Key == Key.Enter) Commit(); };
        Row(label, description, box);
    }

    /// <summary>
    /// A colour: a swatch that opens a palette, and a hex box. Empty means "use the theme"
    /// (<paramref name="emptyValue"/> is written); the swatch then shows the theme's colour.
    /// </summary>
    private void Colour(string label, string description, string key, string value, string emptyValue, Func<Theme, Color> themeColour)
    {
        Track(key);
        string Shown(string v) => v.Equals("system", StringComparison.OrdinalIgnoreCase) ? "" : v;
        var box = new TextBox { Text = Shown(value), Width = 110 };
        AutomationName(box, label);
        var error = new TextBlock { Text = "Use #RRGGBB", Foreground = Brushes.IndianRed, FontSize = 12, Margin = new Thickness(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center, Visibility = Visibility.Collapsed };
        var swatch = new Border { Width = 22, Height = 22, CornerRadius = new CornerRadius(4), BorderThickness = new Thickness(1) };
        swatch.SetResourceReference(Border.BorderBrushProperty, "ControlStrokeColorDefaultBrush");
        var pick = new Button { Content = swatch, Padding = new Thickness(5), MinWidth = 0, MinHeight = 0, Margin = new Thickness(0, 0, 8, 0), ToolTip = "Pick a colour" };
        AutomationName(pick, $"Pick {label.ToLowerInvariant()}");

        void Paint() => swatch.Background = new SolidColorBrush(
            Theme.TryParseColor(box.Text, out var c) ? c : themeColour(Theme.Current));
        Paint();
        _onThemeChanged.Add(Paint);

        string last = Shown(value);
        void Commit(string text)
        {
            text = text.Trim();
            bool ok = text.Length == 0 || Theme.TryParseColor(text, out _);
            error.Visibility = ok ? Visibility.Collapsed : Visibility.Visible;
            if (!ok) return; // keep what was typed so it can be fixed
            if (text.Length > 0 && !text.StartsWith('#')) text = "#" + text;
            text = text.ToUpperInvariant();
            box.Text = text;
            Paint();
            if (text == last) return;
            last = text;
            Save(key, text.Length == 0 ? emptyValue : text);
        }
        box.TextChanged += (_, _) => { if (Theme.TryParseColor(box.Text, out _)) { error.Visibility = Visibility.Collapsed; Paint(); } };
        box.LostKeyboardFocus += (_, _) => Commit(box.Text);
        box.KeyDown += (_, e) => { if (e.Key == Key.Enter) Commit(box.Text); };
        pick.Click += (_, _) => ShowPalette(pick, box.Text, Commit);

        var panel = new StackPanel { Orientation = Orientation.Horizontal };
        panel.Children.Add(error);
        panel.Children.Add(pick);
        panel.Children.Add(box);
        Row(label, description, panel);
    }

    /// <summary>A palette under <paramref name="anchor"/>; picking writes the colour through <paramref name="choose"/> ("" = default).</summary>
    private void ShowPalette(FrameworkElement anchor, string current, Action<string> choose)
    {
        var popup = new Popup { PlacementTarget = anchor, Placement = PlacementMode.Bottom, StaysOpen = false, AllowsTransparency = true, VerticalOffset = 4 };
        var grid = new UniformGrid { Columns = 8 };
        foreach (var hex in Palette)
        {
            var colour = (Color)ColorConverter.ConvertFromString("#" + hex);
            var cell = new Button
            {
                Content = new Border { Width = 20, Height = 20, CornerRadius = new CornerRadius(3), Background = Frozen(colour) },
                Padding = new Thickness(3),
                Margin = new Thickness(1),
                MinWidth = 0,
                MinHeight = 0,
                ToolTip = "#" + hex,
            };
            AutomationName(cell, "#" + hex);
            cell.Click += (_, _) => { popup.IsOpen = false; choose("#" + hex); };
            grid.Children.Add(cell);
        }

        var more = new Button { Content = "More colours…", Margin = new Thickness(0, 0, 8, 0) };
        more.Click += (_, _) =>
        {
            popup.IsOpen = false;
            using var dialog = new System.Windows.Forms.ColorDialog { FullOpen = true, AnyColor = true };
            if (Theme.TryParseColor(current, out var c)) dialog.Color = System.Drawing.Color.FromArgb(c.R, c.G, c.B);
            if (dialog.ShowDialog() == System.Windows.Forms.DialogResult.OK)
                choose($"#{dialog.Color.R:X2}{dialog.Color.G:X2}{dialog.Color.B:X2}");
        };
        var useDefault = new Button { Content = "Default" };
        useDefault.Click += (_, _) => { popup.IsOpen = false; choose(""); };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 8, 0, 0) };
        buttons.Children.Add(more);
        buttons.Children.Add(useDefault);

        var body = new StackPanel();
        body.Children.Add(grid);
        body.Children.Add(buttons);
        var frame = new Border { Child = body, Padding = new Thickness(8), CornerRadius = new CornerRadius(8), BorderThickness = new Thickness(1), Background = Background };
        frame.SetResourceReference(Border.BorderBrushProperty, "SurfaceStrokeColorFlyoutBrush");
        popup.Child = frame;

        // Keyboard: focus starts on the current colour (or the first), arrows move around the grid,
        // Esc closes, and focus goes back to the swatch that opened it.
        KeyboardNavigation.SetDirectionalNavigation(grid, KeyboardNavigationMode.Cycle);
        KeyboardNavigation.SetTabNavigation(frame, KeyboardNavigationMode.Cycle);
        frame.PreviewKeyDown += (_, e) =>
        {
            if (e.Key != Key.Escape) return;
            e.Handled = true;
            popup.IsOpen = false;
        };
        popup.Opened += (_, _) =>
        {
            int at = Array.FindIndex(Palette, hex => string.Equals("#" + hex, current, StringComparison.OrdinalIgnoreCase));
            ((UIElement)grid.Children[Math.Max(0, at)]).Focus();
        };
        popup.Closed += (_, _) => anchor.Focus();
        _openPopup = popup;
        popup.IsOpen = true;
    }

    /// <summary>A list setting edited as one entry per line.</summary>
    private void Lines(string label, string description, string key, List<string> value)
    {
        Track(key);
        var box = new TextBox
        {
            Text = string.Join(Environment.NewLine, value),
            AcceptsReturn = true,
            TextWrapping = TextWrapping.NoWrap,
            MinHeight = 64,
            Width = 260,
            Margin = new Thickness(0, 4, 0, 4),
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        };
        string last = box.Text;
        box.LostKeyboardFocus += (_, _) =>
        {
            if (box.Text == last) return;
            last = box.Text;
            var lines = box.Text.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).ToList();
            Save(key, SettingsStore.ToNode(lines));
        };
        Row(label, description, box);
    }

    private static void AutomationName(DependencyObject element, string name) =>
        System.Windows.Automation.AutomationProperties.SetName(element, name);

    // ---------------------------------------------------------------- shortcuts

    private void AddShortcutRow(Shortcut shortcut)
    {
        var row = new ShortcutRow(shortcut, _s, SaveShortcuts, RemoveShortcut);
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
        Save("shortcuts", SettingsStore.ToNode(list));
    }

    /// <summary>One shortcut: its keys, which windows it lists, and options that override the Behaviour tab.</summary>
    private sealed class ShortcutRow
    {
        public Shortcut Shortcut { get; }
        public Border Root { get; }
        public TextBox Keys { get; }
        public bool IsValid => ShortcutKeys.TryParse(Shortcut.Keys, out _);

        private readonly TextBlock _error;
        private readonly Action _save;
        private readonly List<Action<Settings>> _defaultLabels = new();

        public ShortcutRow(Shortcut shortcut, Settings defaults, Action save, Action<ShortcutRow> remove)
        {
            Shortcut = shortcut;
            _save = save;

            Keys = new TextBox { Text = shortcut.Keys, Width = 160 };
            AutomationName(Keys, "Keys");
            _error = new TextBlock { Text = "Not a valid key combination", Foreground = Brushes.IndianRed, FontSize = 12, Visibility = Visibility.Collapsed, Margin = new Thickness(0, 6, 0, 0) };
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
            windows.MinWidth = 200;
            AutomationName(windows, "Lists");
            windows.SelectionChanged += (_, _) =>
            {
                if (windows.SelectedItem is ComboBoxItem { Tag: WindowScope scope }) { Shortcut.Windows = scope; _save(); }
            };

            var removeButton = new Button { Content = "Remove", VerticalAlignment = VerticalAlignment.Bottom, HorizontalAlignment = HorizontalAlignment.Right };
            removeButton.Click += (_, _) => remove(this);

            var top = new Grid();
            top.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            top.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            top.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            var lists = Labelled("Lists", windows);
            Grid.SetColumn(lists, 1);
            Grid.SetColumn(removeButton, 2);
            top.Children.Add(Labelled("Keys", Keys));
            top.Children.Add(lists);
            top.Children.Add(removeButton);

            var overrides = new WrapPanel { Margin = new Thickness(0, 4, 0, 0) };
            overrides.Children.Add(Override("Show on", shortcut.ShowOn, v => Shortcut.ShowOn = v, s => s.ShowOn, ShowOnOptions));
            overrides.Children.Add(Override("List windows from", shortcut.ListWindowsFrom, v => Shortcut.ListWindowsFrom = v, s => s.ListWindowsFrom, ListFromOptions));
            overrides.Children.Add(Override("Virtual desktops", shortcut.VirtualDesktops, v => Shortcut.VirtualDesktops = v, s => s.VirtualDesktops, DesktopOptions));
            overrides.Children.Add(Override("Group windows", shortcut.GroupWindows, v => Shortcut.GroupWindows = v, s => s.GroupWindows, GroupOptions));
            overrides.Children.Add(StayOpenOverride());
            overrides.Children.Add(Override("Layout", shortcut.Layout, v => Shortcut.Layout = v, s => s.Layout, LayoutOptions));
            overrides.Children.Add(Override("Order", shortcut.SortOrder, v => Shortcut.SortOrder = v, s => s.SortOrder, SortOptions));
            RefreshDefaults(defaults);

            bool anyOverride = shortcut.ShowOn != null || shortcut.ListWindowsFrom != null || shortcut.VirtualDesktops != null
                || shortcut.GroupWindows != null || shortcut.StayOpenAfterAltRelease != null || shortcut.Layout != null || shortcut.SortOrder != null;
            overrides.Visibility = anyOverride ? Visibility.Visible : Visibility.Collapsed;

            // A flat text toggle rather than an Expander, which would draw a second card inside this one.
            var moreText = new TextBlock { VerticalAlignment = VerticalAlignment.Center };
            var chevron = new TextBlock { FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"), FontSize = 10, Margin = new Thickness(8, 1, 0, 0), VerticalAlignment = VerticalAlignment.Center };
            var moreContent = new StackPanel { Orientation = Orientation.Horizontal };
            moreContent.Children.Add(moreText);
            moreContent.Children.Add(chevron);
            var more = new Button
            {
                Content = moreContent,
                Background = Brushes.Transparent,
                BorderThickness = new Thickness(0),
                Padding = new Thickness(6, 4, 6, 4),
                Margin = new Thickness(-6, 8, 0, 0),
                HorizontalAlignment = HorizontalAlignment.Left,
            };
            void ShowState()
            {
                bool open = overrides.Visibility == Visibility.Visible;
                moreText.Text = open ? "Fewer options" : "More options";
                chevron.Text = open ? "" : ""; // chevron up / down
                AutomationName(more, moreText.Text);
            }
            ShowState();
            more.Click += (_, _) =>
            {
                overrides.Visibility = overrides.Visibility == Visibility.Visible ? Visibility.Collapsed : Visibility.Visible;
                ShowState();
            };

            var body = new StackPanel();
            body.Children.Add(top);
            body.Children.Add(_error);
            body.Children.Add(more);
            body.Children.Add(overrides);
            Root = Card(body, new Thickness(16, 12, 16, 12));
            Root.Margin = new Thickness(0, 0, 0, 8);
        }

        /// <summary>Updates each "Default (…)" entry to the Behaviour tab's current value.</summary>
        public void RefreshDefaults(Settings defaults)
        {
            foreach (var refresh in _defaultLabels) refresh(defaults);
        }

        private static FrameworkElement Labelled(string label, FrameworkElement control)
        {
            var panel = new StackPanel { Margin = new Thickness(0, 0, 12, 0) };
            panel.Children.Add(new TextBlock { Text = label, FontSize = 12, Opacity = 0.7, Margin = new Thickness(0, 0, 0, 4) });
            panel.Children.Add(control);
            return panel;
        }

        /// <summary>A combo with "Default (…)" (null: use the Behaviour tab) plus each value.</summary>
        private FrameworkElement Override<T>(string label, T? value, Action<T?> set, Func<Settings, T> globalValue, (T Value, string Text)[] options) where T : struct, Enum
        {
            var combo = new ComboBox { MinWidth = 200 };
            AutomationName(combo, label);
            var defaultItem = new ComboBoxItem { Tag = null };
            _defaultLabels.Add(s => defaultItem.Content = $"Default ({options.First(o => EqualityComparer<T>.Default.Equals(o.Value, globalValue(s))).Text})");
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
            labelled.Margin = new Thickness(0, 6, 12, 0);
            return labelled;
        }

        private FrameworkElement StayOpenOverride()
        {
            var combo = new ComboBox { MinWidth = 200 };
            AutomationName(combo, "Stay open");
            ComboBoxItem? defaultItem = null;
            foreach (var (tag, text) in new (bool?, string)[] { (null, ""), (true, "Yes"), (false, "No") })
            {
                var item = new ComboBoxItem { Content = text, Tag = tag };
                defaultItem ??= item;
                combo.Items.Add(item);
                if (Shortcut.StayOpenAfterAltRelease == tag) combo.SelectedItem = item;
            }
            _defaultLabels.Add(s => defaultItem!.Content = $"Default ({(s.StayOpenAfterAltRelease ? "Yes" : "No")})");
            combo.SelectionChanged += (_, _) =>
            {
                Shortcut.StayOpenAfterAltRelease = (combo.SelectedItem as ComboBoxItem)?.Tag as bool?;
                _save();
            };
            var labelled = Labelled("Stay open", combo);
            labelled.Margin = new Thickness(0, 6, 12, 0);
            return labelled;
        }
    }
}
