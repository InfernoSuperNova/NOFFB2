namespace NOFFB2;

public class FFSpring : FFEffect
{
    private const float SPRING_GAIN = 1f;
    private const float DAMPING_GAIN = 0.15f;
    private const float MaxDeltaTime = 0.05f;
    private const float VelocityFilterStrength = 0.25f;
    
    private float _lastPos;
    private float _lastTime = float.NaN;
    private float _filteredVelocity;

    public FFSpring(float durationSeconds) : base(durationSeconds)
    {
    }

    public float Gain { get; set; } = 1.0f;
    public float Center { get; set; } = 0.0f;
    private float SpringGain => Gain * SPRING_GAIN;
    private float DampingGain => Gain * DAMPING_GAIN;
    

    public override float Calculate(long elapsed, float currentPos)
    {
        float time = GetElapsedSeconds(elapsed);
        float deltaTime = float.IsNaN(_lastTime) ? 0f : UnityEngine.Mathf.Min(time - _lastTime, MaxDeltaTime);
        float rawVelocity = deltaTime > 0.000001f ? (currentPos - _lastPos) / deltaTime : 0f;
        _filteredVelocity = UnityEngine.Mathf.Lerp(_filteredVelocity, rawVelocity, VelocityFilterStrength);

        _lastPos = currentPos;
        _lastTime = time;

        float springForce = -((currentPos - Center) * SpringGain);
        float damperForce = -(_filteredVelocity * DampingGain);
        return UnityEngine.Mathf.Clamp(springForce + damperForce, -1f, 1f);
    }
}
