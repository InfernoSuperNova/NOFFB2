namespace NOFFB2;

public class FFDecaying : FFLinear
{
    public FFDecaying(float durationSeconds, float force) : base(durationSeconds, force)
    {
    }

    public override float Calculate(long elapsed, float currentPos)
    {
        return base.Calculate(elapsed, currentPos) * (1 - GetNormalizedTime(elapsed));
    }
}
