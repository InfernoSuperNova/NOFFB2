using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using SharpDX.DirectInput;
using UnityEngine;

namespace NOFFB2;

internal sealed class DirectInputDeviceContext : IDisposable
{
    internal readonly struct AxisDebugSnapshot
    {
        public AxisDebugSnapshot(float x, float y, float z, float rotationX, float rotationY, float rotationZ)
        {
            X = x;
            Y = y;
            Z = z;
            RotationX = rotationX;
            RotationY = rotationY;
            RotationZ = rotationZ;
        }

        public float X { get; }
        public float Y { get; }
        public float Z { get; }
        public float RotationX { get; }
        public float RotationY { get; }
        public float RotationZ { get; }

        public override string ToString()
        {
            return $"X={X:F3} Y={Y:F3} Z={Z:F3} RotX={RotationX:F3} RotY={RotationY:F3} RotZ={RotationZ:F3}";
        }
    }

    internal sealed class AxisBinding
    {
        public AxisBinding(FFAxis axis, int[] effectAxes, int[] effectDirections, string actuatorName)
        {
            Axis = axis;
            EffectAxes = effectAxes;
            EffectDirections = effectDirections;
            ActuatorName = actuatorName;
        }

        public FFAxis Axis { get; }
        public int[] EffectAxes { get; }
        public int[] EffectDirections { get; }
        public string ActuatorName { get; }
    }

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetConsoleWindow();

    private readonly DirectInput _directInput;

    private DirectInputDeviceContext(
        DirectInput directInput,
        Joystick device,
        string deviceName,
        List<EffectInfo> supportedEffects,
        IReadOnlyDictionary<FFAxis, AxisBinding> axisBindings)
    {
        _directInput = directInput;
        Device = device;
        DeviceName = deviceName;
        SupportedEffects = supportedEffects;
        AxisBindings = axisBindings;
    }

    public Joystick Device { get; }
    public string DeviceName { get; }
    public IReadOnlyList<EffectInfo> SupportedEffects { get; }
    public IReadOnlyDictionary<FFAxis, AxisBinding> AxisBindings { get; }

    public bool SupportsEffect(Guid effectGuid)
    {
        foreach (var effectInfo in SupportedEffects)
        {
            if (effectInfo.Guid == effectGuid)
            {
                return true;
            }
        }

        return false;
    }

    public bool TryGetAxisBinding(FFAxis axis, out AxisBinding binding) => AxisBindings.TryGetValue(axis, out binding);

    public bool TryGetAxisPosition(FFAxis axis, out float normalizedPosition)
    {
        normalizedPosition = 0f;

        if (!AxisBindings.TryGetValue(axis, out var binding))
        {
            return false;
        }

        try
        {
            Device.Poll();
            var state = Device.GetCurrentState();
            normalizedPosition = NormalizeAxisValue(GetAxisValue(state, binding.ActuatorName));
            if (axis == FFAxis.Roll)
            {
                normalizedPosition = -normalizedPosition;
            }
            return true;
        }
        catch (Exception ex)
        {
            Plugin.Logger?.LogWarning($"Failed reading DirectInput axis position for {axis} on `{DeviceName}`: {ex.Message}");
            return false;
        }
    }

    public bool TryGetAxisDebugSnapshot(out AxisDebugSnapshot snapshot)
    {
        snapshot = default;

        try
        {
            Device.Poll();
            var state = Device.GetCurrentState();
            snapshot = new AxisDebugSnapshot(
                NormalizeAxisValue(state.X),
                NormalizeAxisValue(state.Y),
                NormalizeAxisValue(state.Z),
                NormalizeAxisValue(state.RotationX),
                NormalizeAxisValue(state.RotationY),
                NormalizeAxisValue(state.RotationZ));
            return true;
        }
        catch (Exception ex)
        {
            Plugin.Logger?.LogWarning($"Failed reading DirectInput debug axis snapshot on `{DeviceName}`: {ex.Message}");
            return false;
        }
    }

    public static DirectInputDeviceContext TryCreate()
    {
        var directInput = new DirectInput();
        var instances = directInput.GetDevices(DeviceClass.GameControl, DeviceEnumerationFlags.AttachedOnly);
        var cooperativeWindow = GetCooperativeWindow();

        Plugin.Logger?.LogInfo($"DirectInput devices found: {instances.Count}");
        Plugin.Logger?.LogInfo($"DirectInput cooperative window handle: 0x{cooperativeWindow.ToInt64():X}");

        foreach (var instance in instances)
        {
            Plugin.Logger?.LogInfo($"- {instance.InstanceName} | Product: {instance.ProductName} | InstanceGuid: {instance.InstanceGuid}");
        }

        foreach (var instance in instances)
        {
            Joystick candidate = null;
            try
            {
                Plugin.Logger?.LogInfo($"Trying DirectInput device `{instance.InstanceName}`...");
                candidate = new Joystick(directInput, instance.InstanceGuid);
                Plugin.Logger?.LogInfo($"`{instance.InstanceName}` created Joystick object.");

                candidate.SetCooperativeLevel(cooperativeWindow, CooperativeLevel.Exclusive | CooperativeLevel.Background);
                Plugin.Logger?.LogInfo($"`{instance.InstanceName}` cooperative level set.");

                candidate.Properties.AxisMode = DeviceAxisMode.Absolute;
                Plugin.Logger?.LogInfo($"`{instance.InstanceName}` axis mode set.");

                Capabilities capabilities;
                try
                {
                    capabilities = candidate.Capabilities;
                    Plugin.Logger?.LogInfo($"`{instance.InstanceName}` capabilities read.");
                }
                catch (Exception ex)
                {
                    Plugin.Logger?.LogWarning($"`{instance.InstanceName}` failed on Capabilities: {ex.Message}");
                    throw;
                }

                IList<DeviceObjectInstance> allObjects;
                try
                {
                    allObjects = candidate.GetObjects();
                    Plugin.Logger?.LogInfo($"`{instance.InstanceName}` GetObjects() returned {allObjects.Count} objects.");
                }
                catch (Exception ex)
                {
                    Plugin.Logger?.LogWarning($"`{instance.InstanceName}` failed on GetObjects(): {ex.Message}");
                    throw;
                }

                var actuators = new List<DeviceObjectInstance>();
                foreach (var obj in allObjects)
                {
                    if (IsProbableActuator(obj))
                    {
                        actuators.Add(obj);
                    }
                }

                actuators.Sort(CompareActuatorPriority);

                List<EffectInfo> supportedEffects;
                try
                {
                    supportedEffects = new List<EffectInfo>(candidate.GetEffects());
                    Plugin.Logger?.LogInfo($"`{instance.InstanceName}` GetEffects() returned {supportedEffects.Count} effects.");
                }
                catch (Exception ex)
                {
                    Plugin.Logger?.LogWarning($"`{instance.InstanceName}` failed on GetEffects(): {ex.Message}");
                    throw;
                }

                var supportsForceFeedback =
                    (capabilities.Flags & DeviceFlags.ForceFeedback) != 0 ||
                    actuators.Count > 0 ||
                    supportedEffects.Count > 0;

                Plugin.Logger?.LogInfo(
                    $"DirectInput candidate `{instance.InstanceName}`: Axes={capabilities.AxeCount}, Buttons={capabilities.ButtonCount}, FF={supportsForceFeedback}, Actuators={actuators.Count}, Effects={supportedEffects.Count}");

                if (!supportsForceFeedback || actuators.Count == 0)
                {
                    candidate.Dispose();
                    continue;
                }

                try
                {
                    candidate.Properties.AutoCenter = false;
                }
                catch (Exception ex)
                {
                    Plugin.Logger?.LogWarning($"Failed to disable autocenter on `{instance.InstanceName}`: {ex.Message}");
                }

                try
                {
                    candidate.Properties.ForceFeedbackGain = 10_000;
                }
                catch (Exception ex)
                {
                    Plugin.Logger?.LogWarning($"Failed to set FF gain on `{instance.InstanceName}`: {ex.Message}");
                }

                try
                {
                    candidate.Acquire();
                }
                catch (Exception ex)
                {
                    Plugin.Logger?.LogWarning($"Acquire failed for `{instance.InstanceName}`: {ex.Message}");
                }

                try
                {
                    candidate.SendForceFeedbackCommand(ForceFeedbackCommand.SetActuatorsOn);
                }
                catch (Exception ex)
                {
                    Plugin.Logger?.LogWarning($"Failed to enable FF actuators on `{instance.InstanceName}`: {ex.Message}");
                }

                var axisBindings = CreateAxisBindings(actuators);
                if (axisBindings.Count == 0)
                {
                    Plugin.Logger?.LogWarning($"`{instance.InstanceName}` has FF support but no usable axis bindings.");
                    candidate.Dispose();
                    continue;
                }

                var context = new DirectInputDeviceContext(
                    directInput,
                    candidate,
                    instance.InstanceName,
                    supportedEffects,
                    axisBindings);

                context.LogSelection();
                return context;
            }
            catch (Exception ex)
            {
                Plugin.Logger?.LogWarning($"DirectInput init failed for `{instance.InstanceName}`: {ex.Message}");
                candidate?.Dispose();
            }
        }

        Plugin.Logger?.LogWarning("No DirectInput force feedback device found.");
        directInput.Dispose();
        return null;
    }

    private void LogSelection()
    {
        Plugin.Logger?.LogInfo($"Selected DirectInput device: `{DeviceName}`");
        foreach (var kvp in AxisBindings)
        {
            var binding = kvp.Value;
            Plugin.Logger?.LogInfo(
                $"AxisBinding[{binding.Axis}] Actuator={binding.ActuatorName} Axes=[{string.Join(",", binding.EffectAxes)}] Directions=[{string.Join(",", binding.EffectDirections)}]");
        }

        foreach (var effectInfo in SupportedEffects)
        {
            Plugin.Logger?.LogInfo($"Supported FF effect: {effectInfo.Name} Guid={effectInfo.Guid}");
        }
    }

    private static Dictionary<FFAxis, AxisBinding> CreateAxisBindings(IReadOnlyList<DeviceObjectInstance> actuators)
    {
        var axisBindings = new Dictionary<FFAxis, AxisBinding>();

        if (actuators.Count >= 1)
        {
            axisBindings[FFAxis.Roll] = CreateBinding(FFAxis.Roll, actuators[0]);
        }

        if (actuators.Count >= 2)
        {
            axisBindings[FFAxis.Pitch] = CreateBinding(FFAxis.Pitch, actuators[1]);
        }
        else if (actuators.Count >= 1)
        {
            axisBindings[FFAxis.Pitch] = CreateBinding(FFAxis.Pitch, actuators[0]);
        }

        return axisBindings;
    }

    private static AxisBinding CreateBinding(FFAxis axis, DeviceObjectInstance actuator)
    {
        return new AxisBinding(
            axis,
            new[] { (int)actuator.ObjectId },
            new[] { 10_000 },
            actuator.Name ?? actuator.ObjectId.ToString());
    }

    private static int GetAxisValue(JoystickState state, string actuatorName)
    {
        return actuatorName switch
        {
            "X Axis" => state.X,
            "Y Axis" => state.Y,
            "Z Axis" => state.Z,
            "Rotation X" => state.RotationX,
            "Rotation Y" => state.RotationY,
            "Rotation Z" => state.RotationZ,
            _ => actuatorName.IndexOf("Y Axis", StringComparison.OrdinalIgnoreCase) >= 0 ? state.Y : state.X
        };
    }

    private static float NormalizeAxisValue(int rawValue)
    {
        return Mathf.Clamp(((rawValue / 65535f) * 2f) - 1f, -1f, 1f);
    }

    private static IntPtr GetCooperativeWindow()
    {
        var handle = GetForegroundWindow();
        if (handle != IntPtr.Zero)
        {
            return handle;
        }

        handle = Process.GetCurrentProcess().MainWindowHandle;
        if (handle != IntPtr.Zero)
        {
            return handle;
        }

        handle = GetConsoleWindow();
        if (handle != IntPtr.Zero)
        {
            return handle;
        }

        throw new InvalidOperationException("Unable to resolve a valid window handle for DirectInput cooperative level.");
    }

    private static bool IsProbableActuator(DeviceObjectInstance obj)
    {
        var objectIdText = obj.ObjectId.ToString();
        var name = obj.Name ?? string.Empty;

        if (name.StartsWith("Collection", StringComparison.OrdinalIgnoreCase) ||
            name.StartsWith("DC ", StringComparison.OrdinalIgnoreCase) ||
            name.StartsWith("ET ", StringComparison.OrdinalIgnoreCase) ||
            name.IndexOf("Effect Block Index", StringComparison.OrdinalIgnoreCase) >= 0 ||
            name.IndexOf("Magnitude", StringComparison.OrdinalIgnoreCase) >= 0 ||
            name.IndexOf("Coefficient", StringComparison.OrdinalIgnoreCase) >= 0 ||
            name.IndexOf("Offset", StringComparison.OrdinalIgnoreCase) >= 0 ||
            name.IndexOf("Duration", StringComparison.OrdinalIgnoreCase) >= 0 ||
            name.IndexOf("Trigger", StringComparison.OrdinalIgnoreCase) >= 0 ||
            name.IndexOf("Gain", StringComparison.OrdinalIgnoreCase) >= 0)
        {
            return false;
        }

        if (objectIdText.IndexOf("ForceFeedbackActuator", StringComparison.OrdinalIgnoreCase) >= 0)
        {
            return true;
        }

        return name.IndexOf("actuator", StringComparison.OrdinalIgnoreCase) >= 0;
    }

    private static int CompareActuatorPriority(DeviceObjectInstance left, DeviceObjectInstance right)
    {
        return GetActuatorPriority(right).CompareTo(GetActuatorPriority(left));
    }

    private static int GetActuatorPriority(DeviceObjectInstance obj)
    {
        var score = 0;
        var name = obj.Name ?? string.Empty;
        var objectIdText = obj.ObjectId.ToString();

        if (objectIdText.IndexOf("ForceFeedbackActuator", StringComparison.OrdinalIgnoreCase) >= 0) score += 1000;
        if (objectIdText.IndexOf("AbsoluteAxis", StringComparison.OrdinalIgnoreCase) >= 0) score += 300;

        if (name.Equals("X Axis", StringComparison.OrdinalIgnoreCase)) score += 200;
        else if (name.Equals("Y Axis", StringComparison.OrdinalIgnoreCase)) score += 180;
        else if (name.IndexOf("Axis", StringComparison.OrdinalIgnoreCase) >= 0) score += 120;
        else if (name.IndexOf("actuator", StringComparison.OrdinalIgnoreCase) >= 0) score += 80;

        if (name.StartsWith("DC ", StringComparison.OrdinalIgnoreCase)) score -= 1000;
        if (name.StartsWith("ET ", StringComparison.OrdinalIgnoreCase)) score -= 1000;
        if (name.StartsWith("Collection", StringComparison.OrdinalIgnoreCase)) score -= 1000;
        if (objectIdText.IndexOf("Output", StringComparison.OrdinalIgnoreCase) >= 0) score -= 100;

        return score;
    }

    public void Dispose()
    {
        try
        {
            Device?.Unacquire();
        }
        catch
        {
            // ignored during shutdown
        }

        Device?.Dispose();
        _directInput.Dispose();
    }
}
