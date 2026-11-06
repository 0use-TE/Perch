using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Perch.Animation;
using Perch.Interaction;
using Perch.Platform.Windows;
using Perch.Services;
using Perch.ViewModels;
using Perch.Views.Pages;
using Forms = System.Windows.Forms;

namespace Perch.Views;

public partial class IslandWindow : Window
{
    // While compact, an alert, a running focus timer or network speed widens the island to about 380 DIP.
    private const double WideProgress = (380 - IslandOutline.CompactWidth) / (IslandOutline.ExpandedWidth - IslandOutline.CompactWidth);
    private const int TrayIndex = 7;
    private readonly Store store = new();
    private readonly IslandViewModel model;
    private readonly Reminders reminders;
    private readonly IslandController controller = new();
    private readonly IslandAnimator animator = new();
    private readonly DispatcherTimer clock = new();
    private readonly DispatcherTimer dragWatch = new() { Interval = TimeSpan.FromMilliseconds(250) };
    private readonly UIElement[] pages;
    private readonly Button[] tabs;
    private IslandHost? host;
    private FullscreenWatcher? fullscreen;
    private SettingsWindow? settingsWindow;
    private Forms.NotifyIcon? tray;
    private Forms.ContextMenuStrip? trayMenu;
    private Forms.ToolStripMenuItem? hoverItem, motionItem, menuShowHide, menuSettings, menuLayout, menuExit;
    private int page;
    private double shapeW, shapeH, content;
    private bool largeHost, closing, userHidden;
    private bool Capsule => store.State.Settings.Shape == "capsule";
    // Closed: always black. Open: black at the user's opacity (translucent) when the material is "glass".
    private bool Glass => store.State.Settings.Appearance == "glass";
    private readonly BlurEffect focusBlur = new() { RenderingBias = RenderingBias.Performance };
    private readonly DispatcherTimer outsideWatch = new() { Interval = TimeSpan.FromMilliseconds(60) };
    private readonly DispatcherTimer backdropTimer = new() { Interval = TimeSpan.FromMilliseconds(100) };
    private readonly DispatcherTimer menuWatch = new() { Interval = TimeSpan.FromMilliseconds(40) };
    private ContextMenu? openMenu;
    private bool menuOpen, backdropReady;
    private (double?, double?, bool) appliedWeather;
    private (string, int) appliedCalendar = ("", 10);
    private const int HotkeyIslandId = 1, HotkeyNoteId = 2;
    [DllImport("user32.dll")] private static extern nint WindowFromPoint(System.Drawing.Point point);
    [DllImport("user32.dll")] private static extern nint GetAncestor(nint hwnd, uint flags);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint hwnd, out uint processId);
    [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int key);
    private enum HitRegion { None, Compact, Envelope, Expanded }
    private HitRegion region;

    public IslandWindow()
    {
        Loc.Apply(store.State.Settings.Language);
        ThemeManager.Apply(store.State.Settings.Theme);
        Loc.Changed += OnLanguageChanged;
        model = new IslandViewModel(store);
        reminders = new Reminders(store, model.Alerts);
        InitializeComponent();
        DataContext = model;
        pages = [HomePage, MusicPanel, FocusPanel, TodoPanel, NotesPanel, InfoPanel, AppsPanel, TrayPanel, PhotosPanel, CalendarPanel, ClipboardPanel, SystemPanel];
        tabs = [HomeTab, MusicTab, FocusTab, TodoTab, NoteTab, InfoTab, AppsTab, TrayTab, PhotosTab, CalendarTab, ClipboardTab, SystemTab];
        ExpandedContent.Margin = new Thickness(IslandOutline.PadX, 0, 0, 0);
        var settings = store.State.Settings;
        controller.HoverEnabled = settings.HoverExpand;
        animator.ReducedMotion = settings.ReducedMotion ?? false;
        controller.CollapseOnLeave = settings.CollapseOnLeave;
        outsideWatch.Tick += OnOutsideWatch;
        menuWatch.Tick += OnMenuWatch;
        backdropTimer.Tick += (_, _) => RefreshBackdrop();
        controller.ExpansionChanged += _ => UpdateShape();
        model.ShapeChanged += UpdateShape;
        animator.Frame += Draw;
        dragWatch.Tick += (_, _) => { if (!CursorOverIsland()) { dragWatch.Stop(); controller.Pointer(false); } };
        SourceInitialized += OnSourceReady;
        PreviewMouseRightButtonUp += OnContextMenu;
        PreviewMouseDown += OnInputClick;
        // Any text box on any page puts the island into editing mode so it stays open while typing.
        AddHandler(Keyboard.GotKeyboardFocusEvent, new KeyboardFocusChangedEventHandler((_, e) => { if (e.NewFocus is TextBox) controller.Edit(true); }), true);
        Deactivated += (_, _) => { host?.Passive(); Keyboard.ClearFocus(); controller.Edit(false); };
        clock.Tick += OnClock;
        IsVisibleChanged += (_, _) => { if (IsVisible) RefreshClock(); else clock.Stop(); };
        Closed += OnClosed;
        StartupEntry.Apply(settings.LaunchAtStartup);
    }

    private void OnSourceReady(object? sender, EventArgs e)
    {
        host = new(this);
        ApplyPages();
        host.DisplayChanged += Reposition;
        host.HotKeyPressed += OnHotKey;
        host.ClipboardUpdated += () => { if (store.State.Settings.ShowClipboard) model.Clipboard.Capture(); };
        RegisterHotkeys();
        fullscreen = new FullscreenWatcher(new WindowInteropHelper(this).Handle);
        fullscreen.Changed += ApplyVisibility;
        ApplyLook();
        PlaceHost();
        Draw(0, 0, true);
        CreateTray();
        RefreshClock();
        UpdateShape();
    }

    // Open: full size. Closed: compact, or wide compact while something is live.
    private void UpdateShape()
    {
        var expanded = controller.State != InteractionState.Compact;
        if (!expanded) HideMore();
        var w = expanded ? 1 : model.IsWide ? WideProgress : 0;
        var h = expanded ? 1.0 : 0.0;
        if ((w > 0 || h > 0) && !largeHost)
        {
            largeHost = true;
            PlaceHost();
            Draw(shapeW, shapeH, false);
        }
        UpdateOutsideWatch();
        PinGlyph.Foreground = controller.State == InteractionState.Pinned ? (Brush)FindResource("IslandAccent") : (Brush)FindResource("IslandMuted");
        PinButton.ToolTip = Loc.T(controller.State == InteractionState.Pinned ? "Nav.Unpin" : "Nav.Pin");
        animator.MoveTo(w, h);
    }

    // Click-to-close mode: while open, a mouse press outside the island collapses it.
    private void UpdateOutsideWatch()
    {
        var needed = !controller.CollapseOnLeave && controller.State is InteractionState.Expanded or InteractionState.Editing;
        if (needed) outsideWatch.Start(); else outsideWatch.Stop();
    }

    private void OnOutsideWatch(object? sender, EventArgs e)
    {
        if (menuOpen || (GetAsyncKeyState(0x01) & 0x8000) == 0 || CursorOverIsland()) return;
        Keyboard.ClearFocus();
        controller.Collapse();
    }

    // The island never takes focus, so WPF can't see clicks elsewhere: while a menu is open, any mouse press outside it closes it.
    private void OnMenuWatch(object? sender, EventArgs e)
    {
        if (openMenu is not { IsOpen: true } menu) { menuWatch.Stop(); return; }
        var pressed = IsDown(0x01) || IsDown(0x02) || IsDown(0x04);
        if (!pressed || CursorOverOwnPopup()) return;
        menu.IsOpen = false;
    }

    private static bool IsDown(int key) => ((ushort)GetAsyncKeyState(key) & 0x8000) != 0;

    // True when the window under the cursor is one of this process's popups (the menu or its submenus), not a normal window.
    private bool CursorOverOwnPopup()
    {
        var cursor = Forms.Control.MousePosition;
        var root = GetAncestor(WindowFromPoint(cursor), 2);
        if (root == 0) return false;
        GetWindowThreadProcessId(root, out var pid);
        if (pid != (uint)Environment.ProcessId) return false;
        var self = new WindowInteropHelper(this).Handle;
        var settingsHandle = settingsWindow == null ? 0 : new WindowInteropHelper(settingsWindow).Handle;
        return root != self && root != settingsHandle;
    }

    // The stage only changes size at two moments; during animation only the mask moves, so no layout runs per frame.
    private void PlaceHost()
    {
        var width = largeHost ? IslandOutline.HostWidth : IslandOutline.CompactWidth;
        var height = largeHost ? IslandOutline.HostHeight : IslandOutline.CompactHostHeight;
        host?.Place(width, height);
        Shell.Width = width; Shell.Height = height;
        region = HitRegion.None;
    }

    private void Draw(double w, double h, bool settled)
    {
        shapeW = Math.Clamp(w, 0, 1.04);
        shapeH = Math.Clamp(h, 0, 1.03);
        if (settled && shapeW == 0 && shapeH == 0 && largeHost)
        {
            largeHost = false;
            PlaceHost();
        }
        var width = IslandOutline.CompactWidth + (IslandOutline.ExpandedWidth - IslandOutline.CompactWidth) * shapeW;
        var height = IslandOutline.CompactHeight + (IslandOutline.ExpandedHeight - IslandOutline.CompactHeight) * shapeH;
        var stageWidth = largeHost ? IslandOutline.HostWidth : IslandOutline.CompactWidth;
        var outline = IslandOutline.Create((stageWidth - width) / 2, width, height, Math.Clamp(shapeH, 0, 1), Capsule);
        ShellOutline.Data = outline;
        ShellContent.Clip = outline;
        // Koko's backdrop animation state: solid black when small, increasingly translucent as it opens.
        if (Glass) IslandLook.SetGlassFill(ShellOutline, store.State.Settings, Math.Clamp(shapeH, 0, 1));
        // Frosted backdrop: only while (nearly) open; closed it is plain black and nothing is captured.
        var frosted = Glass && backdropReady && shapeH > 0.04;
        ShellBackdrop.Visibility = frosted ? Visibility.Visible : Visibility.Collapsed;
        if (frosted)
        {
            ShellBackdrop.Data = outline;
            if (!backdropTimer.IsEnabled) { RefreshBackdrop(); backdropTimer.Start(); }
        }
        else backdropTimer.Stop();

        // Content follows height: fades in over the second half of expanding, gone within the first 30% of collapsing.
        // The rate limit keeps a mid-flight reversal from making the opacity jump.
        var target = animator.Expanding ? Math.Clamp((shapeH - 0.45) / 0.55, 0, 1) : Math.Clamp((shapeH - 0.70) / 0.30, 0, 1);
        var limit = animator.StepSeconds * 8;
        content = settled ? target : Math.Clamp(target, content - limit, content + limit);
        // Koko's per-section animation state: the nav row leads, the page trails, and the whole block sharpens from a blur.
        ExpandedContent.Opacity = Math.Clamp(content * 1.7, 0, 1);
        PageStage.Opacity = Math.Clamp((content - 0.25) / 0.75, 0, 1);
        var blur = (1 - content) * 7;
        if (blur > 0.4 && !animator.ReducedMotion) { focusBlur.Radius = blur; ExpandedContent.Effect = focusBlur; }
        else ExpandedContent.Effect = null;
        ExpandedContent.IsHitTestVisible = animator.Expanding && Math.Min(shapeW, shapeH) > 0.94;
        ContentOffset.Y = 6 * (1 - content);
        // The compact header stretches with the wide island and fades as the island grows taller.
        CompactHeader.Width = Math.Max(176, width - 72);
        CompactHeader.Opacity = 1 - Math.Clamp(shapeH * 4, 0, 1);
        CompactHeader.IsHitTestVisible = shapeH < 0.25;

        // The native region changes once per state, not per frame. While moving it covers the whole padded host,
        // which contains every intermediate shape.
        var next = !settled ? HitRegion.Envelope : shapeH > 0.5 ? HitRegion.Expanded : HitRegion.Compact;
        if (next == region || host == null) return;
        if (next == HitRegion.Envelope && !largeHost) return; // compact host is about to grow; UpdateShape() redraws
        region = next;
        host.Region(next == HitRegion.Envelope ? IslandOutline.Create(0, IslandOutline.HostWidth, IslandOutline.HostHeight, 1) : outline, 0);
    }

    // Form (notch / floating capsule) and appearance (solid black / frosted glass) are settings, applied live.
    private void ApplyLook()
    {
        var s = store.State.Settings;
        var gap = Capsule ? IslandOutline.CapsuleGap : 0;
        CompactHeader.Margin = new Thickness(0, gap, 0, 0);
        ExpandedContent.Margin = new Thickness(IslandOutline.PadX, gap, 0, 0);
        IslandLook.Apply(ShellOutline, ShellGrain, ShellSheen, Glass);
        backdropReady = host != null && host.ExcludeFromCapture(Glass);
        region = HitRegion.None;
        PlaceHost();
        Draw(shapeW, shapeH, !animator.IsRunning);
    }

    // Samples the screen behind the island, blurs it and paints it under the black tint: a frosted-glass texture.
    private void RefreshBackdrop()
    {
        if (host == null || !backdropReady || !IsVisible) return;
        var picture = BackdropCapture.Grab(host.ScreenRect());
        if (picture == null) return;
        ShellBackdrop.Fill = new ImageBrush(picture)
        {
            Stretch = Stretch.Fill,
            TileMode = TileMode.None,
            ViewportUnits = BrushMappingMode.Absolute,
            Viewport = new Rect(0, 0, Shell.Width, Shell.Height),
        };
    }

    private void OnContextMenu(object sender, MouseButtonEventArgs e)
    {
        if (ShellOutline.Data is not Geometry outline || !outline.FillContains(e.GetPosition(Stage))) return;
        var s = store.State.Settings;
        MenuItem Item(string key, Action action, bool check = false)
        {
            var item = new MenuItem { Header = Loc.T(key), IsChecked = check, StaysOpenOnClick = false };
            item.Click += (_, _) => { action(); store.Touch(); settingsWindow?.Refresh(); };
            return item;
        }
        MenuItem Group(string key, params MenuItem[] children)
        {
            var group = new MenuItem { Header = Loc.T(key) };
            foreach (var child in children) group.Items.Add(child);
            return group;
        }
        var menu = new ContextMenu { PlacementTarget = this, Placement = System.Windows.Controls.Primitives.PlacementMode.MousePoint };
        menu.Items.Add(Item("Menu.Settings", OpenSettings));
        menu.Items.Add(new Separator());
        menu.Items.Add(Group("Menu.Form",
            Item("Menu.FormStandard", () => { s.Shape = "standard"; ApplyLook(); }, s.Shape != "capsule"),
            Item("Menu.FormCapsule", () => { s.Shape = "capsule"; ApplyLook(); }, s.Shape == "capsule")));
        menu.Items.Add(Group("Menu.Appearance",
            Item("Menu.AppearanceBlack", () => { s.Appearance = "black"; ApplyLook(); }, s.Appearance != "glass"),
            Item("Menu.AppearanceGlass", () => { s.Appearance = "glass"; ApplyLook(); }, s.Appearance == "glass")));
        menu.Items.Add(Group("Menu.Expand",
            Item("Menu.ExpandHover", () => SetHover(true), s.HoverExpand),
            Item("Menu.ExpandClick", () => SetHover(false), !s.HoverExpand)));
        menu.Items.Add(Group("Menu.Collapse",
            Item("Menu.CollapseLeave", () => SetCollapse(true), s.CollapseOnLeave),
            Item("Menu.CollapseClick", () => SetCollapse(false), !s.CollapseOnLeave)));
        var languages = new List<MenuItem> { Item("Settings.LanguageSystem", () => ChangeLanguage(""), s.Language == "") };
        foreach (var option in Loc.Available)
        {
            var code = option.Code;
            var item = new MenuItem { Header = option.Name, IsChecked = s.Language == code };
            item.Click += (_, _) => { ChangeLanguage(code); settingsWindow?.Refresh(); };
            languages.Add(item);
        }
        menu.Items.Add(Group("Menu.Language", languages.ToArray()));
        menu.Items.Add(new Separator());
        menu.Items.Add(Item("Menu.Exit", () => System.Windows.Application.Current.Shutdown()));
        // While the menu is open the island must not collapse under it.
        menu.Opened += (_, _) => { menuOpen = true; openMenu = menu; menuWatch.Start(); controller.Edit(true); };
        menu.Closed += (_, _) => { menuOpen = false; openMenu = null; menuWatch.Stop(); controller.Edit(IsActive && IsKeyboardFocusWithin); };
        menu.IsOpen = true;
        if (Glass && backdropReady) MenuFrost.Attach(menu, ThemeManager.IsLight(s.Theme));
        e.Handled = true;
    }

    private void SetCollapse(bool onLeave)
    {
        store.State.Settings.CollapseOnLeave = onLeave;
        controller.CollapseOnLeave = onLeave;
        UpdateOutsideWatch();
    }

    // Everything on the settings window's island page takes effect immediately, not on close.
    private void ApplyLive()
    {
        var s = store.State.Settings;
        controller.HoverEnabled = s.HoverExpand;
        controller.CollapseOnLeave = s.CollapseOnLeave;
        if (Loc.Resolve(s.Language) != Loc.Code) Loc.Apply(s.Language); // language switches immediately, not when the window closes
        animator.ReducedMotion = s.ReducedMotion ?? false;
        ThemeManager.Apply(s.Theme);
        if (trayMenu != null) TrayColors.Apply(trayMenu, ThemeManager.IsLight(s.Theme));
        if (hoverItem != null) hoverItem.Checked = s.HoverExpand;
        if (motionItem != null) motionItem.Checked = animator.ReducedMotion;
        ApplyLook();
        ApplyPages();
        RegisterHotkeys();
        var calendarKey = (s.CalendarUrls, s.CalendarReminder);
        if (calendarKey != appliedCalendar) { appliedCalendar = calendarKey; model.Calendar.SettingsChanged(); }
        var weatherKey = (s.Latitude, s.Longitude, s.Fahrenheit);
        if (weatherKey != appliedWeather) { appliedWeather = weatherKey; model.Weather.SettingsChanged(); }
        UpdateOutsideWatch();
        store.Touch();
    }

    private void SetHover(bool value)
    {
        store.State.Settings.HoverExpand = value;
        controller.HoverEnabled = value;
        if (hoverItem != null) hoverItem.Checked = value;
    }

    private void ChangeLanguage(string code)
    {
        store.State.Settings.Language = code;
        Loc.Apply(code);
        store.Touch();
    }

    private void Reposition() { PlaceHost(); Draw(shapeW, shapeH, !animator.IsRunning); }
    private void OnPointerEnter(object sender, MouseEventArgs e) => controller.Pointer(true);
    private void OnPointerLeave(object sender, MouseEventArgs e) => controller.Pointer(false);

    private void OnInputClick(object sender, MouseButtonEventArgs e)
    {
        var hit = e.OriginalSource as DependencyObject;
        while (hit != null)
        {
            if (hit is TextBox) { host?.BeginEditing(); return; }
            hit = hit is Visual ? VisualTreeHelper.GetParent(hit) : LogicalTreeHelper.GetParent(hit);
        }
    }

    private void OnPin(object sender, RoutedEventArgs e) => controller.TogglePin();
    private void OnHeaderClick(object sender, MouseButtonEventArgs e) => controller.Open();
    private void OnSettings(object sender, RoutedEventArgs e) => OpenSettings();

    private void OnTab(object sender, RoutedEventArgs e)
    {
        Keyboard.ClearFocus();
        FocusManager.SetFocusedElement(this, null);
        controller.Edit(false);
        SelectPage(int.Parse((string)((Button)sender).Tag));
    }

    // ---- global shortcuts (RegisterHotKey on the island window)
    private void RegisterHotkeys()
    {
        if (host == null) return;
        host.UnregisterHotKey(HotkeyIslandId);
        host.UnregisterHotKey(HotkeyNoteId);
        var s = store.State.Settings;
        var taken = new List<string>();
        var clashes = new List<string>();
        if (s.HotkeysEnabled)
        {
            var playerKeys = s.PlayerKeys.SelectMany(k => new[] { k.Play, k.Previous, k.Next }).Where(k => !string.IsNullOrWhiteSpace(k)).Select(k => k.ToLowerInvariant()).ToHashSet();
            void Register(int id, string text)
            {
                if (string.IsNullOrWhiteSpace(text)) return;
                if (playerKeys.Contains(text.ToLowerInvariant())) { clashes.Add(text); return; } // Perch would answer its own key press
                if (!HotkeyParser.TryParse(text, out var modifiers, out var key) || !host.RegisterHotKey(id, modifiers, key)) taken.Add(text);
            }
            Register(HotkeyIslandId, s.HotkeyIsland);
            Register(HotkeyNoteId, s.HotkeyNote);
        }
        var problem = clashes.Count > 0 ? Loc.F("Set.HotkeyClash", string.Join(", ", clashes)) : taken.Count > 0 ? Loc.F("Set.HotkeyTaken", string.Join(", ", taken)) : "";
        settingsWindow?.ShowHotkeyStatus(problem);
    }

    private void OnHotKey(int id)
    {
        if (!IsVisible) return;
        if (id == HotkeyIslandId)
        {
            if (controller.State == InteractionState.Compact) controller.Open(); else controller.Collapse();
        }
        else if (id == HotkeyNoteId)
        {
            controller.Open();
            if (!PageShown()[4]) return;
            SelectPage(4);
            model.Notes.New.Execute(null);
            host?.BeginEditing();
            Dispatcher.BeginInvoke(DispatcherPriority.Input, () => NotesPanel.FocusTitle());
        }
    }

    // Pages can be switched off in settings: their tab and the shortcuts to them on the overview disappear.
    // ---- navigation bar: pinned pages on the bar (the current one always shown), the rest under "More"
    private static readonly string[] NavIds = ["home", "music", "focus", "todo", "notes", "info", "apps", "tray", "photos", "calendar", "clipboard", "system"];
    private readonly List<int> morePages = [];
    private DateTime lastWheel;

    private StackPanel Face(int index) => (StackPanel)tabs[index].Content;

    private void ApplyNav()
    {
        var s = store.State.Settings;
        var shown = PageShown();
        var order = s.NavOrder.Select(id => Array.IndexOf(NavIds, id)).Where(i => i > 0 && shown[i]).Distinct().ToList();
        var bar = order.Where(i => s.NavPinned.Contains(NavIds[i])).ToList();
        var more = order.Where(i => !bar.Contains(i)).ToList();
        if (page > 0 && shown[page] && !bar.Contains(page)) { bar.Add(page); more.Remove(page); }

        NavStack.Children.Clear();
        NavStack.Children.Add(HomeTab);
        for (var k = 0; k < bar.Count; k++)
        {
            var tab = tabs[bar[k]];
            tab.Margin = new Thickness(k == 0 ? 10 : 4, 0, 0, 0);
            NavStack.Children.Add(tab);
        }
        morePages.Clear();
        morePages.AddRange(more);
        MoreButton.Visibility = more.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        NavStack.Children.Add(MoreButton);
        for (var i = 1; i < tabs.Length; i++) Face(i).Children[1].Visibility = i == page ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnMore(object sender, RoutedEventArgs e)
    {
        OnMoreCore();
    }

    private void OnMoreCore()
    {
        if (MoreLayer.Visibility == Visibility.Visible) { HideMore(); return; }
        MoreItems.Children.Clear();
        foreach (var index in morePages)
        {
            var face = Face(index);
            var item = new Button { Style = (Style)FindResource("Tile"), Tag = index.ToString(), Height = 40, MinWidth = 164, Margin = new Thickness(2), HorizontalContentAlignment = HorizontalAlignment.Left };
            item.Click += OnMoreItem;
            System.Windows.Automation.AutomationProperties.SetName(item, ((TextBlock)face.Children[1]).Text);
            item.Content = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(12, 0, 18, 0),
                Children =
                {
                    new TextBlock { Text = ((TextBlock)face.Children[0]).Text, FontFamily = new FontFamily("Segoe Fluent Icons"), FontSize = 16, Width = 22, Foreground = new SolidColorBrush(Color.FromRgb(0xC9, 0xC9, 0xCE)), VerticalAlignment = VerticalAlignment.Center },
                    new TextBlock { Text = ((TextBlock)face.Children[1]).Text, FontSize = 13, Foreground = Brushes.White, Margin = new Thickness(10, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center },
                },
            };
            MoreItems.Children.Add(item);
        }
        MoreItems.Columns = MoreItems.Children.Count > 1 ? 2 : 1;
        // The panel's right edge lines up with the "More" button.
        MorePanel.Margin = new Thickness(0, 60, 0, 0);
        MorePanel.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        var width = MorePanel.DesiredSize.Width;
        var right = MoreButton.TranslatePoint(new Point(MoreButton.ActualWidth, 0), MoreLayer).X;
        MorePanel.Margin = new Thickness(Math.Max(12, Math.Min(right - width, 828 - width)), 60, 0, 0);
        MoreLayer.Visibility = Visibility.Visible;
        if (!animator.ReducedMotion)
        {
            // Opens from the "More" button: a short fade, scale and slide.
            var ease = new QuadraticEase { EasingMode = EasingMode.EaseOut };
            var time = TimeSpan.FromMilliseconds(170);
            MorePanel.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, time));
            MoreScale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(0.92, 1, time) { EasingFunction = ease });
            MoreScale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(0.92, 1, time) { EasingFunction = ease });
            MoreShift.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(-6, 0, time) { EasingFunction = ease });
        }
    }

    private void HideMore() => MoreLayer.Visibility = Visibility.Collapsed;
    private void OnMoreBackdrop(object sender, MouseButtonEventArgs e) { HideMore(); e.Handled = true; }
    private void OnMorePanelDown(object sender, MouseButtonEventArgs e) => e.Handled = true;
    private void OnMoreItem(object sender, RoutedEventArgs e) { HideMore(); SelectPage(int.Parse((string)((Button)sender).Tag)); }

    // The wheel over the bar steps through the pages (Overview first, then in the configured order).
    private void OnNavWheel(object sender, MouseWheelEventArgs e)
    {
        e.Handled = true;
        if ((DateTime.Now - lastWheel).TotalMilliseconds < 140) return; // touchpads send a burst of events
        var shown = PageShown();
        var sequence = new List<int> { 0 };
        sequence.AddRange(store.State.Settings.NavOrder.Select(id => Array.IndexOf(NavIds, id)).Where(i => i > 0 && shown[i]).Distinct());
        var at = sequence.IndexOf(page);
        if (at < 0) return;
        var next = Math.Clamp(at + (e.Delta < 0 ? 1 : -1), 0, sequence.Count - 1);
        if (next == at) return;
        lastWheel = DateTime.Now;
        HideMore();
        SelectPage(sequence[next]);
    }

    private bool[] PageShown()
    {
        var s = store.State.Settings;
        return [true, s.ShowMusic, s.ShowFocus, s.ShowTodo, s.ShowNotes, s.ShowInfo, s.ShowApps, s.ShowTray, s.ShowPhotos, s.ShowCalendar, s.ShowClipboard, s.ShowSystem];
    }

    private void ApplyPages()
    {
        var shown = PageShown();
        HomeWeatherButton.Visibility = shown[5] ? Visibility.Visible : Visibility.Collapsed;
        HomeTodoAdd.Visibility = shown[3] ? Visibility.Visible : Visibility.Collapsed;
        HomeNotesButton.Visibility = shown[4] ? Visibility.Visible : Visibility.Collapsed;
        if (!shown[page]) SelectPage(0);
        ApplyNav();
    }

    private void SelectPage(int index)
    {
        if (!PageShown()[index]) return;
        page = index;
        for (var i = 0; i < pages.Length; i++)
        {
            pages[i].Visibility = i == index ? Visibility.Visible : Visibility.Collapsed;
            tabs[i].Background = i == index ? new SolidColorBrush(Color.FromRgb(38, 38, 38)) : Brushes.Transparent;
            if (i > 0) ((TextBlock)Face(i).Children[0]).Foreground = i == index ? Brushes.White : (Brush)FindResource("IslandMuted");
        }
        if (pages[index] == AppsPanel) model.Launcher.EnsureLoaded();
        if (pages[index] == PhotosPanel) model.Photos.EnsureLoaded();
        animator.Reveal(PageHost, PageOffset);
        ApplyNav();
    }

    // Dragging files over the island opens the file shelf; dropping keeps them there.
    private void OnDragEnter(object sender, DragEventArgs e)
    {
        e.Handled = true;
        if (!store.State.Settings.ShowTray || TrayPage.DraggingOut || !e.Data.GetDataPresent(DataFormats.FileDrop)) { e.Effects = DragDropEffects.None; return; }
        e.Effects = DragDropEffects.Copy;
        if (page != TrayIndex) SelectPage(TrayIndex);
        controller.Open();
        dragWatch.Start();
    }

    private void OnDragOver(object sender, DragEventArgs e)
    {
        e.Effects = store.State.Settings.ShowTray && !TrayPage.DraggingOut && e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private void OnDrop(object sender, DragEventArgs e)
    {
        e.Handled = true;
        if (!store.State.Settings.ShowTray || TrayPage.DraggingOut || e.Data.GetData(DataFormats.FileDrop) is not string[] files) return;
        model.Tray.Add(files);
    }

    // During and after an OLE drag WPF doesn't report the pointer leaving, so the cursor is checked against the outline.
    private bool CursorOverIsland()
    {
        if (!IsVisible || ShellOutline.Data is not Geometry outline) return false;
        var cursor = Forms.Control.MousePosition;
        return outline.FillContains(Stage.PointFromScreen(new Point(cursor.X, cursor.Y)));
    }

    private void OpenSettings()
    {
        if (settingsWindow != null) { settingsWindow.Activate(); return; }
        var s = store.State.Settings;
        var before = (s.City, s.Fahrenheit, s.PhotoFolder, s.Language);
        settingsWindow = new SettingsWindow(s);
        settingsWindow.Applied += ApplyLive;
        settingsWindow.Closed += (_, _) =>
        {
            settingsWindow = null;
            if (!closing) ApplySettings(before.City, before.Fahrenheit, before.PhotoFolder, before.Language);
        };
        settingsWindow.Show();
        settingsWindow.Activate();
    }

    private void ApplySettings(string city, bool fahrenheit, string photoFolder, string language)
    {
        var s = store.State.Settings;
        if (s.Language != language) Loc.Apply(s.Language); // raises Changed, which relabels everything
        ApplyLive();
        model.Alerts.Sound = s.AlertSound;
        StartupEntry.Apply(s.LaunchAtStartup);
        if (s.City != city || s.Fahrenheit != fahrenheit) model.Weather.SettingsChanged();
        if (s.PhotoFolder != photoFolder) model.Photos.SettingsChanged();
        model.Focus.SettingsChanged();
        model.UpdateCompact();
        ApplyVisibility();
        store.Touch();
    }

    private void ApplyVisibility()
    {
        var hide = userHidden || (store.State.Settings.HideInFullscreen && fullscreen?.IsFullscreen == true);
        if (hide && IsVisible) { Hide(); controller.Edit(false); controller.Pointer(false); }
        else if (!hide && !IsVisible) { Show(); Reposition(); }
    }

    private void ToggleVisible()
    {
        if (IsVisible) { userHidden = true; ApplyVisibility(); return; }
        userHidden = false;
        Show();
        Reposition();
    }

    private void OnClock(object? sender, EventArgs e) => RefreshClock();

    private void RefreshClock()
    {
        model.UpdateClock();
        clock.Stop();
        if (!IsVisible) return;
        clock.Interval = TimeSpan.FromMilliseconds(60000 - DateTime.Now.TimeOfDay.TotalMilliseconds % 60000 + 30);
        clock.Start();
    }

    // The multi-size .ico lets Windows pick 16/20/24/32 px for the tray at any DPI.
    private static System.Drawing.Icon LoadTrayIcon()
    {
        try
        {
            using var stream = System.Windows.Application.GetResourceStream(new Uri("pack://application:,,,/Assets/perch.ico"))!.Stream;
            return new System.Drawing.Icon(stream, System.Windows.Forms.SystemInformation.SmallIconSize);
        }
        catch { return System.Drawing.SystemIcons.Application; }
    }

    private void CreateTray()
    {
        trayMenu = new Forms.ContextMenuStrip();
        menuShowHide = new Forms.ToolStripMenuItem("", null, (_, _) => ToggleVisible());
        menuSettings = new Forms.ToolStripMenuItem("", null, (_, _) => OpenSettings());
        menuLayout = new Forms.ToolStripMenuItem();
        Forms.ToolStripMenuItem Toggle(bool initial, Action<bool> changed)
        {
            var item = new Forms.ToolStripMenuItem { Checked = initial, CheckOnClick = true };
            item.CheckedChanged += (_, _) => changed(item.Checked);
            menuLayout.DropDownItems.Add(item);
            return item;
        }
        hoverItem = Toggle(controller.HoverEnabled, value =>
        {
            controller.HoverEnabled = value;
            store.State.Settings.HoverExpand = value;
            store.Touch();
        });
        motionItem = Toggle(animator.ReducedMotion, value =>
        {
            animator.ReducedMotion = value;
            store.State.Settings.ReducedMotion = value;
            store.Touch();
        });
        menuExit = new Forms.ToolStripMenuItem("", null, (_, _) => System.Windows.Application.Current.Shutdown());
        trayMenu.Items.AddRange([menuShowHide, menuSettings, new Forms.ToolStripSeparator(), menuLayout, new Forms.ToolStripSeparator(), menuExit]);
        trayMenu.Opening += (_, _) => controller.Edit(true);
        trayMenu.Closed += (_, _) => controller.Edit(IsActive && IsKeyboardFocusWithin);
        tray = new Forms.NotifyIcon { Icon = LoadTrayIcon(), ContextMenuStrip = trayMenu, Visible = true };
        tray.DoubleClick += (_, _) => ToggleVisible();
        LocalizeTray();
        TrayColors.Apply(trayMenu, ThemeManager.IsLight(store.State.Settings.Theme));
    }

    // The tray menu is WinForms, so it doesn't see the WPF string resources and is relabelled in code.
    private void LocalizeTray()
    {
        if (tray == null) return;
        menuShowHide!.Text = Loc.T("Menu.ShowHide");
        menuSettings!.Text = Loc.T("Menu.Settings");
        menuLayout!.Text = Loc.T("Menu.Layout");
        hoverItem!.Text = Loc.T("Menu.Hover");
        motionItem!.Text = Loc.T("Menu.ReduceMotion");
        menuExit!.Text = Loc.T("Menu.Exit");
        tray.Text = Loc.T("Menu.Tooltip");
    }

    private void OnLanguageChanged()
    {
        model.Relocalize();
        LocalizeTray();
        UpdateShape();
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        if (closing) return;
        closing = true;
        Loc.Changed -= OnLanguageChanged;
        store.Flush();
        clock.Stop(); clock.Tick -= OnClock;
        dragWatch.Stop();
        outsideWatch.Stop(); outsideWatch.Tick -= OnOutsideWatch;
        backdropTimer.Stop();
        menuWatch.Stop(); menuWatch.Tick -= OnMenuWatch;
        reminders.Dispose();
        fullscreen?.Dispose();
        settingsWindow?.Close();
        model.ShapeChanged -= UpdateShape;
        controller.Dispose();
        animator.Frame -= Draw;
        animator.Dispose();
        if (host != null) { host.DisplayChanged -= Reposition; host.Dispose(); }
        if (tray != null) { tray.Visible = false; tray.Dispose(); }
        trayMenu?.Dispose();
        System.Windows.Application.Current.Shutdown();
    }
}
