using UnityEngine;

namespace NOFFB2;

public class FFSine : FFLinear
{
    private float _frequency;
    private float _offset = 0;
    public FFSine(float durationSeconds, float frequency, float amplitude) : base(durationSeconds, amplitude)
    {
        _frequency = frequency;
        _offset = (float)Plugin.I.RNG.NextDouble();
    }
    
    public override float Calculate(long ticks) 
    {
        float t = GetElapsedSeconds(ticks);
        return Mathf.Sin(_offset + t * _frequency * 2 * Mathf.PI) * _force;
    }
}