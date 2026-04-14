using System;
using UnityEngine;

namespace NOFFB2;

public sealed class FFPerlinEffect : FFEffect
{
    private const float LowFrequencyHz = 8f;
    private const float HighFrequencyHz = 16f;
    private const float LowNoiseAmplitude = 0.03f;
    private const float HighNoiseAmplitude = 0.01f;
    private const float LowShakeThreshold = 0.001f;
    private const float HighShakeThreshold = 0.005f;
    private const float LowShakeDecayPerSecond = 5f;
    private const float HighShakeDecayPerSecond = 4f;
    private const float ForceScale = 10f;

    private readonly object _sync = new();

    private float _lowFreqShake;
    private float _highFreqShake;
    private float _lastSampleTime;
    private bool _hasSampleTime;

    private float offset;

    public FFPerlinEffect() : base(float.MaxValue)
    {
        offset = (float)(Plugin.I.RNG.NextDouble() * 10);
    }

    public void AddShake(float lowFreqShake, float highFreqShake)
    {
        lock (_sync)
        {
            _lowFreqShake += lowFreqShake;
            _highFreqShake += highFreqShake;
        }
    }

    public override float Calculate(long elapsed)
    {
        var force = ActualCalculate(elapsed);
        return force * 3; // TODO: Make this a config constant that can be changed by the user
    }

    private float ActualCalculate(long elapsed)
    {
        var currentTime = GetElapsedSeconds(elapsed) + offset;

        lock (_sync)
        {
            var deltaTime = !_hasSampleTime ? 0f : Mathf.Max(0f, currentTime - _lastSampleTime);
            _hasSampleTime = true;
            _lastSampleTime = currentTime;

            _lowFreqShake = Mathf.Min(_lowFreqShake, 1f);
            _highFreqShake = Mathf.Min(_highFreqShake, 1f);

            if (_lowFreqShake < LowShakeThreshold && _highFreqShake < HighShakeThreshold)
            {
                Decay(deltaTime);
                return 0f;
            }

            var lowNoise = SampleNoise(currentTime, LowFrequencyHz) * Mathf.Max(_lowFreqShake - LowShakeThreshold, 0f) * LowNoiseAmplitude;
            var highNoise = SampleNoise(currentTime, HighFrequencyHz) * Mathf.Max(_highFreqShake - HighShakeThreshold, 0f) * HighNoiseAmplitude;

            Decay(deltaTime);
            
            return Mathf.Clamp((lowNoise + highNoise) * ForceScale, -1f, 1f);
        }
    }

    private void Decay(float deltaTime)
    {
        _lowFreqShake = Mathf.Lerp(_lowFreqShake, 0f, LowShakeDecayPerSecond * deltaTime);
        _highFreqShake = Mathf.Lerp(_highFreqShake, 0f, HighShakeDecayPerSecond * deltaTime);
    }

    private static float SampleNoise(float time, float frequency)
    {
        return
            (Mathf.PerlinNoise1D(time * frequency * 2f) - 0.5f) +
            (Mathf.PerlinNoise1D(time * frequency * 1.6666667f) - 0.5f) +
            (Mathf.PerlinNoise1D(time * frequency * 1.2107f) - 0.5f);
    }
}
