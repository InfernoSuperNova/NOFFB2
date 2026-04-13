using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using SharpDX.DirectInput;

namespace NOFFB2;

internal sealed class DirectInputDeviceContext : IDisposable
{
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
        int[] effectAxes,
        int[] effectDirections)
    {
        _directInput = directInput;
        Device = device;
        DeviceName = deviceName;
        SupportedEffects = supportedEffects;
        EffectAxes = effectAxes;
        EffectDirections = effectDirections;
    }

    public Joystick Device { get; }

    public string DeviceName { get; }

    public IReadOnlyList<EffectInfo> SupportedEffects { get; }

    public int[] EffectAxes { get; }

    public int[] EffectDirections { get; }

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
                    Plugin.Logger?.LogInfo(
                        $"`{instance.InstanceName}` object: Name={obj.Name}, Offset={obj.Offset}, ObjectId={obj.ObjectId}, TypeGuid={obj.ObjectType}");

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

                var context = new DirectInputDeviceContext(
                    directInput,
                    candidate,
                    instance.InstanceName,
                    supportedEffects,
                    new[] { (int)actuators[0].ObjectId },
                    new[] { 0 });

                context.LogSelection(actuators);
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

    private void LogSelection(List<DeviceObjectInstance> actuators)
    {
        Plugin.Logger?.LogInfo($"Selected DirectInput device: `{DeviceName}`");
        for (var index = 0; index < actuators.Count; index++)
        {
            var actuator = actuators[index];
            Plugin.Logger?.LogInfo($"Actuator[{index}] Name={actuator.Name} ObjectId={actuator.ObjectId} Offset={actuator.Offset} TypeFlags={actuator.ObjectType}");
        }

        foreach (var effectInfo in SupportedEffects)
        {
            Plugin.Logger?.LogInfo($"Supported FF effect: {effectInfo.Name} Guid={effectInfo.Guid}");
        }
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
