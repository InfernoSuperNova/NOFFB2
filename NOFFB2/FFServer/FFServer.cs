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
    private const int TargetUpdateHz = 250;
    private static readonly long TargetUpdateTicks = Stopwatch.Frequency / TargetUpdateHz;

    private volatile bool _running = true;
    private readonly object _effectListLock = new();
    private readonly Dictionary<FFAxis, List<FFEffect>> _activeEffects = new();
    private readonly Dictionary<FFAxis, float> _toApplyForces = new();
    private readonly Dictionary<FFAxis, float> _currentPositions = new();
    private readonly Stopwatch _sw;

    private DirectInputDeviceContext _deviceContext;
    private readonly Dictionary<FFAxis, Effect> _constantForceEffects = new();
    private readonly Dictionary<FFAxis, float> _lastAppliedForces = new();
    private Thread _workerThread;

    public FFServer()
    {
        _sw = Stopwatch.StartNew();
        I = this;

        foreach (FFAxis axis in Enum.GetValues(typeof(FFAxis)))
        {
            _activeEffects[axis] = new List<FFEffect>();
            _toApplyForces[axis] = 0f;
            _currentPositions[axis] = 0f;
            _lastAppliedForces[axis] = float.NaN;
        }
    }

    public static FFServer I { get; private set; }

    public bool TryGetAxisDebugSnapshot(out string snapshot)
    {
        snapshot = null;
        if (_deviceContext == null || !_deviceContext.TryGetAxisDebugSnapshot(out var rawSnapshot))
        {
            return false;
        }

        snapshot = rawSnapshot.ToString();
        return true;
    }

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
            TryCreateConstantForceEffects();
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
        long nextTick = Stopwatch.GetTimestamp();

        while (_running)
        {
            ProcessOutput();

            nextTick += TargetUpdateTicks;
            long now = Stopwatch.GetTimestamp();
            long remainingTicks = nextTick - now;

            if (remainingTicks <= 0)
            {
                if (-remainingTicks > TargetUpdateTicks * 4)
                {
                    nextTick = now;
                }

                continue;
            }

            int sleepMilliseconds = (int)(remainingTicks * 1000 / Stopwatch.Frequency);
            if (sleepMilliseconds > 1)
            {
                Thread.Sleep(sleepMilliseconds - 1);
            }

            while (_running && Stopwatch.GetTimestamp() < nextTick)
            {
                Thread.SpinWait(32);
            }
        }
    }

    private bool TryCreateConstantForceEffects()
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
            foreach (FFAxis axis in Enum.GetValues(typeof(FFAxis)))
            {
                var effect = new Effect(
                    _deviceContext.Device,
                    EffectGuid.ConstantForce,
                    CreateConstantForceParameters(
                        new[] { rollBinding.EffectAxes[0], pitchBinding.EffectAxes[0] },
                        new[] { 0, 0 },
                        0));
                effect.Download();
                effect.Start(-1);
                _constantForceEffects[axis] = effect;
                _lastAppliedForces[axis] = 0f;
                Plugin.Logger?.LogInfo($"Created independent DirectInput constant-force vector effect for {axis} on `{_deviceContext.DeviceName}`.");
            }

            return true;
        }
        catch (Exception ex)
        {
            Plugin.Logger?.LogWarning($"Failed to create DirectInput constant-force effects: {ex.Message}");
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
            Parameters = new SharpDX.DirectInput.ConstantForce { Magnitude = magnitude }
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
                Parameters = new SharpDX.DirectInput.ConstantForce { Magnitude = magnitude }
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
        foreach (var effect in _constantForceEffects.Values)
        {
            try
            {
                effect.Stop();
            }
            catch
            {
                // ignored during shutdown
            }

            try
            {
                effect.Unload();
            }
            catch
            {
                // ignored during shutdown
            }

            effect.Dispose();
        }

        _constantForceEffects.Clear();
        foreach (FFAxis axis in Enum.GetValues(typeof(FFAxis)))
        {
            _lastAppliedForces[axis] = float.NaN;
        }
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
                if (_deviceContext != null && _deviceContext.TryGetAxisPosition(axis, out var currentPos))
                {
                    _currentPositions[axis] = currentPos;
                }

                float currentPosition = _currentPositions[axis];
                for (var index = effects.Count - 1; index >= 0; index--)
                {
                    var effect = effects[index];
                    if (!effect.Continuous && time > effect.KillTime)
                    {
                        effects.RemoveAt(index);
                        continue;
                    }

                    _toApplyForces[axis] += effect.Calculate(time, currentPosition);
                }
            }
        }

        ApplyForces(_toApplyForces);
    }

    private void ApplyForces(IReadOnlyDictionary<FFAxis, float> forces)
    {
        if (_deviceContext == null || _constantForceEffects.Count == 0)
        {
            return;
        }

        foreach (FFAxis axis in Enum.GetValues(typeof(FFAxis)))
        {
            if (!_constantForceEffects.TryGetValue(axis, out var effect) ||
                !_deviceContext.TryGetAxisBinding(FFAxis.Roll, out var rollBinding) ||
                !_deviceContext.TryGetAxisBinding(FFAxis.Pitch, out var pitchBinding))
            {
                continue;
            }

            float force = Mathf.Clamp(forces.TryGetValue(axis, out var value) ? value : 0f, -1f, 1f);
            if (!float.IsNaN(_lastAppliedForces[axis]) && Mathf.Abs(force - _lastAppliedForces[axis]) < 0.001f)
            {
                continue;
            }

            int magnitude = Mathf.RoundToInt(Mathf.Abs(force) * 10_000f);
            int[] directions = axis == FFAxis.Roll
                ? new[] { force >= 0f ? 10_000 : -10_000, 0 }
                : new[] { 0, force >= 0f ? 10_000 : -10_000 };

            try
            {
                effect.SetParameters(
                    CreateConstantForceParameters(
                        new[] { rollBinding.EffectAxes[0], pitchBinding.EffectAxes[0] },
                        directions,
                        magnitude),
                    EffectParameterFlags.TypeSpecificParameters | EffectParameterFlags.Direction);
                _lastAppliedForces[axis] = force;
            }
            catch (Exception ex)
            {
                Log($"Failed to update DirectInput constant force on `{_deviceContext.DeviceName}` axis {axis} force {force:F3}: {ex.Message}", LogLevel.Warning);
            }
        }
    }

    private static void Log(object log, LogLevel level = LogLevel.Info) => Plugin.Log($"FFServer : {log}", level);
}
