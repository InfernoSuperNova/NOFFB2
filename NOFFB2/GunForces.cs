using System;
using System.Collections.Generic;
using BepInEx.Logging;

namespace NOFFB2;

public sealed class GunForces
{
    private readonly Dictionary<WeaponStation, Action> _weaponHandlers = new();
    private Aircraft _aircraft;
    private float _aircraftMass;

    private Dictionary<WeaponStation, int> _ammoCounts = new();

    public void SetAircraft(Aircraft aircraft)
    {
        if (aircraft == null) return;
        if (ReferenceEquals(_aircraft, aircraft))
        {
            return;
        }
        
        UnsubscribeAll();
        _ammoCounts.Clear();
        _aircraft = aircraft;
        _aircraftMass = aircraft.GetMass();
        SubscribeAll();
    }

    private void SubscribeAll()
    {
        if (_aircraft?.weaponStations == null)
        {
            return;
        }

        foreach (var weaponStation in _aircraft.weaponStations)
        {
            if (weaponStation == null || _weaponHandlers.ContainsKey(weaponStation))
            {
                continue;
            }

            Action handler = () => OnWeaponStationUpdated(weaponStation);
            _weaponHandlers[weaponStation] = handler;
            _ammoCounts[weaponStation] = weaponStation.Ammo;
            weaponStation.OnUpdated += handler;
        }
    }

    private void UnsubscribeAll()
    {
        foreach (var kvp in _weaponHandlers)
        {
            kvp.Key.OnUpdated -= kvp.Value;
        }

        _weaponHandlers.Clear();
    }

    private void OnWeaponStationUpdated(WeaponStation wep)
    {
        int newAmmo = wep.Ammo;

        if (wep.WeaponInfo.gun && newAmmo < _ammoCounts[wep])
            OnGunFired(wep);
        
        _ammoCounts[wep] = newAmmo;
    }

    private void OnGunFired(WeaponStation wep)
    {
        var energy = (wep.WeaponInfo.massPerRound * wep.WeaponInfo.muzzleVelocity * wep.WeaponInfo.muzzleVelocity) / 900; // TODO: Multiply by user settable config value

        var impulse = energy / _aircraftMass;

        var pitchEffect = new FFDecayingSine(0.2f, 10, impulse);
        var rollEffect = new FFDecayingSine(0.2f, 10, impulse);
        FFServer.I.AddEffect(pitchEffect, FFAxis.Pitch);
        FFServer.I.AddEffect(rollEffect, FFAxis.Roll);
    }
}
