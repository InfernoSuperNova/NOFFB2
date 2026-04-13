namespace NOFFB2;

public class FFDecaying : FFLinear
{
    public FFDecaying(float durationSeconds, float force) : base(durationSeconds, force)
    {
    }

    public override float Calculate(long elapsed)
    {
        return base.Calculate(elapsed) * (1 - GetNormalizedTime(elapsed));
    }
}
