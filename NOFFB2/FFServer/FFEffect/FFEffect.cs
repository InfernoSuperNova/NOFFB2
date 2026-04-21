using System.Diagnostics;
using UnityEngine;

namespace NOFFB2;

public abstract class FFEffect
{
    private long _startTime = 0;
    private long _duration = 0;
    private float _durationSeconds = 0;

    
    
    public FFEffect(float durationSeconds)
    {
        _durationSeconds = durationSeconds;
    }

    public bool Continuous { get; private set; }
    public long KillTime => Continuous ? long.MaxValue : _startTime + _duration;



    public void Start(long elapsed, bool continuous)
    {
        Continuous = continuous;
        _startTime = elapsed;
        _duration = (long)(_durationSeconds * Stopwatch.Frequency);
    }
    

    public void Extend(float seconds) => _duration += (long)(seconds * Stopwatch.Frequency);
    
    
    protected float GetNormalizedTime(long currentTicks)
    {
        if (_duration == 0) return 1.0f;
        return (float)(currentTicks - _startTime) / _duration;
    }

    // Helper to get actual elapsed seconds since start for Sine math
    protected float GetElapsedSeconds(long currentTicks)
    {
        return (float)(currentTicks - _startTime) / Stopwatch.Frequency;
    }

    public abstract float Calculate(long elapsed, float currentPos);
}