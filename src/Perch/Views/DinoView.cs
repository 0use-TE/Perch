using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Perch.Services;
using Perch.Services.Games;

namespace Perch.Views;

// Draws the Chrome-style dinosaur game from its sprite pictures (Assets/Dino) and passes clicks and keys to it.
// The pictures are the offline game's, inverted to light grey on black like Chrome's dark mode.
public sealed class DinoView : Control
{
    private static readonly Dictionary<string, Bitmap> Sprites = [];
    private static readonly IBrush Grey = new SolidColorBrush(Color.FromRgb(0xAC, 0xAC, 0xAC));
    private static readonly IBrush Dim = new SolidColorBrush(Color.FromRgb(0x6E, 0x6E, 0x76));
    private static readonly Typeface Mono = new("Cascadia Mono, Consolas", FontStyle.Normal, FontWeight.Bold);

    public DinoGame Game { get; } = new();
    public int Best { get; set; }
    // The new best score, once a run that beat the old one is over.
    public event Action<int>? NewBest;
    // A click or key started the game: the island should keep itself open and take keyboard input.
    public event Action? Engaged;

    private double worldWidth = 700;
    private bool jumpKeyDown;
    private DinoState lastState = DinoState.Ready;

    public DinoView()
    {
        Focusable = true;
        ClipToBounds = true;
        Cursor = new Cursor(StandardCursorType.Hand);
        RenderOptions.SetBitmapInterpolationMode(this, BitmapInterpolationMode.HighQuality);
    }

    private static Bitmap Sprite(string name)
    {
        if (Sprites.TryGetValue(name, out var cached)) return cached;
        using var stream = AssetLoader.Open(new Uri($"avares://Perch/Assets/Dino/{name}.png"));
        return Sprites[name] = new Bitmap(stream);
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        Engage();
        Game.Press();
        e.Handled = true;
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        Game.ReleaseJump();
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Key is Key.Space or Key.Up or Key.W) { if (!jumpKeyDown) { jumpKeyDown = true; Engage(); Game.Press(); } e.Handled = true; } // holding the key must not press again
        else if (e.Key is Key.Down or Key.S) { Game.Duck(true); e.Handled = true; }
    }

    protected override void OnKeyUp(KeyEventArgs e)
    {
        base.OnKeyUp(e);
        if (e.Key is Key.Space or Key.Up or Key.W) { jumpKeyDown = false; Game.ReleaseJump(); }
        else if (e.Key is Key.Down or Key.S) Game.Duck(false);
    }

    private void Engage() { Focus(); Engaged?.Invoke(); }

    // One step of the game, called for every rendered frame while the page is on screen.
    public void Tick(double seconds)
    {
        Game.Update(seconds, worldWidth);
        if (Game.State == DinoState.Dead && lastState != DinoState.Dead && (int)Game.Score > Best) { Best = (int)Game.Score; NewBest?.Invoke(Best); }
        lastState = Game.State;
        InvalidateVisual();
    }

    private static void Draw(DrawingContext context, string name, double x, double y)
    {
        var bitmap = Sprite(name);
        var (w, h) = (bitmap.PixelSize.Width / 2.0, bitmap.PixelSize.Height / 2.0); // the pictures are twice the game size
        context.DrawImage(bitmap, new Rect(bitmap.Size), new Rect(x, y, w, h));
    }

    public override void Render(DrawingContext context)
    {
        var size = Bounds.Size;
        if (size.Width < 50 || size.Height < 50) return;
        context.DrawRectangle(Brushes.Transparent, null, new Rect(size)); // an empty control is not hit by the pointer; this makes the whole area clickable
        var scale = size.Height / DinoGame.Height;
        worldWidth = size.Width / scale;
        using var _ = context.PushTransform(Matrix.CreateScale(scale, scale));

        foreach (var cloud in Game.Clouds) Draw(context, "Cloud", cloud.X, cloud.Y);

        // the track: a long picture, repeated as it slides by
        var track = Sprite("Track");
        var trackWidth = track.PixelSize.Width / 2.0;
        var offset = Game.Distance % trackWidth;
        for (var x = -offset; x < worldWidth; x += trackWidth) Draw(context, "Track", x, DinoGame.GroundY - 4);

        foreach (var o in Game.Obstacles) Draw(context, o.Sprite(Game.Clock), o.X, o.Top);

        var dino = Game.DinoSprite;
        var dinoHeight = Sprite(dino).PixelSize.Height / 2.0;
        Draw(context, dino, DinoGame.DinoX, DinoGame.GroundY - Game.Lift - dinoHeight);

        DrawTexts(context);
    }

    private void DrawTexts(DrawingContext context)
    {
        void Text(string text, double size, IBrush brush, double x, double y, bool right = false, bool center = false)
        {
            var formatted = new FormattedText(text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, Mono, size, brush);
            var left = right ? x - formatted.Width : center ? x - formatted.Width / 2 : x;
            context.DrawText(formatted, new Point(left, y));
        }
        // the score blinks for a moment at every hundred points, as in Chrome
        var shown = Game.Flash <= 0 || (int)(Game.Flash * 6) % 2 == 1;
        var score = shown ? ((int)Game.Score).ToString("00000") : "     ";
        Text(Best > 0 ? $"HI {Best:00000}  {score}" : score, 13, Grey, worldWidth - 12, 8, right: true);
        var mid = worldWidth / 2;
        switch (Game.State)
        {
            case DinoState.Ready: Text(Loc.T("Dino.Start"), 12.5, Dim, mid, 44, center: true); break;
            case DinoState.Paused: Text(Loc.T("Dino.Paused"), 12.5, Dim, mid, 44, center: true); break;
            case DinoState.Dead:
                var over = Sprite("GameOver"); var reset = Sprite("Reset");
                Draw(context, "GameOver", mid - over.PixelSize.Width / 4.0, 36);
                Draw(context, "Reset", mid - reset.PixelSize.Width / 4.0, 62);
                break;
        }
    }
}
