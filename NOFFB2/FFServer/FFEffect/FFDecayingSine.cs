namespace NOFFB2;

public class FFDecayingSine : FFSine
{
    public FFDecayingSine(float durationSeconds, float frequency, float amplitude) : base(durationSeconds, frequency, amplitude)
    {
    }
    
    public override float Calculate(long elapsed, float currentPos)
    {
        return base.Calculate(elapsed, currentPos) * (1 - GetNormalizedTime(elapsed));
    }
}
