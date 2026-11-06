using System.Threading;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Perch.Platform.Windows;
using Perch.Services;

namespace Perch.Views;

// Edits the live Settings object directly. Island options are applied as they change (Applied); the rest on close.
public partial class SettingsWindow : Window
{
    private readonly Settings settings;
    private readonly Control[] pages;
    private bool applyQueued;
    private readonly DispatcherTimer placeTimer = new() { Interval = TimeSpan.FromMilliseconds(350) };
    private CancellationTokenSource? placeSearch;
    private bool choosingPlace;

    public event Action? Applied;

    public SettingsWindow(Settings settings)
    {
        this.settings = settings;
        InitializeComponent();
        pages = [PageIsland, PageFeatures, PageReminders, PageGeneral];
        FillLanguages();
        DataContext = settings;
        placeTimer.Tick += OnPlaceTimer;
        ShowPlace();
        BuildNav();
        Loc.Changed += OnLanguageChanged;
        Closed += (_, _) => Loc.Changed -= OnLanguageChanged;
        RefreshPreview();
        ApplyChrome();

        // Bubbled from the switches and segmented choices; sliders and the language list are watched directly.
        AddHandler(Button.ClickEvent, (_, _) => Changed());
        GlassSlider.PropertyChanged += (_, e) => { if (e.Property == RangeBase.ValueProperty) Changed(); };
        LanguageBox.SelectionChanged += (_, _) => Changed();
        Sidebar.AddHandler(ToggleButton.IsCheckedChangedEvent, OnPage);
        PlaceBox.PropertyChanged += (_, e) => { if (e.Property == TextBox.TextProperty) OnPlaceTextChanged(); };
        PlaceResults.SelectionChanged += OnPlaceChosen;
        AddHandler(KeyDownEvent, OnHotkeyKey, RoutingStrategies.Tunnel);
        NavList.AddHandler(PointerPressedEvent, OnNavPress, RoutingStrategies.Tunnel);
        NavList.PointerMoved += OnNavMove;
        NavList.PointerReleased += OnNavUp;
        NavList.PointerCaptureLost += OnNavLost;
        // The caption area (top 52 px) drags the window; our own buttons sit on top of it.
        AddHandler(PointerPressedEvent, OnTitlePress, RoutingStrategies.Bubble);
    }

    private void OnTitlePress(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed || e.GetPosition(this).Y > 52) return;
        for (var hit = e.Source as Visual; hit != null; hit = hit.GetVisualParent())
            if (hit is Button or RadioButton) return;
        BeginMoveDrag(e);
    }

    private void Changed()
    {
        if (fillingLanguages || !IsLoaded || applyQueued) return;
        applyQueued = true;
        // Let the binding write the new value first.
        Dispatcher.UIThread.Post(() =>
        {
            applyQueued = false;
            Applied?.Invoke(); // applies the theme too, so the preview below reads the new brushes
            ApplyChrome();
            RefreshPreview();
        }, DispatcherPriority.Background);
    }

    private void OnLanguageChanged()
    {
        FillLanguages();
        ShowPlace();
        BuildNav();
    }

    // The first entry ("Match Windows") is translated, so the list is rebuilt whenever the language changes.
    // Replacing the items makes the combo box drop its selection and write another value back through the binding,
    // so that churn is fenced off and the real value is restored afterwards.
    private bool fillingLanguages;

    private void FillLanguages()
    {
        var current = settings.Language;
        fillingLanguages = true;
        try
        {
            LanguageBox.ItemsSource = new[] { new LanguageOption("", Loc.T("Settings.LanguageSystem")) }.Concat(Loc.Available).ToList();
            settings.Language = current;
            LanguageBox.SelectedValue = current;
        }
        finally { fillingLanguages = false; }
    }

    // Called when something else (the right-click menu) changed a setting while this window is open.
    public void Refresh()
    {
        DataContext = null;
        DataContext = settings;
        ApplyChrome();
        RefreshPreview();
    }

    // Frosted material: translucent acrylic window. Solid material: an ordinary opaque window in the theme colour.
    private void ApplyChrome()
    {
        var light = ThemeManager.IsLight(settings.Theme);
        var frosted = IslandLook.IsGlass(settings);
        TransparencyLevelHint = frosted ? [WindowTransparencyLevel.AcrylicBlur, WindowTransparencyLevel.Blur] : [WindowTransparencyLevel.None];
        this.Bind(BackgroundProperty, this.GetResourceObservable(frosted ? "Ui.Tint" : "Ui.Fallback"));
        WindowBackdrop.SetBorder(this, light);
    }

    // The preview shows the selected material: closed is always black, open is translucent in glass mode.
    private void RefreshPreview()
    {
        var glass = IslandLook.IsGlass(settings);
        GlassRow.IsEnabled = glass;
        GlassRow.Opacity = glass ? 1 : 0.4;
        var capsule = settings.Shape == "capsule";

        PreviewOutline.Data = IslandOutline.Create(6, IslandOutline.CompactWidth, IslandOutline.CompactHeight, 0, capsule);
        IslandLook.Apply(PreviewOutline, PreviewGrain, PreviewSheen, glass);
        IslandLook.SetGlassFill(PreviewOutline, settings, 0);
        var top = 9 + (capsule ? IslandOutline.CapsuleGap : 0);
        Canvas.SetTop(PreviewText, top);
        Canvas.SetTop(PreviewTime, top);
    }

    private void OnPage(object? sender, RoutedEventArgs e)
    {
        if (e.Source is not RadioButton { GroupName: "page", IsChecked: true } selected || pages == null) return;
        var index = Sidebar.Children.IndexOf(selected);
        for (var i = 0; i < pages.Length; i++) pages[i].IsVisible = i == index;
    }

    private async void OnPickFolder(object? sender, RoutedEventArgs e)
    {
        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = Loc.T("Settings.PickFolder"), AllowMultiple = false });
        if (folders.Count == 0 || folders[0].TryGetLocalPath() is not { } path) return;
        settings.PhotoFolder = path;
        DataContext = null;
        DataContext = settings;
    }

    // ---- navigation bar: which pages stay on the bar, and in what order
    private sealed record NavMeta(string Id, string Glyph, string NameKey);
    public sealed class NavEntry
    {
        public string Id { get; init; } = "";
        public string Glyph { get; init; } = "";
        public string Name { get; init; } = "";
        public bool Pinned { get; set; }
    }

    private static readonly NavMeta[] NavPages =
    [
        new("music", "", "Nav.Music"), new("focus", "", "Nav.Focus"), new("todo", "", "Nav.Todo"), new("notes", "", "Nav.Notes"),
        new("info", "", "Nav.Info"), new("calendar", "", "Nav.Calendar"), new("apps", "", "Nav.Apps"), new("tray", "", "Nav.Tray"),
        new("photos", "", "Nav.Photos"), new("clipboard", "", "Nav.Clipboard"), new("system", "", "Nav.System"),
    ];

    private const int MaxPinned = 7;

    private List<NavEntry> navEntries = [];
    private Point navPress;
    private NavEntry? navPending;
    private IPointer? navPointer;

    private void BuildNav()
    {
        navEntries = settings.NavOrder
            .Select(id => NavPages.FirstOrDefault(p => p.Id == id))
            .Where(p => p != null)
            .Select(p => new NavEntry { Id = p!.Id, Glyph = p.Glyph, Name = Loc.T(p.NameKey), Pinned = settings.NavPinned.Contains(p.Id) })
            .ToList();
        NavList.ItemsSource = navEntries;
    }

    private void OnNavPin(object? sender, RoutedEventArgs e)
    {
        var box = (CheckBox)sender!;
        if (box.DataContext is not NavEntry entry) return;
        if (box.IsChecked == true && settings.NavPinned.Count >= MaxPinned)
        {
            box.IsChecked = false;
            NavStatus.Text = Loc.F("Set.NavMax", MaxPinned);
            NavStatus.IsVisible = true;
            return;
        }
        NavStatus.IsVisible = false;
        settings.NavPinned.Remove(entry.Id);
        if (box.IsChecked == true) settings.NavPinned.Add(entry.Id);
        Changed();
    }

    // A press on a row (not on its switch) may turn into a drag.
    private void OnNavPress(object? sender, PointerPressedEventArgs e)
    {
        navPress = e.GetPosition(NavList);
        navPointer = e.Pointer;
        navPending = null;
        if (!e.GetCurrentPoint(NavList).Properties.IsLeftButtonPressed) return;
        for (var hit = e.Source as Visual; hit != null; hit = hit.GetVisualParent())
        {
            if (hit is CheckBox) return;
            if (hit is Control { DataContext: NavEntry entry }) navPending = entry;
            if (hit == NavList) break;
        }
    }

    // Live-preview drag: the grabbed row follows the pointer, the other rows slide aside to show the order it would take.
    private List<Control> navRows = [];
    private double navRowHeight, navGrab;
    private double[] navTarget = [], navCurrent = [];
    private int navFrom = -1, navTo;
    private bool navDragging, navAnimating;

    private void OnNavMove(object? sender, PointerEventArgs e)
    {
        var position = e.GetPosition(NavList);
        if (!navDragging)
        {
            if (!e.GetCurrentPoint(NavList).Properties.IsLeftButtonPressed || navPending is not { } entry) return;
            if (Math.Abs(position.Y - navPress.Y) < 4 && Math.Abs(position.X - navPress.X) < 4) return;
            BeginNavDrag(entry);
            if (!navDragging) return;
        }
        var count = navRows.Count;
        var top = Math.Clamp(position.Y - navGrab, 0, (count - 1) * navRowHeight);
        navTo = Math.Clamp((int)Math.Round(top / navRowHeight), 0, count - 1);
        ((TranslateTransform)navRows[navFrom].RenderTransform!).Y = top - navFrom * navRowHeight;
        for (var i = 0; i < count; i++)
        {
            if (i == navFrom) continue;
            navTarget[i] = navFrom < navTo && i > navFrom && i <= navTo ? -navRowHeight : navTo < navFrom && i >= navTo && i < navFrom ? navRowHeight : 0;
        }
        SlideRows();
    }

    // Other rows ease toward their target offset, one step per rendered frame.
    private void SlideRows()
    {
        if (navAnimating) return;
        navAnimating = true;
        void Step(TimeSpan _)
        {
            var moving = false;
            for (var i = 0; i < navRows.Count; i++)
            {
                if (i == navFrom || !navDragging) continue;
                var delta = navTarget[i] - navCurrent[i];
                if (Math.Abs(delta) < 0.4) navCurrent[i] = navTarget[i];
                else { navCurrent[i] += delta * 0.3; moving = true; }
                ((TranslateTransform)navRows[i].RenderTransform!).Y = navCurrent[i];
            }
            if (moving && navDragging) TopLevel.GetTopLevel(this)?.RequestAnimationFrame(Step); else navAnimating = false;
        }
        TopLevel.GetTopLevel(this)?.RequestAnimationFrame(Step);
    }

    private void BeginNavDrag(NavEntry entry)
    {
        navPending = null;
        navRows = navEntries.Select((_, i) => NavList.ContainerFromIndex(i)).Where(r => r != null).Select(r => r!).ToList();
        navFrom = navEntries.IndexOf(entry);
        if (navFrom < 0 || navRows.Count != navEntries.Count) return;
        navRowHeight = navRows[navFrom].Bounds.Height;
        navGrab = navPress.Y - navFrom * navRowHeight;
        navTarget = new double[navRows.Count];
        navCurrent = new double[navRows.Count];
        foreach (var row in navRows) row.RenderTransform = new TranslateTransform();
        var grabbed = navRows[navFrom];
        grabbed.ZIndex = 10;
        grabbed.Opacity = 0.96;
        grabbed.Effect = new DropShadowEffect { BlurRadius = 18, OffsetY = 3, Opacity = 0.45, Color = Colors.Black };
        if ((grabbed as ContentPresenter)?.Child is Panel root) root.Background = new SolidColorBrush(Color.FromArgb(0x40, 0x80, 0x80, 0x80));
        navTo = navFrom;
        navDragging = true;
        NavList.Cursor = new Cursor(StandardCursorType.SizeAll);
        navPointer?.Capture(NavList);
    }

    private void OnNavUp(object? sender, PointerReleasedEventArgs e)
    {
        if (!navDragging) { navPending = null; return; }
        FinishNavDrag(true);
    }

    private void OnNavLost(object? sender, PointerCaptureLostEventArgs e)
    {
        if (navDragging) FinishNavDrag(false);
    }

    private void FinishNavDrag(bool commit)
    {
        navDragging = false;
        NavList.Cursor = Cursor.Default;
        navPointer?.Capture(null);
        var from = navFrom;
        var to = navTo;
        if (commit && from >= 0 && to != from) MoveNav(navEntries[from].Id, navEntries[to].Id, to < from);
        else BuildNav();
        navFrom = -1;
    }

    private void MoveNav(string id, string targetId, bool before)
    {
        if (id == targetId) return;
        var order = settings.NavOrder;
        order.Remove(id);
        var at = order.IndexOf(targetId);
        order.Insert(before ? at : at + 1, id);
        BuildNav();
        Changed();
    }

    // ---- weather location: search a city from a list, or ask Windows for the device position
    private void ShowPlace()
    {
        var name = string.IsNullOrWhiteSpace(settings.CityName) ? Loc.T("Set.PlaceNone") : settings.CityName;
        PlaceCurrent.Text = settings.UseDeviceLocation && !string.IsNullOrWhiteSpace(settings.CityName) ? $"{name}  ·  {Loc.T("Set.PlaceAuto")}" : name;
    }

    private void OnPlaceTextChanged()
    {
        if (choosingPlace) return;
        placeTimer.Stop();
        if ((PlaceBox.Text ?? "").Trim().Length < 2) { ShowPlaceResults(null, ""); return; }
        placeTimer.Start();
    }

    private async void OnPlaceTimer(object? sender, EventArgs e)
    {
        placeTimer.Stop();
        placeSearch?.Cancel();
        var cancel = placeSearch = new CancellationTokenSource();
        ShowPlaceResults(null, Loc.T("Set.Searching"));
        try
        {
            var found = await PlaceSearch.SearchAsync(PlaceBox.Text ?? "", Loc.Culture.TwoLetterISOLanguageName, cancel.Token);
            if (cancel.IsCancellationRequested) return;
            ShowPlaceResults(found, found.Count == 0 ? Loc.T("Set.NoMatches") : "");
        }
        catch (OperationCanceledException) { }
        catch (Exception) { if (!cancel.IsCancellationRequested) ShowPlaceResults(null, Loc.T("Set.SearchFailed")); }
    }

    private void ShowPlaceResults(List<Place>? found, string status)
    {
        PlaceResults.ItemsSource = found;
        PlaceResults.IsVisible = found is { Count: > 0 };
        PlaceStatus.Text = status;
        PlaceStatus.IsVisible = status.Length > 0;
    }

    private void OnPlaceChosen(object? sender, SelectionChangedEventArgs e)
    {
        if (PlaceResults.SelectedItem is not Place place) return;
        SetPlace(place.Label, place.Latitude, place.Longitude, follow: false);
        choosingPlace = true;
        PlaceBox.Text = "";
        choosingPlace = false;
        ShowPlaceResults(null, "");
    }

    private void SetPlace(string label, double latitude, double longitude, bool follow)
    {
        settings.UseDeviceLocation = follow;
        settings.CityName = label;
        settings.Latitude = latitude;
        settings.Longitude = longitude;
        settings.City = ""; // the old free-text city is replaced by the picked place
        ShowPlace();
        Changed();
    }

    private async void OnUseLocation(object? sender, RoutedEventArgs e)
    {
        UseLocationButton.IsEnabled = false;
        ShowPlaceResults(null, Loc.T("Set.Locating"));
        var position = await DeviceLocation.GetAsync();
        UseLocationButton.IsEnabled = true;
        if (position is not { } here) { ShowPlaceResults(null, Loc.T("Set.LocationFailed")); return; }
        var name = await PlaceSearch.ReverseAsync(here.Latitude, here.Longitude, Loc.Culture.TwoLetterISOLanguageName)
                   ?? $"{Loc.T("Set.CurrentLocation")} ({PlaceSearch.Coordinates(here.Latitude, here.Longitude)})";
        ShowPlaceResults(null, "");
        SetPlace(name, here.Latitude, here.Longitude, follow: true);
    }

    // ---- shortcut boxes: click, press the combination; Backspace or Delete clears it
    private void OnHotkeyKey(object? sender, KeyEventArgs e)
    {
        if (e.Source is not TextBox { Tag: "hotkey" } box) return;
        e.Handled = true;
        var key = e.Key;
        if (key is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt or Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin or Key.None) return;
        if (key is Key.Back or Key.Delete) box.Text = "";
        else if (e.KeyModifiers == KeyModifiers.None) return; // a shortcut needs at least one modifier
        else box.Text = HotkeyParser.Format(e.KeyModifiers, key);
        Changed();
    }

    public void ShowHotkeyStatus(string text)
    {
        HotkeyStatus.Text = text;
        HotkeyStatus.IsVisible = text.Length > 0;
    }

    private void OnMinimize(object? sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
    private void OnDone(object? sender, RoutedEventArgs e) => Close();

    protected override void OnClosing(WindowClosingEventArgs e)
    {
        settings.WaterMinutes = Math.Clamp(settings.WaterMinutes, 10, 600);
        settings.SitMinutes = Math.Clamp(settings.SitMinutes, 10, 600);
        settings.FocusMinutes = Math.Clamp(settings.FocusMinutes, 1, 180);
        settings.ShortBreakMinutes = Math.Clamp(settings.ShortBreakMinutes, 1, 60);
        settings.LongBreakMinutes = Math.Clamp(settings.LongBreakMinutes, 1, 120);
        if (!TimeSpan.TryParse(settings.Bedtime, out _)) settings.Bedtime = "23:30";
        base.OnClosing(e);
    }
}
