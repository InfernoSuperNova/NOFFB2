using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using SharpDX.DirectInput;
using UnityEngine;
using ThreadPriority = System.Threading.ThreadPriority;

namespace NOFFB2;


/// <summary>
/// Handles composition of effects
/// </summary>
public class FFServer
{
    private volatile bool _running = true;
    private readonly object _effectListLock = new object();
    private readonly List<FFEffect> _activeEffects = new();
    private readonly Stopwatch _sw;

    private DirectInputDeviceContext _deviceContext;
    private Thread _workerThread;
    private Effect _constantForceEffect;
    private float _lastAppliedForce = float.NaN;

    public FFServer()
    {
        _sw = Stopwatch.StartNew();
        I = this;
    }
    
    
    public static FFServer I { get; private set; }

    
    /// <summary>
    /// Thread safe to be called by Unity
    /// </summary>
    /// <param name="effect"></param>
    public void AddEffect(FFEffect effect)
    {
        effect.Start(_sw.ElapsedTicks, false);
    
        lock (_effectListLock)
        {
            _activeEffects.Add(effect);
        }
    }

    public void Start()
    {
        if (_workerThread != null)
        {
            return;
        }

        _deviceContext = DirectInputDeviceContext.TryCreate();
        if (_deviceContext != null)
        {
            TryCreateConstantForceEffect();
        }

        _workerThread = new Thread(WorkerLoop)
        {
            Priority = ThreadPriority.Highest,
            IsBackground = true,
            Name = "NOFFB2.DirectInput"
        };
        _workerThread.Start();
    }

    public void Stop()
    {
        _running = false;
        try
        {
            _workerThread?.Join(250);
        }
        catch
        {
            // ignored during shutdown
        }

        DisposeEffects();
        _deviceContext?.Dispose();
        _deviceContext = null;
        _workerThread = null;
    }

    private void WorkerLoop()
    {
        while (_running)
        {
            ProcessOutput();
            Thread.Sleep(1);
        }
    }

    private bool TryCreateConstantForceEffect()
    {
        if (_deviceContext == null || _deviceContext.EffectAxes.Length == 0)
        {
            Plugin.Logger?.LogWarning("DirectInput constant force effect skipped: no actuator objects found.");
            return false;
        }

        try
        {
            var parameters = CreateConstantForceParameters(0);
            _constantForceEffect = new Effect(_deviceContext.Device, EffectGuid.ConstantForce, parameters);
            _constantForceEffect.Download();
            _constantForceEffect.Start(-1);
            _lastAppliedForce = 0f;

            Plugin.Logger?.LogInfo($"Created DirectInput constant-force effect on `{_deviceContext.DeviceName}`.");
            return true;
        }
        catch (Exception ex)
        {
            Plugin.Logger?.LogWarning($"Failed to create DirectInput constant-force effect: {ex.Message}");
            DisposeEffects();
            return false;
        }
    }

    private EffectParameters CreateConstantForceParameters(int magnitude)
    {
        return new EffectParameters
        {
            Flags = EffectFlags.ObjectIds | EffectFlags.Cartesian,
            Duration = -1,
            Gain = 10_000,
            SamplePeriod = 0,
            TriggerButton = -1,
            TriggerRepeatInterval = int.MaxValue,
            StartDelay = 0,
            Axes = _deviceContext?.EffectAxes ?? Array.Empty<int>(),
            Directions = _deviceContext?.EffectDirections ?? Array.Empty<int>(),
            Envelope = null,
            Parameters = new SharpDX.DirectInput.ConstantForce { Magnitude = magnitude }
        };
    }

    public bool PlayNativeSine(float durationSeconds, float frequencyHz, float amplitude)
    {
        if (_deviceContext == null || _deviceContext.EffectAxes.Length == 0)
        {
            Plugin.Logger?.LogWarning("Native sine skipped: no DirectInput force-feedback device/effect axis is ready.");
            return false;
        }

        if (!_deviceContext.SupportsEffect(EffectGuid.Sine))
        {
            Plugin.Logger?.LogWarning($"Native sine skipped on `{_deviceContext.DeviceName}`: device does not advertise GUID_Sine.");
            return false;
        }

        durationSeconds = Mathf.Max(0.01f, durationSeconds);
        frequencyHz = Mathf.Clamp(frequencyHz, 1f, 1000f);
        amplitude = Mathf.Clamp01(amplitude);

        var periodMicroseconds = Math.Max(1, Mathf.RoundToInt(1_000_000f / frequencyHz));
        var durationMicroseconds = Math.Max(1, Mathf.RoundToInt(durationSeconds * 1_000_000f));
        var magnitude = Mathf.RoundToInt(amplitude * 10_000f);

        try
        {
            var effect = new Effect(_deviceContext.Device, EffectGuid.Sine, new EffectParameters
            {
                Flags = EffectFlags.ObjectIds | EffectFlags.Cartesian,
                Duration = durationMicroseconds,
                Gain = 10_000,
                SamplePeriod = 0,
                TriggerButton = -1,
                TriggerRepeatInterval = int.MaxValue,
                StartDelay = 0,
                Axes = _deviceContext.EffectAxes,
                Directions = _deviceContext.EffectDirections,
                Envelope = null,
                Parameters = new PeriodicForce
                {
                    Magnitude = magnitude,
                    Offset = 0,
                    Phase = 0,
                    Period = periodMicroseconds
                }
            });

            try
            {
                effect.Download();
                effect.Start(1);
                Plugin.Logger?.LogInfo(
                    $"Playing native DirectInput sine on `{_deviceContext.DeviceName}`: duration={durationSeconds:F2}s freq={frequencyHz:F1}Hz amp={amplitude:F2}");
                Thread.Sleep(Mathf.CeilToInt(durationSeconds * 1000f) + 50);
                effect.Stop();
            }
            finally
            {
                try
                {
                    effect.Unload();
                }
                catch
                {
                    // ignored during cleanup
                }

                effect.Dispose();
            }

            return true;
        }
        catch (Exception ex)
        {
            Plugin.Logger?.LogWarning($"Failed to play native DirectInput sine on `{_deviceContext.DeviceName}`: {ex.Message}");
            return false;
        }
    }

    private void DisposeEffects()
    {
        if (_constantForceEffect == null)
        {
            return;
        }

        try
        {
            _constantForceEffect.Stop();
        }
        catch
        {
            // ignored during shutdown
        }

        try
        {
            _constantForceEffect.Unload();
        }
        catch
        {
            // ignored during shutdown
        }

        _constantForceEffect.Dispose();
        _constantForceEffect = null;
        _lastAppliedForce = float.NaN;
    }

    private void ProcessOutput()
    {
        float totalForce = 0f;
        long time = _sw.ElapsedTicks;
        lock (_effectListLock)
        {
            for (var index = _activeEffects.Count - 1; index >= 0; index--)
            {
                var effect = _activeEffects[index];
                if (time > effect.KillTime)
                {
                    _activeEffects.RemoveAt(index);
                    continue;
                }

                totalForce += effect.Calculate(time);
            }
        }

        ApplyForce(totalForce);
    }

    private void ApplyForce(float totalForce)
    {
        totalForce = Mathf.Clamp(totalForce, -1f, 1f);

        if (_deviceContext == null || _constantForceEffect == null)
        {
            return;
        }

        if (!float.IsNaN(_lastAppliedForce) && Mathf.Abs(totalForce - _lastAppliedForce) < 0.001f)
        {
            return;
        }

        int magnitude = Mathf.RoundToInt(totalForce * 10_000f);
        try
        {
            _constantForceEffect.SetParameters(
                CreateConstantForceParameters(magnitude),
                EffectParameterFlags.TypeSpecificParameters |
                EffectParameterFlags.Direction);
            _lastAppliedForce = totalForce;
        }
        catch (Exception ex)
        {
            Plugin.Logger?.LogWarning($"Failed to update DirectInput constant force on `{_deviceContext.DeviceName}`: {ex.Message}");
        }
    }
}
