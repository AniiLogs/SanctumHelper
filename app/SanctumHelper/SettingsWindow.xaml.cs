using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Threading;

namespace SanctumHelper;

public partial class SettingsWindow : Window
{
    readonly Settings s;
    readonly LayoutDb db;
    readonly Engine engine;
    readonly KeyHook hook;
    readonly OverlayView preview;
    readonly DispatcherTimer statusTimer;
    bool loading = true;
    bool capturingKey;

    /// <summary>On-screen map styles: (value stored in Settings.MiniMapMode, name, description).
    /// The list lives with the drawing code (OverlayView.MiniMapStyles).</summary>
    static (string value, string name, string description)[] MiniMapStyleList => OverlayView.MiniMapStyles;

    /// <summary>Marker layers: (group, layer key, label, game icon).</summary>
    static readonly (string group, string kind, string label, string icon)[] LayerList =
    [
        ("objectives", "doors", "Orange & blue doors", "door_orange"),
        ("objectives", "nest", "Egg nests", "nest"),
        ("objectives", "key", "Key rooms & their doors", "key"),
        ("objectives", "challenge", "Timed challenge room", "challenge"),
        ("treasure", "legendary", "Legendary sarcophagi", "chest_gold"),
        ("treasure", "luxurious", "Luxurious chests", "chest_purple"),
        ("treasure", "exquisite", "Exquisite chests", "chest_blue"),
        ("treasure", "simple", "Simple chests", "chest_white"),
        ("other", "pot", "Pots & vats", "chest"),
        ("other", "pile", "Piles & fossils", "chest"),
    ];

    static readonly Dictionary<string, (string title, string subtitle)> Pages = new()
    {
        ["Status"] = ("Status", "What Sanctum Helper sees right now."),
        ["Overlay"] = ("Map overlay", "The layout lined up on the in-game map while you hold the overlay key."),
        ["Mini"] = ("On-screen map", "A live map that follows you while the in-game map is closed."),
        ["Markers"] = ("Markers", "Choose what's drawn on every map. Icons are the game's own."),
        ["Detection"] = ("Detection", "How the difficulty, the layout and your position are found."),
        ["General"] = ("General", "Startup, recording and compatibility."),
        ["Layouts"] = ("Layout browser", "Every Lost Sanctum layout with its doors, key rooms and nests."),
        ["About"] = ("About", ""),
    };

    public SettingsWindow(Settings settings, LayoutDb db, Engine engine, KeyHook hook)
    {
        InitializeComponent();
        s = settings; this.db = db; this.engine = engine; this.hook = hook;
        Icon = System.Windows.Media.Imaging.BitmapFrame.Create(new Uri("pack://application:,,,/Assets/app.ico"));

        KeyName.Text = KeyHook.Name(s.HoldKey);
        HowKey.Text = KeyHook.Name(s.HoldKey);
        PadName.Text = Lang.T(Gamepad.Name(s.HoldButton));
        ToggleMode.IsChecked = s.ToggleMode;
        ShowMapImage.IsChecked = s.ShowMapImage;
        MapOpacity.Value = s.MapOpacity;
        MarkerScale.Value = s.MarkerScale;
        ShowLabels.IsChecked = s.ShowLabels;
        ShowPlayer.IsChecked = s.ShowPlayer;
        ShowViewCone.IsChecked = s.ShowViewCone;
        TrackMinimap.IsChecked = s.TrackMinimap;
        IslandMap.IsChecked = s.IslandMap;
        UseGameLog.IsChecked = s.UseGameLog;
        ShowNotifications.IsChecked = s.ShowNotifications;
        SaveDebugFrames.IsChecked = s.SaveDebugFrames;
        StartWithWindows.IsChecked = s.StartWithWindows;
        WindowTitle.Text = s.WindowTitle;
        RecordableOverlay.IsChecked = s.RecordableOverlay;

        foreach (var (value, name, description) in MiniMapStyleList)
        {
            MiniMapStyles.Items.Add(new ListBoxItem
            {
                Tag = value,
                Content = new StackPanel
                {
                    Children =
                    {
                        new TextBlock { Text = Lang.T(name), FontSize = 14, FontWeight = FontWeights.SemiBold },
                        new TextBlock { Text = Lang.T(description), Style = (Style)FindResource("Hint"), Margin = new Thickness(0, 4, 0, 0) },
                    },
                },
            });
        }
        MiniMapStyles.SelectedItem = MiniMapStyles.Items.Cast<ListBoxItem>().FirstOrDefault(i => (string)i.Tag == s.MiniMapMode) ?? MiniMapStyles.Items[0];
        foreach (var (tag, text) in new[] { ("TopRight", "Top right"), ("TopCenter", "Top centre"), ("OverGame", "Over the game's minimap (experimental)") })
            MiniMapCorner.Items.Add(new ComboBoxItem { Content = Lang.T(text), Tag = tag });
        MiniMapCorner.SelectedItem = MiniMapCorner.Items.Cast<ComboBoxItem>().FirstOrDefault(i => (string)i.Tag == s.MiniMapCorner) ?? MiniMapCorner.Items[0];
        MiniMapSize.Value = s.MiniMapSize;
        BigMapZoom.Value = s.BigMapZoom;
        foreach (var (tag, text) in new[] { ("Below", "Below the map"), ("Above", "Above the map"), ("Off", "Off") })
            CaptionPosition.Items.Add(new ComboBoxItem { Content = Lang.T(text), Tag = tag });
        CaptionPosition.SelectedItem = CaptionPosition.Items.Cast<ComboBoxItem>().FirstOrDefault(i => (string)i.Tag == s.CaptionPosition) ?? CaptionPosition.Items[0];
        CaptionLayout.IsChecked = s.CaptionLayout;
        CaptionDifficulty.IsChecked = s.CaptionDifficulty;
        BigMapSize.Value = s.BigMapSize;
        LoadMiniSliders();

        foreach (var (group, kind, label, icon) in LayerList)
        {
            var target = group switch { "objectives" => LayerObjectives, "treasure" => LayerTreasure, _ => LayerOther };
            target.Children.Add(LayerCard(kind, label, icon));
        }

        Difficulty.Items.Add(new ComboBoxItem { Content = Lang.T("Auto"), Tag = "Auto" });
        foreach (var d in db.Difficulties) Difficulty.Items.Add(new ComboBoxItem { Content = Lang.Difficulty(d), Tag = d });
        Difficulty.SelectedItem = Difficulty.Items.Cast<ComboBoxItem>().FirstOrDefault(i => (string)i.Tag == s.Difficulty) ?? Difficulty.Items[0];
        // Game language: Auto reads the game's own setting. App language: Auto = the game's.
        GameLanguage.Items.Add(new ComboBoxItem
        {
            Content = Lang.DetectedGameCode != null ? Lang.F("Auto (detected: {0})", Lang.NameOf(Lang.DetectedGameCode)) : Lang.T("Auto (not detected yet)"),
            Tag = "Auto",
        });
        foreach (var (code, name) in Lang.Languages) GameLanguage.Items.Add(new ComboBoxItem { Content = name, Tag = code });
        GameLanguage.SelectedItem = GameLanguage.Items.Cast<ComboBoxItem>().FirstOrDefault(i => (string)i.Tag == s.GameLanguage) ?? GameLanguage.Items[0];
        Language.Items.Add(new ComboBoxItem { Content = Lang.F("Same as the game ({0})", Lang.NameOf(Lang.GameCode)), Tag = "Auto" });
        foreach (var (code, name) in Lang.Languages) Language.Items.Add(new ComboBoxItem { Content = name, Tag = code });
        Language.SelectedItem = Language.Items.Cast<ComboBoxItem>().FirstOrDefault(i => (string)i.Tag == s.Language) ?? Language.Items[0];
        void LanguagePicked()
        {
            if (loading || Language.SelectedItem is not ComboBoxItem { Tag: string app } || GameLanguage.SelectedItem is not ComboBoxItem { Tag: string game }) return;
            s.Language = app;
            s.GameLanguage = game;
            s.Save();
            (Application.Current as App)?.LanguageChanged();
        }
        Language.SelectionChanged += (_, _) => LanguagePicked();
        GameLanguage.SelectionChanged += (_, _) => LanguagePicked();
        if (engine.OcrMissingLanguage is string missing)
            OcrNote.Text = Lang.F("Windows has no text recognition for the game's language ({0}) installed, so the party screen is read in English. Add it under Windows Settings > Time & language > Language (optional features), or set the difficulty by hand.", missing);
        OcrNote.Visibility = string.IsNullOrEmpty(OcrNote.Text) ? Visibility.Collapsed : Visibility.Visible;
        ForcedLayout.Items.Add(new ComboBoxItem { Content = Lang.T("Auto (detect)"), Tag = 0 });
        foreach (var l in db.Layouts.Where(l => l.Difficulties.Count > 0).OrderBy(l => l.Number))
            ForcedLayout.Items.Add(new ComboBoxItem { Content = $"{l.DisplayTitle}  ({string.Join("/", l.Difficulties.Select(Lang.Difficulty))}, {Lang.F("blue door {0}", Lang.T(l.Compass))})", Tag = l.Number });
        ForcedLayout.SelectedItem = ForcedLayout.Items.Cast<ComboBoxItem>().FirstOrDefault(i => (int)i.Tag == s.ForcedLayout) ?? ForcedLayout.Items[0];

        foreach (var l in db.Layouts.Where(l => l.Difficulties.Count > 0).OrderBy(l => DiffOrder(l)).ThenBy(l => l.Number))
            PreviewLayout.Items.Add(new ComboBoxItem { Content = $"{l.DisplayTitle}  ·  {string.Join(" / ", l.Difficulties.Select(Lang.Difficulty))}  ·  {Lang.F("blue door {0}", Lang.T(l.Compass))}", Tag = l });
        preview = new OverlayView(db, s) { Preview = true };
        PreviewHost.Content = preview;
        PreviewHost.SizeChanged += (_, _) => RenderPreview();
        PreviewLayout.SelectionChanged += (_, _) => RenderPreview();
        PreviewLayout.SelectedIndex = 0;

        foreach (var cb in new[] { ToggleMode, ShowMapImage, ShowLabels, ShowPlayer, ShowViewCone, TrackMinimap, IslandMap, UseGameLog, ShowNotifications, CaptionLayout, CaptionDifficulty, SaveDebugFrames, StartWithWindows, RecordableOverlay })
        {
            cb.Checked += (_, _) => Changed();
            cb.Unchecked += (_, _) => Changed();
        }
        foreach (var sl in new[] { MapOpacity, MarkerScale, MiniMapSize, MiniMapZoom, MiniMapOpacity, BigMapZoom, BigMapSize })
            sl.ValueChanged += (_, _) => Changed();
        MiniMapStyles.SelectionChanged += (_, _) =>
        {
            if (loading || MiniMapStyles.SelectedItem == null) return;
            s.MiniMapMode = SelectedStyle;
            LoadMiniSliders();
            Changed();
        };
        CaptionPosition.SelectionChanged += (_, _) => Changed();
        MiniMapCorner.SelectionChanged += (_, _) =>
        {
            // Picking a corner puts the map back there (it replaces a dragged position).
            if (!loading) s.MiniMapX = s.MiniMapY = null;
            Changed();
        };
        // Placement (dragging on the overlay) resizes the map with the wheel and saves it: pick
        // that up when the user comes back, so this window doesn't write the old size back.
        Activated += (_, _) =>
        {
            if (loading || Math.Abs(MiniMapSize.Value - s.MiniMapSize) < 0.5) return;
            loading = true;
            MiniMapSize.Value = s.MiniMapSize;
            loading = false;
        };
        Difficulty.SelectionChanged += (_, _) => { Changed(); engine.ResetRun(); };
        ForcedLayout.SelectionChanged += (_, _) => { Changed(); engine.ResetRun(); };
        WindowTitle.LostFocus += (_, _) => Changed();
        hook.AnyKeyPressed += OnAnyKey;

        foreach (var key in Pages.Keys)
            if (FindName("Nav_" + key) is RadioButton rb) rb.Checked += (_, _) => ShowPage(key);
        ShowPage("Status");

        // Maximised borderless windows overhang the screen by the resize border: pad it back in.
        StateChanged += (_, _) => Root.Margin = WindowState == WindowState.Maximized ? new Thickness(7) : new Thickness(0);

        // Run state has no change event (it's a timer on screen): refresh while the window is open.
        statusTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        statusTimer.Tick += (_, _) => RefreshStatus();
        statusTimer.Start();
        Closed += (_, _) => { hook.AnyKeyPressed -= OnAnyKey; statusTimer.Stop(); };

        var v = typeof(App).Assembly.GetName().Version;
        About.Text = Lang.F("Version {0}  ·  map data from game build {1}  ·  OCR: {2}", v?.ToString(3) ?? "", db.GameDataVersion, engine.OcrLanguage) + "\n\n" +
                     Lang.T("Unofficial fan tool, not affiliated with Pawprint Studio. It only looks at screen pixels and never touches the game's memory or files.");
        loading = false;
        RefreshStatus();
    }

    string SelectedStyle => (string)((ListBoxItem)MiniMapStyles.SelectedItem).Tag;

    /// <summary>A marker layer toggle: game icon, name and a switch.</summary>
    Border LayerCard(string kind, string label, string icon)
    {
        var sw = new CheckBox { Style = (Style)FindResource("Switch"), IsChecked = s.Layer(kind), Tag = kind };
        sw.Checked += (_, _) => Changed();
        sw.Unchecked += (_, _) => Changed();
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var img = new Image { Source = OverlayView.IconImage(icon), Width = 26, Height = 26, Margin = new Thickness(0, 0, 12, 0), VerticalAlignment = VerticalAlignment.Center };
        RenderOptions.SetBitmapScalingMode(img, BitmapScalingMode.HighQuality);
        var text = new TextBlock { Text = Lang.T(label), Style = (Style)FindResource("RowTitle"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 0) };
        Grid.SetColumn(text, 1);
        Grid.SetColumn(sw, 2);
        grid.Children.Add(img);
        grid.Children.Add(text);
        grid.Children.Add(sw);
        return new Border { Style = (Style)FindResource("Card"), Margin = new Thickness(0, 0, 6, 6), Padding = new Thickness(14, 10, 14, 10), Child = grid };
    }

    IEnumerable<CheckBox> LayerSwitches =>
        new[] { LayerObjectives, LayerTreasure, LayerOther }.SelectMany(g => g.Children.OfType<Border>()).Select(b => ((Grid)b.Child).Children.OfType<CheckBox>().First());

    void ShowPage(string key)
    {
        foreach (var k in Pages.Keys)
            if (FindName("Sec_" + k) is FrameworkElement el) el.Visibility = k == key ? Visibility.Visible : Visibility.Collapsed;
        PageScroll.Visibility = key == "Layouts" ? Visibility.Collapsed : Visibility.Visible;
        PageScroll.ScrollToTop();
        PageTitle.Text = Lang.T(Pages[key].title);
        PageSubtitle.Text = string.IsNullOrEmpty(Pages[key].subtitle) ? "" : Lang.T(Pages[key].subtitle);
        PageSubtitle.Visibility = string.IsNullOrEmpty(Pages[key].subtitle) ? Visibility.Collapsed : Visibility.Visible;
        if (key == "Layouts") Dispatcher.BeginInvoke(RenderPreview, DispatcherPriority.Loaded);
    }

    /// <summary>Zoom and opacity are kept separately for the corner map and the see-through overlay.</summary>
    void LoadMiniSliders()
    {
        bool was = loading;
        loading = true;
        bool overlay = s.MiniMapMode == "Overlay", off = s.MiniMapMode == "Off";
        MiniMapZoom.Value = overlay ? s.OverlayZoom : s.MiniMapZoom;
        MiniMapOpacity.Value = overlay ? s.OverlayOpacity : s.MiniMapOpacity;
        MiniMapCorner.IsEnabled = MiniMapSize.IsEnabled = PositionMiniMap.IsEnabled = !overlay && !off;
        MiniMapZoom.IsEnabled = MiniMapOpacity.IsEnabled = !off;
        loading = was;
    }

    static int DiffOrder(Layout l) => l.Difficulties.Contains("Normal") ? 0 : l.Difficulties.Contains("Hard") ? 1 : 2;

    void Changed()
    {
        if (loading) return;
        s.ToggleMode = ToggleMode.IsChecked == true;
        s.ShowMapImage = ShowMapImage.IsChecked == true;
        s.MapOpacity = Math.Round(MapOpacity.Value, 2);
        s.MarkerScale = Math.Round(MarkerScale.Value, 2);
        s.ShowLabels = ShowLabels.IsChecked == true;
        s.ShowPlayer = ShowPlayer.IsChecked == true;
        s.ShowViewCone = ShowViewCone.IsChecked == true;
        s.TrackMinimap = TrackMinimap.IsChecked == true;
        s.IslandMap = IslandMap.IsChecked == true;
        s.UseGameLog = UseGameLog.IsChecked == true;
        s.ShowNotifications = ShowNotifications.IsChecked == true;
        s.SaveDebugFrames = SaveDebugFrames.IsChecked == true;
        s.StartWithWindows = StartWithWindows.IsChecked == true;
        s.WindowTitle = string.IsNullOrWhiteSpace(WindowTitle.Text) ? "Aniimo" : WindowTitle.Text.Trim();
        s.RecordableOverlay = RecordableOverlay.IsChecked == true;
        s.MiniMapMode = SelectedStyle;
        s.MiniMapCorner = (string)((ComboBoxItem)MiniMapCorner.SelectedItem).Tag;
        s.MiniMapSize = Math.Round(MiniMapSize.Value);
        s.CaptionPosition = (string)((ComboBoxItem)CaptionPosition.SelectedItem).Tag;
        s.CaptionLayout = CaptionLayout.IsChecked == true;
        s.CaptionDifficulty = CaptionDifficulty.IsChecked == true;
        if (s.MiniMapMode == "Overlay") { s.OverlayZoom = Math.Round(MiniMapZoom.Value, 2); s.OverlayOpacity = Math.Round(MiniMapOpacity.Value, 2); }
        else if (s.MiniMapMode != "Off") { s.MiniMapZoom = Math.Round(MiniMapZoom.Value, 2); s.MiniMapOpacity = Math.Round(MiniMapOpacity.Value, 2); }
        s.BigMapZoom = Math.Round(BigMapZoom.Value, 2);
        s.BigMapSize = Math.Round(BigMapSize.Value, 2);
        foreach (var cb in LayerSwitches) s.Layers[(string)cb.Tag] = cb.IsChecked == true;
        s.Difficulty = Difficulty.SelectedItem is ComboBoxItem { Tag: string dv } ? dv : "Auto";
        s.ForcedLayout = (int)((ComboBoxItem)ForcedLayout.SelectedItem).Tag;
        s.Save();
        RenderPreview();
        (Application.Current as App)?.SettingsChanged();
    }

    void RenderPreview()
    {
        if (PreviewLayout.SelectedItem is not ComboBoxItem { Tag: Layout l } || PreviewHost.ActualWidth < 10) return;
        int w = (int)PreviewHost.ActualWidth, h = (int)PreviewHost.ActualHeight;
        preview.Frame = new OverlayFrame(new OpenCvSharp.Rect(0, 0, w, h), true, l, null, null, false, string.Join(" / ", l.Difficulties), "");
        preview.InvalidateVisual();
        var keys = l.KeyRooms.Count();
        PreviewInfo.Text = Lang.F("{0}: blue door {1} of the orange door ({2} m).", l.DisplayTitle, Lang.T(l.Compass), l.DoorVector.Distance.ToString("0")) + " " +
                           (keys == 1 ? Lang.T("1 key room") : Lang.F("{0} key rooms", keys)) + ", " +
                           (l.NestCount == 1 ? Lang.T("1 possible egg nest spot") : Lang.F("{0} possible egg nest spots", l.NestCount)) + ". " +
                           Lang.T("Small gold chests outside key rooms are spots where a legendary sarcophagus can appear.");
    }

    public void RefreshStatus()
    {
        var good = (Brush)FindResource("Good");
        var muted = (Brush)FindResource("Muted");
        bool found = engine.GameFound, inRun = found && engine.InRun;

        GameValue.Text = Lang.T(found ? "Found" : "Not running");
        GameValue.Foreground = found ? good : muted;
        GameSub.Text = Lang.T(found ? "Watching the game window" : "Start Aniimo to begin");

        RunValue.Text = Lang.T(inRun ? "In a run" : "Not in a run");
        RunValue.Foreground = inRun ? good : muted;
        RunSub.Text = Lang.T(inRun ? "Run timer on screen" : "Overlays stay hidden");

        string src = Lang.T(s.Difficulty == "Auto" ? (engine.DetectedDifficulty != null ? "Read from the party screen" : "Open the party screen once") : "Set by hand");
        DiffValue.Text = Lang.Difficulty(engine.EffectiveDifficulty);
        DiffValue.Foreground = engine.EffectiveDifficulty != null ? (Brush)FindResource("Text") : muted;
        DiffSub.Text = src;

        var layout = engine.CurrentLayout;
        LayoutValue.Text = layout?.DisplayTitle ?? Lang.T("Not found yet");
        LayoutValue.Foreground = layout != null ? (Brush)FindResource("Accent") : muted;
        LayoutSub.Text = layout != null ? Lang.F("Blue door {0} · via {1}", Lang.T(layout.Compass), Lang.T(engine.LayoutSource)) : Lang.T(inRun ? "Explore, or open the full map" : "Found during a run");

        SideDot.Fill = inRun ? good : found ? (Brush)FindResource("Accent") : (Brush)FindResource("Faint");
        SideStatus.Text = Lang.T(inRun ? "In an Egg Heist run" : found ? "Aniimo found" : "Waiting for Aniimo…");
        SideLayout.Text = layout != null ? $"{layout.DisplayTitle} · {Lang.Difficulty(engine.EffectiveDifficulty)}" : engine.EffectiveDifficulty is string sd ? Lang.Difficulty(sd) : "";
        SideLayout.Visibility = string.IsNullOrEmpty(SideLayout.Text) ? Visibility.Collapsed : Visibility.Visible;

        StatusDebug.Text = string.IsNullOrEmpty(engine.LastMatchInfo) ? "" : "Last map match: " + engine.LastMatchInfo;
    }

    void ChangeKey_Click(object sender, RoutedEventArgs e)
    {
        if (capturingKey) return;
        capturingKey = true;
        KeyName.Text = Lang.T("Press a key…");
        KeyButton.BorderBrush = (Brush)FindResource("Accent");
    }

    void EndCapture()
    {
        capturingKey = false;
        KeyName.Text = KeyHook.Name(s.HoldKey);
        HowKey.Text = KeyName.Text;
        KeyButton.ClearValue(BorderBrushProperty);
    }

    void OnAnyKey(int vk)
    {
        if (!capturingKey) return;
        if (vk == 0x1B) // Esc cancels
        {
            Dispatcher.BeginInvoke(EndCapture);
            return;
        }
        capturingKey = false;
        Dispatcher.BeginInvoke(() =>
        {
            s.HoldKey = vk;
            hook.Key = vk;
            s.Save();
            EndCapture();
        });
    }

    // ---- Controller button: wait for a newly pressed button (Esc on the keyboard cancels).
    DispatcherTimer? padCapture;

    void ChangePad_Click(object sender, RoutedEventArgs e)
    {
        if (padCapture != null) return;
        var pad = new Gamepad();
        pad.Poll();
        int before = pad.Buttons;
        PadName.Text = Lang.T("Press a button…");
        PadButton.BorderBrush = (Brush)FindResource("Accent");
        var started = DateTime.UtcNow;
        padCapture = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(30) };
        padCapture.Tick += (_, _) =>
        {
            pad.Poll();
            int fresh = pad.Buttons & ~before;
            before &= pad.Buttons; // released buttons count as new next time
            bool esc = (Native.GetAsyncKeyState(0x1B) & 0x8000) != 0;
            if (fresh == 0 && !esc && (DateTime.UtcNow - started).TotalSeconds < 15) return;
            if (fresh != 0)
            {
                int bit = fresh & -fresh; // the lowest one, if several came at once
                if ((fresh & Gamepad.LeftTrigger) != 0) bit = Gamepad.LeftTrigger;
                if ((fresh & Gamepad.RightTrigger) != 0) bit = Gamepad.RightTrigger;
                s.HoldButton = bit;
                s.Save();
            }
            EndPadCapture();
        };
        padCapture.Start();
    }

    void EndPadCapture()
    {
        padCapture?.Stop();
        padCapture = null;
        PadName.Text = Lang.T(Gamepad.Name(s.HoldButton));
        PadButton.ClearValue(BorderBrushProperty);
    }

    void ClearPad_Click(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        s.HoldButton = 0;
        s.Save();
        EndPadCapture();
        e.Handled = true;
    }

    void PositionMiniMap_Click(object sender, RoutedEventArgs e)
    {
        // Placement happens on the game screen: the map gets a frame to drag and scroll.
        (Application.Current as App)?.BeginMiniMapPlacement();
    }

    void NewRun_Click(object sender, RoutedEventArgs e) => engine.ResetRun();

    void OpenLogs_Click(object sender, RoutedEventArgs e)
    {
        Directory.CreateDirectory(Settings.Dir);
        Process.Start(new ProcessStartInfo(Settings.Dir) { UseShellExecute = true });
    }

    void Minimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
    void Close_Click(object sender, RoutedEventArgs e) => Close();
}
