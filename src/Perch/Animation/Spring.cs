namespace Perch.Animation;

// A damped spring tuned by feel: Response is seconds per oscillation, Damping 1 is critical (no bounce), below 1 bounces.
// Changing Target, Response or Damping mid-flight keeps Value and Velocity, so reversals stay continuous.
public struct Spring
{
    public double Value, Velocity, Target, Response, Damping;
    public void Step(double h)
    {
        var k = Math.Pow(2 * Math.PI / Response, 2);
        var c = 4 * Math.PI * Damping / Response;
        Velocity += ((Target - Value) * k - Velocity * c) * h;
        Value += Velocity * h;
    }
    public readonly bool Settled => Math.Abs(Target - Value) < 0.001 && Math.Abs(Velocity) < 0.005;
    public void Snap() { Value = Target; Velocity = 0; }
}
