using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using BepInEx.Logging;
using SharpDX.DirectInput;
using UnityEngine;
using ThreadPriority = System.Threading.ThreadPriority;

namespace NOFFB2;

/// <summary>
/// Handles composition of effects.
/// </summary>
public class FFServer
{
    private volatile bool _running = true;
    private readonly object _effectListLock = new();
    private readonly Dictionary<FFAxis, List<FFEffect>> _activeEffects = new();
    private readonly Dictionary<FFAxis, float> _toApplyForces = new();
    private readonly Stopwatch _sw;

    private DirectInputDeviceContext _deviceContext;
    private Effect _constantForceEffect;
    private Vector2 _lastAppliedVector = new(float.NaN, float.NaN);
    private Thread _workerThread;

    public FFServer()
    {
        _sw = Stopwatch.StartNew();
        I = this;

        foreach (FFAxis axis in Enum.GetValues(typeof(FFAxis)))
        {
            _activeEffects[axis] = new List<FFEffect>();
            _toApplyForces[axis] = 0f;
        }
    }

    public static FFServer I { get; private set; }

    /// <summary>
    /// Thread safe to be called by Unity.
    /// </summary>
    public void AddEffect(FFEffect effect, FFAxis axis)
    {
        effect.Start(_sw.ElapsedTicks, false);

        lock (_effectListLock)
        {
            _activeEffects[axis].Add(effect);
        }
    }

    public void AddContinuousEffect(FFEffect effect, FFAxis axis)
    {
        effect.Start(_sw.ElapsedTicks, true);
        Log($"Adding continuous effect of type {effect.GetType()} on axis {axis}");

        lock (_effectListLock)
        {
            _activeEffects[axis].Add(effect);
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
        if (_deviceContext == null)
        {
            Plugin.Logger?.LogWarning("DirectInput constant force effect skipped: no axis bindings found.");
            return false;
        }

        if (!_deviceContext.TryGetAxisBinding(FFAxis.Roll, out var rollBinding) ||
            !_deviceContext.TryGetAxisBinding(FFAxis.Pitch, out var pitchBinding))
        {
            Plugin.Logger?.LogWarning("DirectInput constant force effect skipped: roll/pitch bindings are not both available.");
            return false;
        }

        try
        {
            var parameters = CreateConstantForceParameters(
                new[] { rollBinding.EffectAxes[0], pitchBinding.EffectAxes[0] },
                new[] { 0, 0 },
                0);
            _constantForceEffect = new Effect(_deviceContext.Device, EffectGuid.ConstantForce, parameters);
            _constantForceEffect.Download();
            _constantForceEffect.Start(-1);
            _lastAppliedVector = Vector2.zero;

            Plugin.Logger?.LogInfo($"Created shared DirectInput 2-axis constant-force effect on `{_deviceContext.DeviceName}`.");
            return true;
        }
        catch (Exception ex)
        {
            Plugin.Logger?.LogWarning($"Failed to create DirectInput constant-force effect: {ex.Message}");
            DisposeEffects();
            return false;
        }
    }

    private static EffectParameters CreateConstantForceParameters(int[] axes, int[] directions, int magnitude)
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
            Axes = axes,
            Directions = directions,
            Envelope = null,
            Parameters = new ConstantForce { Magnitude = magnitude }
        };
    }

    public bool PlayNativeSine(float durationSeconds, float frequencyHz, float amplitude)
    {
        if (_deviceContext == null || _deviceContext.AxisBindings.Count == 0)
        {
            Plugin.Logger?.LogWarning("Native sine skipped: no DirectInput force-feedback axis binding is ready.");
            return false;
        }

        if (!_deviceContext.SupportsEffect(EffectGuid.Sine))
        {
            Plugin.Logger?.LogWarning($"Native sine skipped on `{_deviceContext.DeviceName}`: device does not advertise GUID_Sine.");
            return false;
        }

        if (!_deviceContext.TryGetAxisBinding(FFAxis.Pitch, out var binding) &&
            !_deviceContext.TryGetAxisBinding(FFAxis.Roll, out binding))
        {
            Plugin.Logger?.LogWarning($"Native sine skipped on `{_deviceContext.DeviceName}`: no usable axis binding found.");
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
                Axes = binding.EffectAxes,
                Directions = binding.EffectDirections,
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

    public bool PlayNativeConstantVector(float durationSeconds, float rollForce, float pitchForce)
    {
        if (_deviceContext == null)
        {
            Plugin.Logger?.LogWarning("Native constant vector skipped: no DirectInput device is ready.");
            return false;
        }

        if (!_deviceContext.TryGetAxisBinding(FFAxis.Roll, out var rollBinding) ||
            !_deviceContext.TryGetAxisBinding(FFAxis.Pitch, out var pitchBinding))
        {
            Plugin.Logger?.LogWarning($"Native constant vector skipped on `{_deviceContext.DeviceName}`: roll/pitch bindings are not both available.");
            return false;
        }

        durationSeconds = Mathf.Max(0.01f, durationSeconds);
        var vector = new Vector2(rollForce, pitchForce);
        var magnitude01 = Mathf.Clamp01(vector.magnitude);
        if (magnitude01 <= 0.0001f)
        {
            return false;
        }

        var normalized = vector.normalized;
        var magnitude = Mathf.RoundToInt(magnitude01 * 10_000f);
        var directions = new[]
        {
            Mathf.RoundToInt(normalized.x * 10_000f),
            Mathf.RoundToInt(normalized.y * 10_000f)
        };

        try
        {
            var effect = new Effect(_deviceContext.Device, EffectGuid.ConstantForce, new EffectParameters
            {
                Flags = EffectFlags.ObjectIds | EffectFlags.Cartesian,
                Duration = Mathf.RoundToInt(durationSeconds * 1_000_000f),
                Gain = 10_000,
                SamplePeriod = 0,
                TriggerButton = -1,
                TriggerRepeatInterval = int.MaxValue,
                StartDelay = 0,
                Axes = new[] { rollBinding.EffectAxes[0], pitchBinding.EffectAxes[0] },
                Directions = directions,
                Envelope = null,
                Parameters = new ConstantForce { Magnitude = magnitude }
            });

            effect.Download();
            effect.Start(1);

            Plugin.Logger?.LogInfo(
                $"Playing native constant vector on `{_deviceContext.DeviceName}`: roll={rollForce:F2} pitch={pitchForce:F2} duration={durationSeconds:F2}s directions=[{directions[0]},{directions[1]}] magnitude={magnitude}");

            ThreadPool.QueueUserWorkItem(_ =>
            {
                try
                {
                    Thread.Sleep(Mathf.CeilToInt(durationSeconds * 1000f) + 50);
                    effect.Stop();
                }
                catch
                {
                    // ignored during cleanup
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
            });

            return true;
        }
        catch (Exception ex)
        {
            Plugin.Logger?.LogWarning($"Failed to play native constant vector on `{_deviceContext.DeviceName}`: {ex.Message}");
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
        _lastAppliedVector = new Vector2(float.NaN, float.NaN);
    }

    private void ProcessOutput()
    {
        long time = _sw.ElapsedTicks;
        lock (_effectListLock)
        {
            foreach (var kvp in _activeEffects)
            {
                var axis = kvp.Key;
                _toApplyForces[axis] = 0f;
                var effects = kvp.Value;
                for (var index = effects.Count - 1; index >= 0; index--)
                {
                    var effect = effects[index];
                    if (!effect.Continuous && time > effect.KillTime)
                    {
                        effects.RemoveAt(index);
                        continue;
                    }

                    _toApplyForces[axis] += effect.Calculate(time);
                }
            }
        }

        ApplyForces(_toApplyForces);
    }

    private void ApplyForces(IReadOnlyDictionary<FFAxis, float> forces)
    {
        if (_deviceContext == null || _constantForceEffect == null)
        {
            return;
        }

        var rollForce = Mathf.Clamp(forces.TryGetValue(FFAxis.Roll, out var roll) ? roll : 0f, -1f, 1f);
        var pitchForce = Mathf.Clamp(forces.TryGetValue(FFAxis.Pitch, out var pitch) ? pitch : 0f, -1f, 1f);
        var vector = new Vector2(rollForce, pitchForce);

        if (!float.IsNaN(_lastAppliedVector.x) &&
            !float.IsNaN(_lastAppliedVector.y) &&
            Mathf.Abs(vector.x - _lastAppliedVector.x) < 0.001f &&
            Mathf.Abs(vector.y - _lastAppliedVector.y) < 0.001f)
        {
            return;
        }

        var magnitude = Mathf.RoundToInt(Mathf.Clamp01(vector.magnitude) * 10_000f);
        var directions = magnitude == 0
            ? new[] { 0, 0 }
            : new[]
            {
                Mathf.RoundToInt(vector.normalized.x * 10_000f),
                Mathf.RoundToInt(vector.normalized.y * 10_000f)
            };

        try
        {
            if (!_deviceContext.TryGetAxisBinding(FFAxis.Roll, out var rollBinding) ||
                !_deviceContext.TryGetAxisBinding(FFAxis.Pitch, out var pitchBinding))
            {
                return;
            }

            _constantForceEffect.SetParameters(
                CreateConstantForceParameters(
                    new[] { rollBinding.EffectAxes[0], pitchBinding.EffectAxes[0] },
                    directions,
                    magnitude),
                EffectParameterFlags.TypeSpecificParameters | EffectParameterFlags.Direction);
            _lastAppliedVector = vector;
        }
        catch (Exception ex)
        {
            Log($"Failed to update DirectInput constant force on `{_deviceContext.DeviceName}` vector [{vector.x:F3}, {vector.y:F3}]: {ex.Message}", LogLevel.Warning);
        }
    }

    private static void Log(object log, LogLevel level = LogLevel.Info) => Plugin.Log($"FFServer : {log}", level);
}
