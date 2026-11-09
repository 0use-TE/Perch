using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Perch.Animation;
using Perch.Interaction;
using Perch.Platform.Windows;
using Perch.Services;
using Perch.ViewModels;
using Perch.Views.Pages;

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
    private readonly Control[] pages;
    private readonly Button[] tabs;
    private readonly TranslateTransform contentOffset = new(0, 6), pageOffset = new();
    private readonly TranslateTransform moreShift = new();
    private readonly ScaleTransform moreScale = new(1, 1);
    private IslandHost? host;
    private FullscreenWatcher? fullscreen;
    private SettingsWindow? settingsWindow;
    private TrayIcon? tray;
    private NativeMenuItem? hoverItem, motionItem, menuShowHide, menuSettings, menuLayout, menuExit;
    private int page;
    private double shapeW, shapeH, content;
    // The host window stays at full size (872 x 294) so opening never resizes a window mid-animation;
    // the native region keeps the transparent part from catching clicks.
    private bool largeHost = true, closing, userHidden;
    private bool Capsule => store.State.Settings.Shape == "capsule";
    // 0 = the standard shape, 1 = the floating capsule; it slides between them when the form is changed.
    private double capsuleT, appliedGap = -1;
    private int capsuleVersion;
    private bool lookShown;
    private Rect hoverStrip; // the gap above the floating capsule, in window coordinates (empty in the notch form)
    // Closed: always black. Open: black at the user's opacity (translucent) when the material is "glass".
    private bool Glass => IslandLook.IsGlass(store.State.Settings); // chosen in the settings and supported by the environment
    private readonly DispatcherTimer pointerWatch = new() { Interval = TimeSpan.FromMilliseconds(50) };
    private bool pointerOver;
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

    private IBrush Muted => (IBrush)(this.FindResource("IslandMuted") ?? Brushes.Gray);
    private IBrush Accent => (IBrush)(this.FindResource("IslandAccent") ?? Brushes.Orange);

    public IslandWindow()
    {
        Loc.Apply(store.State.Settings.Language);
        ThemeManager.Apply(store.State.Settings.Theme);
        Loc.Changed += OnLanguageChanged;
        model = new IslandViewModel(store);
        reminders = new Reminders(store, model.Alerts);
        InitializeComponent();
        DataContext = model;
        pages = [HomePage, MusicPanel, FocusPanel, TodoPanel, NotesPanel, InfoPanel, AppsPanel, TrayPanel, PhotosPanel, CalendarPanel, ClipboardPanel, SystemPanel, PortsPanel, AiPanel, AlarmPanel, DinoPanel];
        tabs = [HomeTab, MusicTab, FocusTab, TodoTab, NoteTab, InfoTab, AppsTab, TrayTab, PhotosTab, CalendarTab, ClipboardTab, SystemTab, PortsTab, AiTab, AlarmTab, DinoTab];
        ExpandedContent.Margin = new Thickness(IslandOutline.PadX, 0, 0, 0);
        ExpandedContent.RenderTransform = contentOffset;
        PageHost.RenderTransform = pageOffset;
        MorePanel.RenderTransform = new TransformGroup { Children = { moreScale, moreShift } };
        animator.Host = this;
        var settings = store.State.Settings;
        controller.HoverEnabled = settings.HoverExpand;
        animator.ReducedMotion = settings.ReducedMotion ?? false;
        controller.CollapseOnLeave = settings.CollapseOnLeave;
        controller.ExpandDelay = settings.ExpandDelay;
        controller.CollapseDelay = settings.CollapseDelay;
        outsideWatch.Tick += OnOutsideWatch;
        menuWatch.Tick += OnMenuWatch;
        backdropTimer.Tick += (_, _) => RefreshBackdrop();
        controller.ExpansionChanged += _ => UpdateShape();
        model.ShapeChanged += UpdateShape;
        model.Ai.SettingsRequested += OpenAiSettings;
        model.Alarm.RingingChanged += OnAlarmRinging;
        model.Ai.ConfirmChanged += OnAiConfirm;
        model.Dino.Engaged += () => { host?.BeginEditing(); controller.Edit(true); }; // the game takes the keyboard and keeps the island open
        Program.SecondInstance += () => Dispatcher.UIThread.Post(() => { if (IsVisible) controller.Open(); }); // started again: show the island
        animator.Frame += Draw;
        dragWatch.Tick += (_, _) => { if (!CursorOverIsland()) { dragWatch.Stop(); pointerOver = false; controller.Pointer(false); } };

        // Whether the pointer is on the island is decided from the cursor position, not from enter/leave events alone:
        // an event is missed when the pointer slips in over a curved edge, while the shape is still moving, or while
        // another program is in front, and then the island would never open under a pointer that is resting on it.
        // The events only make the reaction quicker; the timer below is the safety net.
        Stage.PointerEntered += (_, _) => UpdatePointer();
        Stage.PointerMoved += (_, _) => UpdatePointer();
        Stage.PointerExited += (_, _) => UpdatePointer();
        pointerWatch.Tick += (_, _) => UpdatePointer();
        pointerWatch.Start();
        CompactHeader.PointerReleased += (_, e) => { if (e.InitialPressMouseButton == MouseButton.Left && (DateTime.Now - slideEnded).TotalMilliseconds > 350) controller.Open(); };
        Stage.PointerReleased += (_, e) => { if (e.InitialPressMouseButton == MouseButton.Left && hoverStrip.Contains(e.GetPosition(Stage))) controller.Open(); };
        NavRow.AddHandler(PointerWheelChangedEvent, OnNavWheel, RoutingStrategies.Tunnel);
        Stage.AddHandler(PointerPressedEvent, OnSlidePressed, RoutingStrategies.Tunnel);
        Stage.AddHandler(PointerMovedEvent, OnSlideMoved, RoutingStrategies.Tunnel);
        Stage.AddHandler(PointerReleasedEvent, OnSlideReleased, RoutingStrategies.Tunnel);
        NavStack.AddHandler(PointerPressedEvent, OnNavPressed, RoutingStrategies.Tunnel);
        NavStack.AddHandler(PointerMovedEvent, OnNavMoved, RoutingStrategies.Tunnel);
        NavStack.AddHandler(PointerReleasedEvent, OnNavReleased, RoutingStrategies.Tunnel);
        NavStack.AddHandler(PointerCaptureLostEvent, (_, e) => { if (tabDragging && e.Source != NavStack) return; if (!navDropping) EndTabDrag(); }, RoutingStrategies.Tunnel);
        navHold.Tick += (_, _) => BeginTabDrag();
        MoreLayer.PointerPressed += (_, e) => { HideMore(); e.Handled = true; };
        MorePanel.PointerPressed += (_, e) => e.Handled = true;
        AddHandler(PointerReleasedEvent, OnContextMenu, RoutingStrategies.Tunnel);
        AddHandler(PointerPressedEvent, OnInputClick, RoutingStrategies.Tunnel);
        // Any text box on any page puts the island into editing mode so it stays open while typing.
        AddHandler(GotFocusEvent, (_, e) => { if (e.Source is TextBox) controller.Edit(true); }, RoutingStrategies.Bubble, true);
        DragDrop.SetAllowDrop(this, true);
        AddHandler(DragDrop.DragEnterEvent, OnDragEnter);
        AddHandler(DragDrop.DragOverEvent, OnDragOver);
        AddHandler(DragDrop.DropEvent, OnDrop);
        Deactivated += (_, _) => { Perch.Interaction.Dbg.Log("window deactivated"); host?.Passive(); FocusManager?.Focus(null); if (!model.Alarm.IsRinging) controller.Edit(false); };
        clock.Tick += OnClock;
        PropertyChanged += (_, e) => { if (e.Property == IsVisibleProperty) { if (IsVisible) RefreshClock(); else clock.Stop(); } };
        Opened += OnSourceReady;
        Closed += OnClosed;
        StartupEntry.Apply(settings.LaunchAtStartup);
    }

    private void OnSourceReady(object? sender, EventArgs e)
    {
        host = new(this, OverTextBox);
        ApplyPages();
        host.DisplayChanged += Reposition;
        host.HotKeyPressed += OnHotKey;
        host.ClipboardUpdated += () => { if (store.State.Settings.ShowClipboard) model.Clipboard.Capture(); };
        RegisterHotkeys();
        fullscreen = new FullscreenWatcher(host.Handle);
        fullscreen.Changed += ApplyVisibility;
        ApplySlide(animate: false);
        ApplyLook();
        PlaceHost();
        Draw(0, 0, true);
        CreateTray();
        RefreshClock();
        UpdateShape();
    }

    // True when the pointer is over a text input, the one case where a click may activate the island.
    private bool OverTextBox()
    {
        var (x, y) = IslandHost.CursorPosition();
        var point = this.PointToClient(new PixelPoint(x, y));
        for (var hit = this.InputHitTest(point) as Visual; hit != null; hit = hit.GetVisualParent())
            if (hit is TextBox) return true;
        return false;
    }

    // Open: full size. Closed: compact, or wide compact while something is live.
    private void UpdateShape()
    {
        var expanded = controller.State != InteractionState.Compact;
        if (!expanded) HideMore();
        UpdatePortsActive();
        var w = expanded ? 1 : model.IsWide ? WideProgress : 0;
        var h = expanded ? 1.0 : 0.0;
        if ((w > 0 || h > 0) && !largeHost)
        {
            largeHost = true;
            PlaceHost();
            Draw(shapeW, shapeH, false);
        }
        UpdateOutsideWatch();
        PinGlyph.Foreground = controller.State == InteractionState.Pinned ? Accent : Muted;
        ToolTip.SetTip(PinButton, Loc.T(controller.State == InteractionState.Pinned ? "Nav.Unpin" : "Nav.Pin"));
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
        FocusManager?.Focus(null);
        controller.Collapse();
    }

    // The island never takes focus, so Avalonia can't see clicks elsewhere: while a menu is open, any mouse press outside it closes it.
    private void OnMenuWatch(object? sender, EventArgs e)
    {
        if (openMenu is not { IsOpen: true } menu) { menuWatch.Stop(); return; }
        var pressed = IsDown(0x01) || IsDown(0x02) || IsDown(0x04);
        if (!pressed || CursorOverOwnPopup()) return;
        menu.Close();
    }

    private static bool IsDown(int key) => ((ushort)GetAsyncKeyState(key) & 0x8000) != 0;

    // True when the window under the cursor is one of this process's popups (the menu or its submenus), not a normal window.
    private bool CursorOverOwnPopup()
    {
        var (x, y) = IslandHost.CursorPosition();
        var root = GetAncestor(WindowFromPoint(new System.Drawing.Point(x, y)), 2);
        if (root == 0) return false;
        GetWindowThreadProcessId(root, out var pid);
        if (pid != (uint)Environment.ProcessId) return false;
        var self = host?.Handle ?? 0;
        var settingsHandle = settingsWindow?.TryGetPlatformHandle()?.Handle ?? 0;
        return root != self && root != settingsHandle;
    }

    // The stage only changes size at two moments; during animation only the mask moves, so no layout runs per frame.
    // ---- dragging the island sideways (an optional setting). While dragged it follows the pointer with a little lag; on release
    // it keeps some of the speed and settles with a spring, and snaps to the centre when let go near it.
    private double slide, slideTarget, slideVelocity, slideStart, slideRaw, slideGlow;
    private bool slidePressed, slideDragging, slideRunning, slideInZone;
    private int slideCursorStart;
    private TimeSpan slideLast;
    private DateTime slideEnded;
    private IPointer? slidePointer;

    private const double SnapZone = 44;

    // The island is pulled toward the centre while it is near it (so it clings there), and lets go gradually: the pull is
    // strongest at the middle and fades out by about 2.3 times the zone, where the island is back under the pointer.
    private static double Magnet(double raw)
    {
        var a = Math.Abs(raw);
        const double hold = 0.22, catchUp = 1.6;
        double pulled;
        if (a < SnapZone) pulled = a * hold;
        else if (a < SnapZone * (1 + (1 - hold) / (catchUp - 1))) pulled = SnapZone * hold + (a - SnapZone) * catchUp;
        else pulled = a;
        return Math.Sign(raw) * pulled;
    }

    private double SlideLimit() { if (host == null) return 0; var (width, _) = host.WorkArea(); return Math.Max(0, (width - IslandOutline.HostWidth) / 2); }

    // Where the island is wanted when the setting is on (the remembered place) and when it is off (the centre).
    private double SlideHome() => store.State.Settings.IslandDrag ? Math.Clamp(store.State.Settings.IslandOffset, -SlideLimit(), SlideLimit()) : 0;

    private void StartSlide()
    {
        if (slideRunning) return;
        slideRunning = true;
        slideLast = TimeSpan.Zero;
        RequestAnimationFrame(SlideFrame);
    }

    private void SlideFrame(TimeSpan now)
    {
        var dt = slideLast == TimeSpan.Zero ? 0.016 : Math.Clamp((now - slideLast).TotalSeconds, 0.001, 0.05);
        slideLast = now;
        var before = slide;
        if (slideDragging && host != null)
        {
            var (cursor, _) = IslandHost.CursorPosition();
            var (_, scale) = host.WorkArea();
            slideRaw = Math.Clamp(slideStart + (cursor - slideCursorStart) / scale, -SlideLimit(), SlideLimit());
            slideTarget = store.State.Settings.IslandDrag ? Magnet(slideRaw) : slideRaw;
            slide += (slideTarget - slide) * (1 - Math.Exp(-dt * 24)); // the island trails the pointer a little
            slideVelocity = (slide - before) / dt;
        }
        else if (animator.ReducedMotion) { slide = slideTarget; slideVelocity = 0; }
        else
        {
            // a spring: quick, with a small overshoot before it rests
            const double omega = 26, damping = 0.58;
            var acceleration = -2 * damping * omega * slideVelocity - omega * omega * (slide - slideTarget);
            slideVelocity += acceleration * dt;
            slide += slideVelocity * dt;
        }
        host!.Offset = slide;
        PlaceHost();
        // the rim lights up while the island is inside the snapping zone and fades when it leaves or lands
        var glowWanted = slideDragging && Math.Abs(slideRaw) < SnapZone ? 1.0 : 0.0;
        slideGlow = animator.ReducedMotion ? glowWanted : slideGlow + (glowWanted - slideGlow) * (1 - Math.Exp(-dt * 16));
        if (Math.Abs(slideGlow - glowWanted) < 0.02) slideGlow = glowWanted;
        SnapRing.Opacity = slideGlow;
        if (glowWanted == 1.0 && !slideInZone) Perch.Services.Sounds.Audio.Snap(); // a tick the moment it clings
        slideInZone = glowWanted == 1.0;
        var resting = !slideDragging && Math.Abs(slide - slideTarget) < 0.3 && Math.Abs(slideVelocity) < 3 && slideGlow == 0;
        if (resting) { slide = slideTarget; slideVelocity = 0; host.Offset = slide; PlaceHost(); slideRunning = false; return; }
        RequestAnimationFrame(SlideFrame);
    }

    // The settings changed (or the screen did): glide to where the island belongs now.
    private void ApplySlide(bool animate = true)
    {
        if (host == null) return;
        slideTarget = SlideHome();
        if (!animate) { slide = slideTarget; slideVelocity = 0; host.Offset = slide; return; }
        if (Math.Abs(slide - slideTarget) > 0.3) StartSlide();
    }

    // Where a press may start a drag: the closed island, or the empty part of the top strip of the open one.
    private bool OnSlideHandle(PointerPressedEventArgs e)
    {
        if (!store.State.Settings.IslandDrag || !e.GetCurrentPoint(Stage).Properties.IsLeftButtonPressed) return false;
        if (controller.State == InteractionState.Compact) return true;
        if (e.GetPosition(Stage).Y > 58) return false;
        for (var hit = e.Source as Visual; hit != null; hit = hit.GetVisualParent())
            if (hit is Button or TextBox or ToggleButton or Slider) return false;
        return true;
    }

    private void OnSlidePressed(object? sender, PointerPressedEventArgs e)
    {
        if (slideDragging || !OnSlideHandle(e)) return;
        if (e.ClickCount == 2) { GoToCentre(); return; } // double-click on the handle: back to the middle
        slidePressed = true;
        slidePointer = e.Pointer;
        slideCursorStart = IslandHost.CursorPosition().X;
        slideStart = slide;
        slideRaw = slide;
    }

    private void OnSlideMoved(object? sender, PointerEventArgs e)
    {
        if (!slidePressed || host == null) return;
        if (!e.GetCurrentPoint(Stage).Properties.IsLeftButtonPressed) { slidePressed = false; return; }
        var (cursor, _) = IslandHost.CursorPosition();
        if (!slideDragging)
        {
            var (_, scale) = host.WorkArea();
            if (Math.Abs(cursor - slideCursorStart) / scale < 5) return;
            slideDragging = true;
            controller.Edit(true); // stays as it is (open or closed) for the whole drag
            slidePointer?.Capture(Stage);
            StartSlide();
        }
        e.Handled = true;
    }

    private void OnSlideReleased(object? sender, PointerReleasedEventArgs e)
    {
        var was = slideDragging;
        slidePressed = false;
        if (!was) return;
        slideDragging = false;
        slideEnded = DateTime.Now;
        slidePointer?.Capture(null);
        // let go near the middle and it snaps there; otherwise it rests a little beyond where it was let go, as if it had been flicked
        var rest = Math.Clamp(slideRaw + Math.Clamp(slideVelocity * 0.06, -90, 90), -SlideLimit(), SlideLimit());
        slideTarget = Math.Abs(rest) < 44 ? 0 : rest;
        controller.Edit(IsActive && IsKeyboardFocusWithin);
        store.State.Settings.IslandOffset = slideTarget;
        store.Touch();
        e.Handled = true;
        StartSlide();
    }

    private void GoToCentre()
    {
        slideTarget = 0;
        store.State.Settings.IslandOffset = 0;
        store.Touch();
        StartSlide();
    }

    private void PlaceHost()
    {
        var width = largeHost ? IslandOutline.HostWidth : IslandOutline.CompactWidth;
        var height = largeHost ? IslandOutline.HostHeight : IslandOutline.CompactHostHeight;
        host?.Place(width, height);
        Shell.Width = width; Shell.Height = height;
    }

    // How tall the island is when open. Most pages use the standard height; the AI chat opens taller, and the island
    // grows or shrinks to match when you switch to or from it.
    private double expandedHeight = IslandOutline.ExpandedHeight;
    private int heightVersion;
    private const int AiPage = 13, AlarmPage = 14;

    private void ApplyPageHeight(bool animate)
    {
        var extra = page == AiPage ? IslandOutline.TallExtra : 0;
        PageStage.Height = 176 + extra;
        var target = IslandOutline.ExpandedHeight + extra;
        if (Math.Abs(target - expandedHeight) < 0.5) return;
        var version = ++heightVersion;
        var from = expandedHeight;
        if (!animate || animator.ReducedMotion) { expandedHeight = target; Draw(shapeW, shapeH, !animator.IsRunning); return; }
        animator.Tween(240, t =>
        {
            if (version != heightVersion) return; // another page was chosen meanwhile
            expandedHeight = from + (target - from) * (1 - (1 - t) * (1 - t));
            Draw(shapeW, shapeH, !animator.IsRunning);
        });
    }

    private void Draw(double w, double h, bool settled)
    {
        shapeW = Math.Clamp(w, 0, 1.04);
        shapeH = Math.Clamp(h, 0, 1.03);
        var width = IslandOutline.CompactWidth + (IslandOutline.ExpandedWidth - IslandOutline.CompactWidth) * shapeW;
        var height = IslandOutline.CompactHeight + (expandedHeight - IslandOutline.CompactHeight) * shapeH;
        var stageWidth = largeHost ? IslandOutline.HostWidth : IslandOutline.CompactWidth;
        var polygon = IslandOutline.Morph((stageWidth - width) / 2, width, height, Math.Clamp(shapeH, 0, 1), capsuleT);
        // The compact text and the open content sit lower by the capsule's gap as it detaches from the top edge.
        var gap = IslandOutline.CapsuleGap * capsuleT;
        if (Math.Abs(gap - appliedGap) > 0.01)
        {
            appliedGap = gap;
            CompactHeader.Margin = new Thickness(0, gap, 0, 0);
            ExpandedContent.Margin = new Thickness(IslandOutline.PadX, gap, 0, 0);
        }
        var outline = IslandOutline.ToGeometry(polygon);
        // The floating capsule leaves a gap above itself. That gap counts as part of the island for hover and clicks,
        // otherwise a pointer pushed against the top edge of the screen would never reach it.
        hoverStrip = capsuleT > 0.01 ? new Rect((stageWidth - width) / 2, 0, width, IslandOutline.CapsuleGap * capsuleT + 2) : default;
        ShellOutline.Data = outline;
        SnapRing.Data = outline;
        ShellContent.Clip = outline;
        // Solid black when small, increasingly translucent as it opens.
        if (Glass) IslandLook.SetGlassFill(ShellOutline, store.State.Settings, Math.Clamp(shapeH, 0, 1));
        // Frosted backdrop: only while (nearly) open; closed it is plain black and nothing is captured.
        var frosted = Glass && backdropReady && shapeH > 0.04;
        ShellBackdrop.IsVisible = frosted;
        if (frosted)
        {
            ShellBackdrop.Clip = outline;
            if (!backdropTimer.IsEnabled) { RefreshBackdrop(); backdropTimer.Start(); }
        }
        else backdropTimer.Stop();

        // Content follows height: fades in over the second half of expanding, gone within the first 30% of collapsing.
        // The rate limit keeps a mid-flight reversal from making the opacity jump.
        var target = animator.Expanding ? Math.Clamp((shapeH - 0.45) / 0.55, 0, 1) : Math.Clamp((shapeH - 0.70) / 0.30, 0, 1);
        var limit = animator.StepSeconds * 8;
        content = settled ? target : Math.Clamp(target, content - limit, content + limit);
        // The nav row leads, the page trails, and the whole block sharpens from a blur.
        ExpandedContent.Opacity = Math.Clamp(content * 1.7, 0, 1);
        PageStage.Opacity = Math.Clamp((content - 0.25) / 0.75, 0, 1);
        ExpandedContent.IsHitTestVisible = animator.Expanding && Math.Min(shapeW, shapeH) > 0.94;
        contentOffset.Y = 6 * (1 - content);
        // The compact header stretches with the wide island and fades as the island grows taller.
        CompactHeader.Width = Math.Max(176, Math.Min(width, 380) - 72);
        CompactHeader.Opacity = 1 - Math.Clamp(shapeH * 4, 0, 1);
        CompactHeader.IsHitTestVisible = shapeH < 0.25;

        // The native region follows the drawn shape exactly: anything more would show unrendered pixels or catch the pointer.
        if (host == null) return;
        host.Region(IslandOutline.Grown(polygon, 1.5), 0, capsuleT > 0.01 ? hoverStrip : null);
    }

    // Form (notch / floating capsule) and appearance (solid black / frosted glass) are settings, applied live.
    private void ApplyLook()
    {
        MoveToForm();
        IslandLook.Apply(ShellOutline, Glass);
        backdropReady = host != null && host.ExcludeFromCapture(Glass);
        PlaceHost();
        Draw(shapeW, shapeH, !animator.IsRunning);
    }

    // Switching between the standard shape and the capsule slides the outline from one to the other (the first time, and with
    // "reduce motion", it simply snaps).
    private void MoveToForm()
    {
        var target = Capsule ? 1.0 : 0.0;
        var version = ++capsuleVersion;
        if (!lookShown || host == null || animator.ReducedMotion || Math.Abs(target - capsuleT) < 0.001) { capsuleT = target; lookShown = true; return; }
        var from = capsuleT;
        animator.Tween(300, t =>
        {
            if (version != capsuleVersion) return; // another change came in meanwhile
            var ease = t < 0.5 ? 4 * t * t * t : 1 - Math.Pow(-2 * t + 2, 3) / 2; // ease in and out
            capsuleT = from + (target - from) * ease;
            Draw(shapeW, shapeH, !animator.IsRunning);
        });
    }

    // Samples the screen behind the island, blurs it and paints it under the black tint: a frosted-glass texture.
    private void RefreshBackdrop()
    {
        if (host == null || !backdropReady || !IsVisible) return;
        var picture = BackdropCapture.Grab(host.ScreenRect());
        if (picture == null) return;
        ShellBackdrop.Background = new ImageBrush(picture) { Stretch = Stretch.Fill, TileMode = TileMode.None };
    }

    private void OnContextMenu(object? sender, PointerReleasedEventArgs e)
    {
        if (e.InitialPressMouseButton != MouseButton.Right) return;
        if (ShellOutline.Data is not Geometry outline || !outline.FillContains(e.GetPosition(Stage))) return;
        var s = store.State.Settings;
        MenuItem Item(string key, Action action, bool check = false)
        {
            var item = new MenuItem { Header = Loc.T(key), ToggleType = MenuItemToggleType.CheckBox, IsChecked = check };
            item.Click += (_, _) => { action(); store.Touch(); settingsWindow?.Refresh(); };
            return item;
        }
        MenuItem Group(string key, params MenuItem[] children)
        {
            var group = new MenuItem { Header = Loc.T(key) };
            foreach (var child in children) group.Items.Add(child);
            return group;
        }
        var menu = new ContextMenu();
        // The island window is always dark, so the menu takes its colours from the chosen theme explicitly.
        var light = ThemeManager.IsLight(s.Theme);
        IBrush Brush(string dark, string lightColor) => new SolidColorBrush(Color.Parse(light ? lightColor : dark));
        menu.Resources["MenuFlyoutPresenterBackground"] = Brush("#F0060607", "#F2FAFAFB");
        menu.Resources["MenuFlyoutPresenterBorderBrush"] = Brush("#24FFFFFF", "#1A000000");
        menu.Resources["MenuFlyoutItemForeground"] = Brush("#F2F2F4", "#16161A");
        menu.Resources["MenuFlyoutItemForegroundPointerOver"] = Brush("#F2F2F4", "#16161A");
        menu.Resources["MenuFlyoutItemForegroundPressed"] = Brush("#F2F2F4", "#16161A");
        menu.Resources["MenuFlyoutSubItemForeground"] = Brush("#F2F2F4", "#16161A");
        menu.Resources["MenuFlyoutSubItemForegroundPointerOver"] = Brush("#F2F2F4", "#16161A");
        menu.Resources["MenuFlyoutSubItemForegroundPressed"] = Brush("#F2F2F4", "#16161A");
        menu.Resources["MenuFlyoutSubItemChevron"] = Brush("#9A9AA4", "#6A6A74");
        menu.Resources["MenuFlyoutItemBackgroundPointerOver"] = Brush("#2EFFFFFF", "#16000000");
        menu.Resources["MenuFlyoutItemBackgroundPressed"] = Brush("#2EFFFFFF", "#16000000");
        menu.Resources["MenuFlyoutSubItemBackgroundPointerOver"] = Brush("#2EFFFFFF", "#16000000");
        menu.Resources["MenuFlyoutSubItemBackgroundPressed"] = Brush("#2EFFFFFF", "#16000000");
        menu.Resources["MenuFlyoutSubItemBackgroundPointerOver"] = Brush("#2EFFFFFF", "#16000000");
        menu.Resources["MenuFlyoutSeparatorBackground"] = Brush("#26FFFFFF", "#18000000");
        if (AddPageItems(menu, e.GetPosition(this))) menu.Items.Add(new Separator());
        menu.Items.Add(Item("Menu.Settings", OpenSettings));
        menu.Items.Add(new Separator());
        menu.Items.Add(Group("Menu.Form",
            Item("Menu.FormStandard", () => { s.Shape = "standard"; ApplyLook(); }, s.Shape != "capsule"),
            Item("Menu.FormCapsule", () => { s.Shape = "capsule"; ApplyLook(); }, s.Shape == "capsule")));
        MenuItem GlassItem()
        {
            var item = Item("Menu.AppearanceGlass", () => { s.Appearance = "glass"; ApplyLook(); }, s.Appearance == "glass");
            item.IsEnabled = Translucency.Supported;
            return item;
        }
        menu.Items.Add(Group("Menu.Appearance",
            Item("Menu.AppearanceBlack", () => { s.Appearance = "black"; ApplyLook(); }, s.Appearance != "glass"),
            GlassItem()));
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
            var item = new MenuItem { Header = option.Name, ToggleType = MenuItemToggleType.CheckBox, IsChecked = s.Language == code };
            item.Click += (_, _) => { ChangeLanguage(code); settingsWindow?.Refresh(); };
            languages.Add(item);
        }
        menu.Items.Add(Group("Menu.Language", languages.ToArray()));
        menu.Items.Add(new Separator());
        menu.Items.Add(Item("Menu.Exit", App.Quit));
        // While the menu is open the island must not collapse under it.
        menu.Opened += (_, _) => { menuOpen = true; openMenu = menu; menuWatch.Start(); controller.Edit(true); };
        menu.Closed += (_, _) => { menuOpen = false; openMenu = null; menuWatch.Stop(); controller.Edit(IsActive && IsKeyboardFocusWithin); };
        menu.Open(Stage);
        e.Handled = true;
    }

    // Right-click on a page: the actions for whatever is under the pointer (a note, a to-do, a clipboard entry, a shelf file)
    // come first, then the island's own menu. Returns whether anything was added.
    private bool AddPageItems(ContextMenu menu, Point point)
    {
        if (controller.State == InteractionState.Compact) return false;
        object? hit = null;
        for (var visual = this.InputHitTest(point) as Visual; visual != null && hit == null; visual = visual.GetVisualParent())
            if (visual is StyledElement { DataContext: NoteItem or TodoItem or ClipItem or TrayFile or CommandItem or PortEntry or ChatLine } element) hit = element.DataContext;

        var first = menu.Items.Count;
        void Act(string key, Action action, bool? check = null)
        {
            var item = new MenuItem { Header = Loc.T(key) };
            if (check is { } value) { item.ToggleType = MenuItemToggleType.CheckBox; item.IsChecked = value; }
            item.Click += (_, _) => { action(); store.Touch(); };
            menu.Items.Add(item);
        }
        void Copy(string text) { if (Ui.Clipboard is { } clipboard) _ = Avalonia.Input.Platform.ClipboardExtensions.SetTextAsync(clipboard, text); }

        var current = pages[page];
        if (current == NotesPanel)
        {
            if (hit is NoteItem note)
            {
                Act("Notes.Highlight", () => { model.Notes.Selected = note; model.Notes.ToggleHighlight.Execute(null); }, note.Highlight);
                Act("Menu.Copy", () => Copy(string.Join(Environment.NewLine, new[] { note.Title, note.Text }.Where(part => !string.IsNullOrEmpty(part)))));
                Act("Notes.Delete", () => { model.Notes.Selected = note; model.Notes.Delete.Execute(null); });
                menu.Items.Add(new Separator());
            }
            Act("Notes.New", () => model.Notes.New.Execute(null));
        }
        else if (current == TodoPanel)
        {
            if (hit is TodoItem todo)
            {
                Act(todo.Done ? "Menu.MarkNotDone" : "Menu.MarkDone", () => todo.Done = !todo.Done);
                Act("Common.Delete", () => model.Todo.Delete.Execute(todo));
                menu.Items.Add(new Separator());
            }
            Act("Todo.ClearDone", () => model.Todo.ClearDone.Execute(null));
        }
        else if (current == ClipboardPanel)
        {
            if (hit is ClipItem clip)
            {
                Act("Menu.Copy", () => model.Clipboard.Copy.Execute(clip));
                Act("Clip.Remove", () => model.Clipboard.Remove.Execute(clip));
                menu.Items.Add(new Separator());
            }
            Act(model.Clipboard.Paused ? "Clip.Resume" : "Clip.Pause", () => model.Clipboard.TogglePause.Execute(null));
            Act("Clip.Clear", () => model.Clipboard.Clear.Execute(null));
        }
        else if (current == TrayPanel)
        {
            if (hit is TrayFile file)
            {
                Act("Common.Open", () => model.Tray.Open.Execute(file));
                Act("Tray.Reveal", () => model.Tray.Reveal.Execute(file));
                Act("Common.Remove", () => model.Tray.Remove.Execute(file));
                menu.Items.Add(new Separator());
            }
            Act("Common.Clear", () => model.Tray.Clear.Execute(null));
        }
        else if (current == AiPanel)
        {
            if (hit is ChatLine { Text.Length: > 0 } line) Act("Menu.Copy", () => Copy(line.Text));
            Act("Ai.New", () => model.Ai.NewChat.Execute(null));
        }
        else if (current == PortsPanel && hit is PortEntry port)
        {
            Act("Ports.CopyPort", () => model.Ports.CopyPort.Execute(port));
            Act("Ports.CopyAddress", () => model.Ports.CopyAddress.Execute(port));
            Act("Ports.OpenBrowser", () => model.Ports.OpenBrowser.Execute(port));
            if (port.Path != null) Act("Tray.Reveal", () => model.Ports.Reveal.Execute(port));
        }
        else if (current == AppsPanel && hit is CommandItem command)
        {
            Act("Common.Run", () => model.Launcher.RunCommand.Execute(command));
            Act("Common.Delete", () => model.Launcher.RemoveCommand.Execute(command));
        }
        // A trailing separator would double up with the one the caller adds.
        if (menu.Items.Count > first && menu.Items[^1] is Separator) menu.Items.RemoveAt(menu.Items.Count - 1);
        return menu.Items.Count > first;
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
        controller.ExpandDelay = s.ExpandDelay;
        controller.CollapseDelay = s.CollapseDelay;
        if (Loc.Resolve(s.Language) != Loc.Code) Loc.Apply(s.Language); // language switches immediately, not when the window closes
        animator.ReducedMotion = s.ReducedMotion ?? false;
        ThemeManager.Apply(s.Theme);
        if (hoverItem != null) hoverItem.IsChecked = s.HoverExpand;
        if (motionItem != null) motionItem.IsChecked = animator.ReducedMotion;
        Translucency.Refresh();
        model.Ai.RefreshProviders();
        ApplyLook();
        ApplyPages();
        RegisterHotkeys();
        // What the closed island shows (network speed, music, the focus timer) follows the settings immediately.
        if (!s.ShowFocus && model.Focus.Running) { model.Focus.Suspend(); model.Alerts.Show(AlertQueue.Timer, Loc.T("Focus.Suspended")); }
        model.Focus.SettingsChanged();
        model.UpdateCompact();
        var calendarKey = (s.CalendarUrls, s.CalendarReminder);
        if (calendarKey != appliedCalendar) { appliedCalendar = calendarKey; model.Calendar.SettingsChanged(); }
        var weatherKey = (s.Latitude, s.Longitude, s.Fahrenheit);
        if (weatherKey != appliedWeather) { appliedWeather = weatherKey; model.Weather.SettingsChanged(); }
        UpdateOutsideWatch();
        if (!slideDragging) ApplySlide();
        store.Touch();
    }

    private void SetHover(bool value)
    {
        store.State.Settings.HoverExpand = value;
        controller.HoverEnabled = value;
        if (hoverItem != null) hoverItem.IsChecked = value;
    }

    private void ChangeLanguage(string code)
    {
        store.State.Settings.Language = code;
        Loc.Apply(code);
        store.Touch();
    }

    private void Reposition() { if (!slideDragging) ApplySlide(animate: false); PlaceHost(); Draw(shapeW, shapeH, !animator.IsRunning); }

    private void OnInputClick(object? sender, PointerPressedEventArgs e)
    {
        for (var hit = e.Source as Visual; hit != null; hit = hit.GetVisualParent())
            if (hit is TextBox) { host?.BeginEditing(); return; }
    }

    private void OnPin(object? sender, RoutedEventArgs e) => controller.TogglePin();
    private void OnSettings(object? sender, RoutedEventArgs e) => OpenSettings();

    private void OnTab(object? sender, RoutedEventArgs e)
    {
        if (tabDragged || (DateTime.Now - tabDropped).TotalMilliseconds < 400) { tabDragged = false; return; } // the release that ends a drag is not a click
        FocusManager?.Focus(null);
        controller.Edit(false);
        SelectPage(int.Parse((string)((Button)sender!).Tag!));
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
            Dispatcher.UIThread.Post(() => NotesPanel.FocusTitle(), DispatcherPriority.Input);
        }
    }

    // ---- navigation bar: pinned pages on the bar (the current one always shown), the rest under "More"
    private static readonly string[] NavNameKeys = ["Nav.Overview", "Nav.Music", "Nav.Focus", "Nav.Todo", "Nav.Notes", "Nav.Info", "Nav.Apps", "Nav.Tray", "Nav.Photos", "Nav.Calendar", "Nav.Clipboard", "Nav.System", "Nav.Ports", "Nav.Ai", "Nav.Alarm", "Nav.Dino"];
    private static readonly string[] NavIds = ["home", "music", "focus", "todo", "notes", "info", "apps", "tray", "photos", "calendar", "clipboard", "system", "ports", "ai", "alarm", "dino"];
    private readonly List<int> morePages = [];
    private DateTime lastWheel;

    private StackPanel Face(int index) => (StackPanel)tabs[index].Content!;

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
        MoreButton.IsVisible = more.Count > 0;
        NavStack.Children.Add(MoreButton);
        for (var i = 1; i < tabs.Length; i++) Face(i).Children[1].IsVisible = i == page;
    }

    private void OnMore(object? sender, RoutedEventArgs e)
    {
        if (MoreLayer.IsVisible) { HideMore(); return; }
        MoreItems.Children.Clear();
        foreach (var index in morePages)
        {
            var face = Face(index);
            var name = Loc.T(NavNameKeys[index]);
            var item = new Button { Tag = index.ToString(), Height = 40, MinWidth = 164, Margin = new Thickness(2), HorizontalContentAlignment = HorizontalAlignment.Left };
            item.Classes.Add("Tile");
            item.Click += OnMoreItem;
            Avalonia.Automation.AutomationProperties.SetName(item, name);
            item.Content = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(12, 0, 18, 0),
                Children =
                {
                    new TextBlock { Text = ((TextBlock)face.Children[0]).Text, FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"), FontSize = 16, Width = 22, Foreground = new SolidColorBrush(Color.FromRgb(0xC9, 0xC9, 0xCE)), VerticalAlignment = VerticalAlignment.Center },
                    new TextBlock { Text = name, FontSize = 13, Foreground = Brushes.White, Margin = new Thickness(10, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center },
                },
            };
            MoreItems.Children.Add(item);
        }
        MoreItems.Columns = MoreItems.Children.Count > 1 ? 2 : 1;
        // The panel's right edge lines up with the "More" button.
        MorePanel.Margin = new Thickness(0, 60, 0, 0);
        MorePanel.Measure(Size.Infinity);
        var width = MorePanel.DesiredSize.Width;
        var right = MoreButton.TranslatePoint(new Point(MoreButton.Bounds.Width, 0), MoreLayer)?.X ?? 828;
        MorePanel.Margin = new Thickness(Math.Max(12, Math.Min(right - width, 828 - width)), 60, 0, 0);
        MoreLayer.IsVisible = true;
        if (animator.ReducedMotion) { MorePanel.Opacity = 1; moreScale.ScaleX = moreScale.ScaleY = 1; moreShift.Y = 0; return; }
        // Opens from the "More" button: a short fade, scale and slide.
        animator.Tween(170, t =>
        {
            var ease = 1 - (1 - t) * (1 - t);
            MorePanel.Opacity = t;
            moreScale.ScaleX = moreScale.ScaleY = 0.92 + 0.08 * ease;
            moreShift.Y = -6 * (1 - ease);
        });
    }

    private void HideMore() => MoreLayer.IsVisible = false;
    private void OnMoreItem(object? sender, RoutedEventArgs e) { HideMore(); SelectPage(int.Parse((string)((Button)sender!).Tag!)); }

    // ---- reordering the bar, like the Windows taskbar or a browser tab strip: press a pinned page icon and drag it sideways
    // (or hold it a moment first). The icon follows the hand; the others glide aside to open the slot it would land in.
    // Nothing in the bar is rebuilt until the drop, and every icon eases toward its target on each rendered frame.
    // Icons are shifted with their margins (a negative right margin cancels the shift for the icons after it).
    private readonly DispatcherTimer navHold = new() { Interval = TimeSpan.FromMilliseconds(260) };
    private Button? dragTab;
    private Point dragStart;
    private double dragGrab, dragPointerX;
    private bool tabDragging, tabDragged, stripBusy, navDropping;
    // Dragging the page borrowed from "More" swaps it with a pinned icon instead: the icon under it slides over to the
    // borrowed one's place, and on the drop that icon goes into "More" while the borrowed page stays on the bar.
    private bool dragBorrowed;
    private Button? swapTarget;
    private IPointer? dragPointer;
    private DateTime tabDropped;
    private readonly Dictionary<Button, double> stripOffset = [];     // how far each icon is drawn from its slot
    private TimeSpan stripLast;

    private List<Button> BarTabs() => NavStack.Children.OfType<Button>().Where(b => b != HomeTab && b != MoreButton && b.IsVisible).ToList();

    // Left edges of the icons in the given order (the bar spacing: 10 px before the first, 4 between), measured from the
    // end of the Overview button, which never moves.
    private Dictionary<Button, double> Slots(List<Button> order)
    {
        var x = HomeTab.Bounds.Right + HomeTab.Margin.Right;
        var result = new Dictionary<Button, double>();
        for (var i = 0; i < order.Count; i++)
        {
            x += i == 0 ? 10 : 4;
            result[order[i]] = x;
            x += order[i].Bounds.Width;
        }
        return result;
    }

    private Dictionary<Button, double> SlotsWith(List<Button> bar, Button moving, int at)
    {
        var order = bar.Where(b => b != moving).ToList();
        order.Insert(Math.Clamp(at, 0, order.Count), moving);
        return Slots(order);
    }

    // The slot whose centre is nearest to the dragged icon (only among the pinned icons; a page borrowed onto the bar stays last).
    private int TargetSlot(List<Button> bar, Button moving)
    {
        var pinned = store.State.Settings.NavPinned;
        var limit = bar.Count(b => pinned.Contains(NavIds[Array.IndexOf(tabs, b)]));
        var center = dragPointerX - dragGrab + moving.Bounds.Width / 2;
        var best = 0;
        var bestGap = double.MaxValue;
        for (var j = 0; j < limit; j++)
        {
            var gap = Math.Abs(SlotsWith(bar, moving, j)[moving] + moving.Bounds.Width / 2 - center);
            if (gap < bestGap) { bestGap = gap; best = j; }
        }
        return best;
    }

    // The pinned icon the borrowed page is held over (its middle within reach of the dragged icon's middle).
    private Button? SwapTarget(List<Button> bar, Button moving, Dictionary<Button, double> home)
    {
        var pinned = store.State.Settings.NavPinned;
        var center = dragPointerX - dragGrab + moving.Bounds.Width / 2;
        Button? best = null;
        var bestGap = double.MaxValue;
        foreach (var b in bar)
        {
            if (b == moving || !pinned.Contains(NavIds[Array.IndexOf(tabs, b)])) continue;
            var gap = Math.Abs(home[b] + b.Bounds.Width / 2 - center);
            if (gap < b.Bounds.Width / 2 + 10 && gap < bestGap) { bestGap = gap; best = b; }
        }
        return best;
    }

    private static void Shift(List<Button> bar, Button b, double offset)
    {
        var left = bar.IndexOf(b) == 0 ? 10 : 4;
        b.Margin = Math.Abs(offset) < 0.01 ? new Thickness(left, 0, 0, 0) : new Thickness(left + offset, 0, -offset, 0);
    }

    private void OnNavPressed(object? sender, PointerPressedEventArgs e)
    {
        if (tabDragging) return;
        EndTabDrag();
        tabDragged = false;
        if (!e.GetCurrentPoint(NavStack).Properties.IsLeftButtonPressed) return;
        var hit = e.Source as Visual;
        while (hit != null && !(hit is Button b && tabs.Contains(b))) hit = hit.GetVisualParent();
        if (hit is not Button tab || tab == HomeTab) return;
        if (!BarTabs().Contains(tab)) return;
        dragTab = tab;
        dragStart = e.GetPosition(NavStack);
        dragPointerX = dragStart.X;
        dragPointer = e.Pointer;
        navHold.Start();
    }

    private void BeginTabDrag()
    {
        navHold.Stop();
        if (dragTab == null || tabDragging) return;
        var bar = BarTabs();
        if (!bar.Contains(dragTab)) { dragTab = null; return; }
        tabDragging = true;
        dragBorrowed = !store.State.Settings.NavPinned.Contains(NavIds[Array.IndexOf(tabs, dragTab)]);
        swapTarget = null;
        dragGrab = dragStart.X - Slots(bar)[dragTab];
        dragTab.ZIndex = 10;
        dragPointer?.Capture(NavStack); // the bar keeps the pointer for the whole drag
        StartStrip();
    }

    private void OnNavMoved(object? sender, PointerEventArgs e)
    {
        if (dragTab == null) return;
        var at = e.GetPosition(NavStack);
        if (!tabDragging)
        {
            // Sliding sideways starts the drag at once (as on the taskbar); straying up or down means it was not one.
            if (Math.Abs(at.Y - dragStart.Y) > 14) { navHold.Stop(); dragTab = null; return; }
            if (Math.Abs(at.X - dragStart.X) < 6) return;
            BeginTabDrag();
            if (!tabDragging) return;
        }
        e.Handled = true;
        tabDragged = true;
        dragPointerX = at.X;
    }

    private void OnNavReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (!tabDragging) { EndTabDrag(); return; }
        e.Handled = true;
        tabDropped = DateTime.Now;
        var moving = dragTab!;
        var bar = BarTabs();
        var before = Slots(bar);
        // Where every icon is drawn right now, so the settle starts from there.
        var drawn = bar.ToDictionary(b => b, b => before[b] + stripOffset.GetValueOrDefault(b));
        drawn[moving] = dragPointerX - dragGrab;
        if (dragBorrowed)
        {
            var target = swapTarget;
            swapTarget = null;
            if (target != null)
            {
                var s = store.State.Settings;
                var newId = NavIds[Array.IndexOf(tabs, moving)];
                var oldId = NavIds[Array.IndexOf(tabs, target)];
                var at = s.NavPinned.IndexOf(oldId);
                if (at >= 0) s.NavPinned[at] = newId;
                var i = s.NavOrder.IndexOf(newId);
                var j = s.NavOrder.IndexOf(oldId);
                if (i >= 0 && j >= 0) (s.NavOrder[i], s.NavOrder[j]) = (s.NavOrder[j], s.NavOrder[i]);
            }
        }
        else
        {
        var final = SlotsWith(bar, moving, TargetSlot(bar, moving));
        // New order: the pinned icons in their new sequence take the places the pinned icons held in the saved order.
        var pinned = store.State.Settings.NavPinned;
        var ids = final.OrderBy(p => p.Value).Select(p => NavIds[Array.IndexOf(tabs, p.Key)]).Where(pinned.Contains).ToList();
        var order = store.State.Settings.NavOrder;
        var places = order.Select((id, i) => (id, i)).Where(t => ids.Contains(t.id)).Select(t => t.i).ToList();
        for (var k = 0; k < Math.Min(places.Count, ids.Count); k++) order[places[k]] = ids[k];
        }
        tabDragging = false;
        dragTab = null;
        navDropping = true;
        ApplyNav();
        dragPointer?.Capture(null);
        navDropping = false;
        var newBar = BarTabs();
        var after = Slots(newBar);
        foreach (var b in newBar)
        {
            stripOffset[b] = drawn.TryGetValue(b, out var x) ? x - after[b] : 0;
            Shift(newBar, b, stripOffset[b]);
        }
        moving.ZIndex = 10; // stays on top while it settles
        StartStrip();
        store.Touch();
        settingsWindow?.Refresh();
    }

    private void EndTabDrag()
    {
        navHold.Stop();
        var was = tabDragging;
        dragTab = null;
        tabDragging = false;
        if (was) { dragPointer?.Capture(null); StartStrip(); }
    }

    private void StartStrip()
    {
        if (stripBusy) return;
        stripBusy = true;
        stripLast = TimeSpan.Zero;
        RequestAnimationFrame(StripFrame);
    }

    // One frame of the bar: the dragged icon sits under the pointer, the rest ease toward their goals (0 once dropped).
    private void StripFrame(TimeSpan now)
    {
        var dt = stripLast == TimeSpan.Zero ? 0.016 : Math.Clamp((now - stripLast).TotalSeconds, 0.001, 0.05);
        stripLast = now;
        var smooth = animator.ReducedMotion ? 1.0 : 1 - Math.Exp(-dt * 20); // about a 50 ms time constant
        var moving = tabDragging ? dragTab : null;
        var bar = BarTabs();
        var home = Slots(bar);
        Dictionary<Button, double>? goals = null;
        if (moving != null && bar.Contains(moving))
        {
            if (dragBorrowed)
            {
                swapTarget = SwapTarget(bar, moving, home);
                goals = bar.ToDictionary(b => b, b => home[b]);
                if (swapTarget != null) goals[swapTarget] = home[moving];
            }
            else goals = SlotsWith(bar, moving, TargetSlot(bar, moving));
        }
        var busy = moving != null;
        foreach (var b in bar)
        {
            var current = stripOffset.GetValueOrDefault(b);
            if (b == moving) current = dragPointerX - dragGrab - home[b];
            else current += ((goals != null ? goals[b] - home[b] : 0) - current) * smooth;
            if (moving == null && Math.Abs(current) < 0.3)
            {
                if (stripOffset.Remove(b)) { Shift(bar, b, 0); b.ZIndex = 0; }
                continue;
            }
            if (moving == null) busy = true;
            stripOffset[b] = current;
            Shift(bar, b, current);
        }
        if (busy) RequestAnimationFrame(StripFrame); else stripBusy = false;
    }

    // The wheel over the bar steps through the pages (Overview first, then in the configured order).
    private void OnNavWheel(object? sender, PointerWheelEventArgs e)
    {
        e.Handled = true;
        if ((DateTime.Now - lastWheel).TotalMilliseconds < 140) return; // touchpads send a burst of events
        var shown = PageShown();
        var sequence = new List<int> { 0 };
        sequence.AddRange(store.State.Settings.NavOrder.Select(id => Array.IndexOf(NavIds, id)).Where(i => i > 0 && shown[i]).Distinct());
        var at = sequence.IndexOf(page);
        if (at < 0) return;
        var next = Math.Clamp(at + (e.Delta.Y < 0 ? 1 : -1), 0, sequence.Count - 1);
        if (next == at) return;
        lastWheel = DateTime.Now;
        HideMore();
        SelectPage(sequence[next]);
    }

    private bool[] PageShown()
    {
        var s = store.State.Settings;
        return [true, s.ShowMusic, s.ShowFocus, s.ShowTodo, s.ShowNotes, s.ShowInfo, s.ShowApps, s.ShowTray, s.ShowPhotos, s.ShowCalendar, s.ShowClipboard, s.ShowSystem, s.ShowPorts, s.ShowAi, s.ShowAlarm, s.ShowDino];
    }

    private void ApplyPages()
    {
        var shown = PageShown();
        HomeWeatherButton.IsVisible = shown[5];
        HomeTodoAdd.IsVisible = shown[3];
        HomeNotesButton.IsVisible = shown[4];
        if (!shown[page]) SelectPage(0);
        ApplyNav();
    }

    // The port list refreshes itself only while it is on screen.
    private void UpdatePortsActive()
    {
        var open = controller.State != InteractionState.Compact;
        model.Ports.Active = open && pages[page] == PortsPanel;
        model.Dino.Active = open && pages[page] == DinoPanel;
        model.Monitor.Active = open && pages[page] == SystemPanel;
        var aiVisible = open && pages[page] == AiPanel;
        if (aiVisible && !aiShown) model.Ai.OnShown(); // the greeting types itself out each time the chat comes on screen
        aiShown = aiVisible;
    }

    private bool aiShown;

    private void SelectPage(int index)
    {
        if (!PageShown()[index]) return;
        page = index;
        for (var i = 0; i < pages.Length; i++)
        {
            pages[i].IsVisible = i == index;
            tabs[i].Background = i == index ? new SolidColorBrush(Color.FromRgb(38, 38, 38)) : Brushes.Transparent;
            if (i > 0) ((TextBlock)Face(i).Children[0]).Foreground = i == index ? Brushes.White : Muted;
        }
        if (pages[index] == AppsPanel) model.Launcher.EnsureLoaded();
        if (pages[index] == PhotosPanel) model.Photos.EnsureLoaded();
        UpdatePortsActive();
        if (pages[index] == AiPanel) model.Ai.RefreshProviders();
        ApplyPageHeight(true);
        animator.Reveal(PageHost, pageOffset);
        ApplyNav();
    }

    // Dragging files over the island opens the file shelf; dropping keeps them there.
    private static bool HasFiles(DragEventArgs e) => e.DataTransfer.Contains(DataFormat.File);

    private void OnDragEnter(object? sender, DragEventArgs e)
    {
        e.Handled = true;
        if (!store.State.Settings.ShowTray || TrayPage.DraggingOut || !HasFiles(e)) { e.DragEffects = DragDropEffects.None; return; }
        e.DragEffects = DragDropEffects.Copy;
        if (page != TrayIndex) SelectPage(TrayIndex);
        controller.Open();
        dragWatch.Start();
    }

    private void OnDragOver(object? sender, DragEventArgs e)
    {
        e.DragEffects = store.State.Settings.ShowTray && !TrayPage.DraggingOut && HasFiles(e) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private void OnDrop(object? sender, DragEventArgs e)
    {
        e.Handled = true;
        if (!store.State.Settings.ShowTray || TrayPage.DraggingOut || e.DataTransfer.TryGetFiles() is not { } items) return;
        var files = items.Select(i => i.Path?.LocalPath).Where(p => p != null).Select(p => p!).ToArray();
        if (files.Length > 0) model.Tray.Add(files);
    }

    // Tells the controller when the pointer arrives on or leaves the island (only on a change).
    private void UpdatePointer()
    {
        var over = CursorOverIsland();
        if (over == pointerOver) return;
        pointerOver = over;
        Perch.Interaction.Dbg.Log($"pointer over island: {over}");
        controller.Pointer(over);
    }

    // During and after a drag the pointer-leave event isn't reported, so the cursor is checked against the outline.
    private bool CursorOverIsland()
    {
        if (!IsVisible || ShellOutline.Data is not Geometry outline) return false;
        var (x, y) = IslandHost.CursorPosition();
        var point = this.PointToClient(new PixelPoint(x, y));
        return outline.FillContains(point) || hoverStrip.Contains(point);
    }

    // A ringing alarm opens the island on the alarm page and keeps it open until it is stopped or snoozed.
    private void OnAlarmRinging(bool ringing)
    {
        if (ringing)
        {
            if (!IsVisible) { userHidden = false; ApplyVisibility(); }
            SelectPage(AlarmPage);
            controller.Edit(true);
        }
        else controller.Edit(IsActive && IsKeyboardFocusWithin);
    }

    // An AI action waits for the user's OK: bring the island up on the chat and keep it open until it is answered.
    private void OnAiConfirm(bool asking)
    {
        if (asking)
        {
            if (!IsVisible) { userHidden = false; ApplyVisibility(); }
            controller.Open();
            SelectPage(Array.IndexOf(NavIds, "ai"));
            controller.Edit(true);
        }
        else controller.Edit(IsActive && IsKeyboardFocusWithin);
    }

    private void OpenAiSettings() { OpenSettings(); settingsWindow?.ShowAiPage(); }

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
        if (hide && IsVisible) { Hide(); controller.Edit(false); pointerOver = false; controller.Pointer(false); }
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

    // The notification-area icon and its menu. The multi-size .ico lets Windows pick the size for any DPI.
    private void CreateTray()
    {
        menuShowHide = new NativeMenuItem();
        menuShowHide.Click += (_, _) => ToggleVisible();
        menuSettings = new NativeMenuItem();
        menuSettings.Click += (_, _) => OpenSettings();
        NativeMenuItem Toggle(bool initial, Action<bool> changed)
        {
            var item = new NativeMenuItem { ToggleType = MenuItemToggleType.CheckBox, IsChecked = initial };
            item.Click += (_, _) => changed(item.IsChecked);
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
        menuLayout = new NativeMenuItem { Menu = new NativeMenu { hoverItem, motionItem } };
        menuExit = new NativeMenuItem();
        menuExit.Click += (_, _) => App.Quit();
        WindowIcon? icon = null;
        try { icon = new WindowIcon(AssetLoader.Open(new Uri("avares://Perch/Assets/perch.ico"))); } catch { }
        tray = new TrayIcon
        {
            Icon = icon,
            IsVisible = true,
            Menu = new NativeMenu { menuShowHide, menuSettings, new NativeMenuItemSeparator(), menuLayout, new NativeMenuItemSeparator(), menuExit },
        };
        tray.Clicked += (_, _) => ToggleVisible();
        TrayIcon.SetIcons(Application.Current!, [tray]);
        LocalizeTray();
    }

    private void LocalizeTray()
    {
        if (tray == null) return;
        menuShowHide!.Header = Loc.T("Menu.ShowHide");
        menuSettings!.Header = Loc.T("Menu.Settings");
        menuLayout!.Header = Loc.T("Menu.Layout");
        hoverItem!.Header = Loc.T("Menu.Hover");
        motionItem!.Header = Loc.T("Menu.ReduceMotion");
        menuExit!.Header = Loc.T("Menu.Exit");
        tray.ToolTipText = Loc.T("Menu.Tooltip");
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
        dragWatch.Stop(); pointerWatch.Stop();
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
        if (tray != null) { tray.IsVisible = false; tray.Dispose(); }
        App.Quit();
    }
}
