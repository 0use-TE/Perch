using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Threading;
using System.Windows.Threading;
using Microsoft.Win32;
using Perch.Platform.Windows;
using Perch.Services;

namespace Perch.Views;

// Edits the live Settings object directly. Island options are applied as they change (Applied); the rest on close.
public partial class SettingsWindow : Window
{
    private readonly Settings settings;
    private readonly FrameworkElement[] pages;
    private bool applyQueued;
    private readonly ResourceDictionary fluent = new();
    private readonly DispatcherTimer placeTimer = new() { Interval = TimeSpan.FromMilliseconds(350) };
    private CancellationTokenSource? placeSearch;
    private bool choosingPlace;

    public event Action? Applied;

    public SettingsWindow(Settings settings)
    {
        // Controls that come from the Windows Fluent theme (combo box, text boxes, scroll bars) follow the light/dark choice too.
        this.settings = settings;
        InitializeComponent();
        Resources.MergedDictionaries.Add(fluent);
        UpdateFluent();
        pages = [PageIsland, PageFeatures, PageReminders, PageGeneral];
        FillLanguages();
        DataContext = settings;
        placeTimer.Tick += OnPlaceTimer;
        ShowPlace();
        BuildNav();
        Loc.Changed += OnLanguageChanged;
        Closed += (_, _) => Loc.Changed -= OnLanguageChanged;
        RefreshPreview();
        // Bubbled from the switches, segmented choices and sliders.
        AddHandler(ButtonBase.ClickEvent, new RoutedEventHandler((_, _) => Changed()));
        AddHandler(RangeBase.ValueChangedEvent, new RoutedPropertyChangedEventHandler<double>((_, _) => Changed()));
        AddHandler(Selector.SelectionChangedEvent, new SelectionChangedEventHandler((_, _) => Changed()));
        SourceInitialized += (_, _) => ApplyChrome();
    }

    private void Changed()
    {
        if (fillingLanguages || !IsLoaded || applyQueued) return;
        applyQueued = true;
        // Let the binding write the new value first.
        Dispatcher.BeginInvoke(DispatcherPriority.Background, () =>
        {
            applyQueued = false;
            Applied?.Invoke(); // applies the theme too, so the preview below reads the new brushes
            UpdateFluent();
            ApplyChrome();
            ResourceRefresh.Run(this); // live theme change: re-read every brush
            RefreshPreview();
        });
    }

    private void OnLanguageChanged()
    {
        ResourceRefresh.Run(this);
        FillLanguages();
        ShowPlace();
        BuildNav();
    }

    // The first entry ("Match Windows") is translated, so the list is rebuilt whenever the language changes.
    // Replacing the items makes the combo box drop its selection and write null/another value back through the binding,
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
        UpdateFluent();
        ResourceRefresh.Run(this);
        ApplyChrome();
        RefreshPreview();
    }

    private void UpdateFluent()
    {
        var name = ThemeManager.IsLight(settings.Theme) ? "Light" : "Dark";
        var source = new Uri($"pack://application:,,,/PresentationFramework.Fluent;component/Themes/Fluent.{name}.xaml");
        if (fluent.Source != source) fluent.Source = source;
    }

    // Frosted material: translucent acrylic window. Solid material: an ordinary opaque window in the theme colour.
    private void ApplyChrome()
    {
        var light = ThemeManager.IsLight(settings.Theme);
        var frosted = IslandLook.IsGlass(settings) && WindowBackdrop.Apply(this, !light);
        if (!frosted) WindowBackdrop.Apply(this, !light);
        WindowBackdrop.SetAcrylic(this, frosted);
        WindowBackdrop.SetDark(this, !light);
        WindowBackdrop.SetBorder(this, light);
        SetResourceReference(BackgroundProperty, frosted ? "Ui.Tint" : "Ui.Fallback");
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

    private void OnPage(object sender, RoutedEventArgs e)
    {
        if (e.OriginalSource is not RadioButton { GroupName: "page" } selected || pages == null) return;
        var index = Sidebar.Children.IndexOf(selected);
        for (var i = 0; i < pages.Length; i++) pages[i].Visibility = i == index ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnPickFolder(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = Loc.T("Settings.PickFolder") };
        if (dialog.ShowDialog(this) == true)
        {
            settings.PhotoFolder = dialog.FolderName;
            DataContext = null;
            DataContext = settings;
        }
    }

    // ---- navigation bar: which pages stay on the bar, and in what order
    private sealed record NavMeta(string Id, string Glyph, string NameKey);
    public sealed class NavEntry : System.ComponentModel.INotifyPropertyChanged
    {
        public string Id { get; init; } = "";
        public string Glyph { get; init; } = "";
        public string Name { get; init; } = "";
        public bool Pinned { get; set; }
        public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged { add { } remove { } }
    }

    private static readonly NavMeta[] NavPages =
    [
        new("music", "\uE8D6", "Nav.Music"), new("focus", "\uE916", "Nav.Focus"), new("todo", "\uE73A", "Nav.Todo"), new("notes", "\uE70B", "Nav.Notes"),
        new("info", "\uE753", "Nav.Info"), new("calendar", "\uE787", "Nav.Calendar"), new("apps", "\uE71D", "Nav.Apps"), new("tray", "\uE896", "Nav.Tray"),
        new("photos", "\uE91B", "Nav.Photos"), new("clipboard", "\uE77F", "Nav.Clipboard"), new("system", "\uE7F4", "Nav.System"),
    ];

    private const int MaxPinned = 7;

    private List<NavEntry> navEntries = [];
    private Point navPress;
    private NavEntry? navPending;

    private void BuildNav()
    {
        navEntries = settings.NavOrder
            .Select(id => NavPages.FirstOrDefault(p => p.Id == id))
            .Where(p => p != null)
            .Select(p => new NavEntry { Id = p!.Id, Glyph = p.Glyph, Name = Loc.T(p.NameKey), Pinned = settings.NavPinned.Contains(p.Id) })
            .ToList();
        NavList.ItemsSource = navEntries;
    }

    private void OnNavPin(object sender, RoutedEventArgs e)
    {
        var box = (CheckBox)sender;
        if (box.DataContext is not NavEntry entry) return;
        if (box.IsChecked == true && settings.NavPinned.Count >= MaxPinned)
        {
            box.IsChecked = false;
            NavStatus.Text = Loc.F("Set.NavMax", MaxPinned);
            NavStatus.Visibility = Visibility.Visible;
            return;
        }
        NavStatus.Visibility = Visibility.Collapsed;
        settings.NavPinned.Remove(entry.Id);
        if (box.IsChecked == true) settings.NavPinned.Add(entry.Id);
        Changed();
    }

    // A press on a row (not on its switch) may turn into a drag.
    private void OnNavPress(object sender, MouseButtonEventArgs e)
    {
        navPress = e.GetPosition(NavList);
        navPending = IsInside<CheckBox>(e.OriginalSource as DependencyObject) ? null : (sender as FrameworkElement)?.DataContext as NavEntry;
    }

    private static bool IsInside<T>(DependencyObject? node) where T : DependencyObject
    {
        for (; node != null; node = node is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(node) : LogicalTreeHelper.GetParent(node))
            if (node is T) return true;
        return false;
    }

    // Live-preview drag: the grabbed row follows the pointer, the other rows slide aside to show the order it would take.
    private List<FrameworkElement> navRows = [];
    private double navRowHeight, navGrab;
    private int navFrom = -1, navTo;
    private bool navDragging;

    private void OnNavMove(object sender, MouseEventArgs e)
    {
        var y = e.GetPosition(NavList).Y;
        if (!navDragging)
        {
            if (e.LeftButton != MouseButtonState.Pressed || navPending is not { } entry) return;
            if (Math.Abs(y - navPress.Y) < SystemParameters.MinimumVerticalDragDistance && Math.Abs(e.GetPosition(NavList).X - navPress.X) < SystemParameters.MinimumHorizontalDragDistance) return;
            BeginNavDrag(entry);
            if (!navDragging) return;
        }
        var count = navRows.Count;
        var top = Math.Clamp(y - navGrab, 0, (count - 1) * navRowHeight);
        navTo = Math.Clamp((int)Math.Round(top / navRowHeight), 0, count - 1);
        ((TranslateTransform)navRows[navFrom].RenderTransform).Y = top - navFrom * navRowHeight;
        for (var i = 0; i < count; i++)
        {
            if (i == navFrom) continue;
            var shift = navFrom < navTo && i > navFrom && i <= navTo ? -navRowHeight : navTo < navFrom && i >= navTo && i < navFrom ? navRowHeight : 0;
            var move = (TranslateTransform)navRows[i].RenderTransform;
            if (Math.Abs(move.Y - shift) < 0.5 && move.GetAnimationBaseValue(TranslateTransform.YProperty) is double now && Math.Abs(now - shift) < 0.5) continue;
            move.BeginAnimation(TranslateTransform.YProperty, new System.Windows.Media.Animation.DoubleAnimation(shift, TimeSpan.FromMilliseconds(130)) { EasingFunction = new System.Windows.Media.Animation.CubicEase { EasingMode = System.Windows.Media.Animation.EasingMode.EaseOut } });
        }
    }

    private void BeginNavDrag(NavEntry entry)
    {
        navPending = null;
        navRows = navEntries.Select(n => NavList.ItemContainerGenerator.ContainerFromItem(n) as FrameworkElement).Where(r => r != null).Select(r => r!).ToList();
        navFrom = navEntries.IndexOf(entry);
        if (navFrom < 0 || navRows.Count != navEntries.Count) return;
        navRowHeight = navRows[navFrom].ActualHeight;
        navGrab = navPress.Y - navFrom * navRowHeight;
        foreach (var row in navRows) row.RenderTransform = new TranslateTransform();
        var grabbed = navRows[navFrom];
        Panel.SetZIndex(grabbed, 10);
        grabbed.Opacity = 0.96;
        grabbed.Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 18, ShadowDepth = 3, Opacity = 0.45, Color = Colors.Black };
        if (VisualTreeHelper.GetChild(grabbed, 0) is Panel root) root.Background = new SolidColorBrush(Color.FromArgb(0x40, 0x80, 0x80, 0x80));
        navTo = navFrom;
        navDragging = true;
        Mouse.OverrideCursor = Cursors.SizeAll;
        NavList.CaptureMouse();
    }

    private void OnNavUp(object sender, MouseButtonEventArgs e)
    {
        if (!navDragging) { navPending = null; return; }
        FinishNavDrag(true);
    }

    private void OnNavLost(object sender, MouseEventArgs e)
    {
        if (navDragging) FinishNavDrag(false);
    }

    private void FinishNavDrag(bool commit)
    {
        navDragging = false;
        Mouse.OverrideCursor = null;
        if (NavList.IsMouseCaptured) NavList.ReleaseMouseCapture();
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

    private void OnPlaceTextChanged(object sender, TextChangedEventArgs e)
    {
        if (choosingPlace) return;
        placeTimer.Stop();
        if (PlaceBox.Text.Trim().Length < 2) { ShowPlaceResults(null, ""); return; }
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
            var found = await PlaceSearch.SearchAsync(PlaceBox.Text, Loc.Culture.TwoLetterISOLanguageName, cancel.Token);
            if (cancel.IsCancellationRequested) return;
            ShowPlaceResults(found, found.Count == 0 ? Loc.T("Set.NoMatches") : "");
        }
        catch (OperationCanceledException) { }
        catch (Exception) { if (!cancel.IsCancellationRequested) ShowPlaceResults(null, Loc.T("Set.SearchFailed")); }
    }

    private void ShowPlaceResults(List<Place>? found, string status)
    {
        PlaceResults.ItemsSource = found;
        PlaceResults.Visibility = found is { Count: > 0 } ? Visibility.Visible : Visibility.Collapsed;
        PlaceStatus.Text = status;
        PlaceStatus.Visibility = status.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnPlaceChosen(object sender, SelectionChangedEventArgs e)
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

    private async void OnUseLocation(object sender, RoutedEventArgs e)
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
    private void OnHotkeyKey(object sender, KeyEventArgs e)
    {
        e.Handled = true;
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (key is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt or Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin or Key.None) return;
        var box = (TextBox)sender;
        if (key is Key.Back or Key.Delete) box.Text = "";
        else if (Keyboard.Modifiers == ModifierKeys.None) return; // a shortcut needs at least one modifier
        else box.Text = HotkeyParser.Format(Keyboard.Modifiers, key);
        Changed();
    }

    public void ShowHotkeyStatus(string text)
    {
        HotkeyStatus.Text = text;
        HotkeyStatus.Visibility = text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnMinimize(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
    private void OnDone(object sender, RoutedEventArgs e) => Close();

    // The caption buttons can't take focus, so commit whatever field is still being typed in.
    protected override void OnClosing(CancelEventArgs e)
    {
        if (Keyboard.FocusedElement is TextBox box) box.GetBindingExpression(TextBox.TextProperty)?.UpdateSource();
        settings.WaterMinutes = Math.Clamp(settings.WaterMinutes, 10, 600);
        settings.SitMinutes = Math.Clamp(settings.SitMinutes, 10, 600);
        settings.FocusMinutes = Math.Clamp(settings.FocusMinutes, 1, 180);
        settings.ShortBreakMinutes = Math.Clamp(settings.ShortBreakMinutes, 1, 60);
        settings.LongBreakMinutes = Math.Clamp(settings.LongBreakMinutes, 1, 120);
        if (!TimeSpan.TryParse(settings.Bedtime, out _)) settings.Bedtime = "23:30";
        base.OnClosing(e);
    }
}
