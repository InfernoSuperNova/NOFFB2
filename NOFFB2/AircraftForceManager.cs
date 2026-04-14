using System;
using System.Collections.Generic;
using BepInEx.Logging;

namespace NOFFB2;

public class AircraftForceManager
{
    private Aircraft _aircraft;
    private Aircraft _oldAircraft;
    private readonly Plugin _plugin;
    private readonly Dictionary<FFAxis, FFPerlinEffect> _cameraShakeEffects = new();
    private readonly GunForces _gunForces = new();
    private bool _loggedFirstUpdate;

    public AircraftForceManager(Plugin plugin)
    {
        _plugin = plugin;
        _plugin.UpdateDispatcher += Update;
        foreach (FFAxis axis in Enum.GetValues(typeof(FFAxis)))
        {
            var effect = new FFPerlinEffect();
            _cameraShakeEffects[axis] = effect;
            FFServer.I.AddContinuousEffect(effect, axis);
        }
        
        Log("Initialized");
        I = this;
    }

    public static AircraftForceManager I { get; private set; }
    public bool AircraftValid => _aircraft != null;

    private void Update()
    {
        if (!_loggedFirstUpdate)
        {
            Log("First manager update");
            _loggedFirstUpdate = true;
        }

        _oldAircraft = _aircraft;
        GameManager.GetLocalAircraft(out _aircraft);
        if (_aircraft != _oldAircraft) OnAircraftChanged();
    }

    private void OnAircraftChanged()
    {
        Log("OnAircraftChanged");
        if (_oldAircraft is not null)
        {
            Log("Removing shake subscription from old aircraft");
            _oldAircraft.onShake -= OnShake;
        }

        if (_aircraft is not null)
        {
            Log("Subscribing new aircraft to new shake");
            _aircraft.onShake += OnShake;
        }

        _gunForces.SetAircraft(_aircraft);
    }

    public void OnShake(Aircraft.OnShake shake)
    {
        foreach (var kvp in _cameraShakeEffects) kvp.Value.AddShake(shake.lowFreqShake, shake.highFreqShake);
    }
    private static void Log(object log, LogLevel level = LogLevel.Info) => Plugin.Log($"AircraftForceManager : {log}", level);
}
