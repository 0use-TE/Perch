namespace Perch.Services.Games;

public enum DinoState { Ready, Running, Paused, Dead }
public enum ObstacleKind { SmallCactus, LargeCactus, Bird }

// A cactus group or a bird. Size is how many cacti stand together (1 to 3); for a bird it picks the flight height.
public sealed class Obstacle(ObstacleKind kind, int size, double x, double top, double gap)
{
    public ObstacleKind Kind { get; } = kind;
    public int Size { get; } = size;
    public double X { get; set; } = x;
    public double Top { get; } = top;
    // Free room that must follow this obstacle before the next one may appear.
    public double Gap { get; } = gap;
    public string Sprite(double clock) => Kind switch
    {
        ObstacleKind.SmallCactus => "SmallCactus" + Size,
        ObstacleKind.LargeCactus => "LargeCactus" + Size,
        _ => (int)(clock * 6) % 2 == 0 ? "Bird1" : "Bird2", // the wings flap six times a second
    };
    public int Width => DinoGame.SizeOf(Sprite(0)).Width;
}

public readonly record struct Cloud(double X, double Y);

// The Chrome offline dinosaur game: same sizes, speeds, jump, ducking, birds and spacing, with no drawing in it. The world
// is Height units tall (a unit is one pixel of the original 1x game), the ground is at GroundY, the dinosaur runs in place
// and the obstacles come toward it. Everything moves by the elapsed time, so the game does not depend on the frame rate.
// Collisions compare the real silhouettes of the sprites (see DinoMasks).
public sealed class DinoGame
{
    public const double Height = 150, GroundY = 136, DinoX = 36;

    // Chrome's numbers, per frame at 60 fps, turned into per second
    private const double Gravity = 0.6 * 3600, JumpStart = 10 * 60, StartSpeed = 6 * 60, MaxSpeed = 13 * 60;
    private const double Acceleration = 0.001 * 3600, SpeedDrop = 3, MinGap = 120, BirdSpeed = 8.5 * 60;
    private const double ScoreCoefficient = 0.025;
    private static readonly double[] BirdTops = [100, 70, 45];

    private static readonly Dictionary<string, bool[]> Cache = [];
    private readonly Random random = new();
    private readonly List<Obstacle> obstacles = [];
    private readonly List<Cloud> clouds = [];
    private double lift, rise, untilFirst = 2.0, cloudGap = 150, deadFor;
    private bool ducking, diving, jumpHeld;
    private int lastHundred;

    public DinoState State { get; private set; } = DinoState.Ready;
    public double Speed { get; private set; } = StartSpeed;
    public double Distance { get; private set; }
    public double Score => Distance * ScoreCoefficient;
    public double Clock { get; private set; }
    // Seconds the score blinks after every hundred points.
    public double Flash { get; private set; }
    public IReadOnlyList<Obstacle> Obstacles => obstacles;
    public IReadOnlyList<Cloud> Clouds => clouds;
    public double Lift => lift;
    public bool OnGround => lift <= 0.001;
    public bool Ducking => ducking && OnGround;

    public static (int Width, int Height) SizeOf(string sprite) => DinoMasks.All.TryGetValue(sprite, out var m) ? (m.Width, m.Height) : (0, 0);

    // The dinosaur's picture right now (running and ducking alternate their legs about twelve times a second).
    public string DinoSprite
    {
        get
        {
            if (State == DinoState.Dead) return "DinoDead";
            if (State == DinoState.Ready || State == DinoState.Paused) return "DinoRun1";
            if (!OnGround) return "DinoJump";
            var step = (int)(Clock * 12) % 2 + 1;
            return Ducking ? "DinoDuck" + step : "DinoRun" + step;
        }
    }

    // Space, up arrow or a click: start, jump, or begin again.
    public void Press()
    {
        switch (State)
        {
            case DinoState.Ready:
                Start();
                Jump();
                break;
            case DinoState.Dead:
                if (deadFor > 0.5) { Start(); Jump(); } // a moment before a new run, so a late click does not skip the screen
                break;
            case DinoState.Paused:
                State = DinoState.Running;
                break;
            case DinoState.Running:
                Jump();
                break;
        }
    }

    // Letting go of the jump key early makes a lower jump.
    public void ReleaseJump()
    {
        jumpHeld = false;
        if (rise > 5 * 60) rise = 5 * 60;
    }

    // Down arrow: duck on the ground, fall faster in the air.
    public void Duck(bool on)
    {
        ducking = on;
        diving = on;
    }

    public void Pause() { if (State == DinoState.Running) State = DinoState.Paused; }

    private void Jump()
    {
        if (!OnGround || Ducking) return;
        rise = JumpStart + Speed / 60 * 6; // faster runs jump a little higher
        lift = 0.01;
        jumpHeld = true;
    }

    private void Start()
    {
        obstacles.Clear();
        lift = 0; rise = 0; Speed = StartSpeed; Distance = 0; untilFirst = 2.0; lastHundred = 0; Flash = 0; deadFor = 0;
        State = DinoState.Running;
    }

    // Moves the game on by `seconds`; `worldWidth` is how far to the right things come in from.
    public void Update(double seconds, double worldWidth)
    {
        Clock += seconds;
        if (State == DinoState.Dead) deadFor += seconds;
        if (State != DinoState.Running) return;

        // the dinosaur: gravity, with a faster fall when ducking in the air
        if (!OnGround || rise > 0)
        {
            rise -= Gravity * (diving ? SpeedDrop : 1) * seconds;
            lift += rise * seconds;
            if (lift <= 0) { lift = 0; rise = 0; }
        }

        var step = Speed * seconds;
        Distance += step;
        Speed = Math.Min(MaxSpeed, Speed + Acceleration * seconds);
        var hundred = (int)(Score / 100);
        if (hundred > lastHundred) { lastHundred = hundred; Flash = 1.0; }
        if (Flash > 0) Flash = Math.Max(0, Flash - seconds);

        MoveClouds(seconds, worldWidth);
        MoveObstacles(step, worldWidth);
        if (Hit()) { State = DinoState.Dead; deadFor = 0; }
    }

    // Slow clouds drift past behind everything.
    private void MoveClouds(double seconds, double worldWidth)
    {
        for (var i = 0; i < clouds.Count; i++) clouds[i] = clouds[i] with { X = clouds[i].X - Speed * 0.2 * seconds };
        clouds.RemoveAll(c => c.X < -60);
        if (clouds.Count < 6 && (clouds.Count == 0 || worldWidth - clouds[^1].X > cloudGap))
        {
            clouds.Add(new Cloud(worldWidth + 10, 30 + random.Next(0, 42)));
            cloudGap = 100 + random.Next(0, 300);
        }
    }

    private void MoveObstacles(double step, double worldWidth)
    {
        foreach (var o in obstacles) o.X -= step;
        obstacles.RemoveAll(o => o.X + o.Width < -10);
        if (untilFirst > 0) { untilFirst -= step / Speed; if (untilFirst > 0) return; }
        var last = obstacles.Count == 0 ? null : obstacles[^1];
        if (last != null && last.X + last.Width + last.Gap > worldWidth) return;
        obstacles.Add(NewObstacle(worldWidth + 10));
    }

    // What comes next: cacti, and birds once the run is fast enough. The room left after it grows with its width and the speed.
    private Obstacle NewObstacle(double x)
    {
        ObstacleKind kind;
        int size = 1;
        double top;
        if (Speed >= BirdSpeed && random.NextDouble() < 0.28)
        {
            kind = ObstacleKind.Bird;
            top = BirdTops[random.Next(BirdTops.Length)];
        }
        else
        {
            kind = random.Next(2) == 0 ? ObstacleKind.SmallCactus : ObstacleKind.LargeCactus;
            size = random.Next(1, 4);
            var height = SizeOf((kind == ObstacleKind.SmallCactus ? "SmallCactus" : "LargeCactus") + size).Height;
            top = GroundY - height;
        }
        var probe = new Obstacle(kind, size, x, top, 0);
        var width = probe.Width;
        var gap = Math.Round(width * (Speed / 60) + MinGap * 0.6);
        gap = gap + random.NextDouble() * gap * 0.5;
        return new Obstacle(kind, size, x, top, gap);
    }

    // The dinosaur against every obstacle: they hit when two filled cells of the two silhouettes overlap.
    private bool Hit()
    {
        var dino = DinoSprite;
        var (dw, dh) = SizeOf(dino);
        var dx = DinoX;
        var dy = GroundY - lift - dh;
        foreach (var o in obstacles)
        {
            var sprite = o.Sprite(Clock);
            var (ow, oh) = SizeOf(sprite);
            if (dx + dw < o.X || dx > o.X + ow || dy + dh < o.Top || dy > o.Top + oh) continue;
            if (Overlap(dino, (int)Math.Round(dx), (int)Math.Round(dy), sprite, (int)Math.Round(o.X), (int)Math.Round(o.Top))) return true;
        }
        return false;
    }

    private static bool[] Mask(string name)
    {
        if (Cache.TryGetValue(name, out var cached)) return cached;
        var (w, h, bits) = DinoMasks.All[name];
        var cells = new bool[w * h];
        for (var i = 0; i < cells.Length; i++)
        {
            var digit = Convert.ToInt32(bits[i / 4].ToString(), 16);
            cells[i] = (digit >> (3 - i % 4) & 1) == 1;
        }
        return Cache[name] = cells;
    }

    private static bool Overlap(string a, int ax, int ay, string b, int bx, int by)
    {
        var (aw, ah) = SizeOf(a);
        var (bw, bh) = SizeOf(b);
        var left = Math.Max(ax, bx); var right = Math.Min(ax + aw, bx + bw);
        var top = Math.Max(ay, by); var bottom = Math.Min(ay + ah, by + bh);
        if (left >= right || top >= bottom) return false;
        var ma = Mask(a); var mb = Mask(b);
        var touching = 0;
        for (var y = top; y < bottom; y++)
            for (var x = left; x < right; x++)
                if (ma[(y - ay) * aw + (x - ax)] && mb[(y - by) * bw + (x - bx)] && ++touching >= 2) return true; // two cells: a touch of one pixel does not count
        return false;
    }
}
