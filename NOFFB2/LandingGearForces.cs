using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace NOFFB2;

public class LandingGearForces
{
    private List<LandingGear> _gear = new();
    private FFLinear _gearPitch;
    private FFLinear _gearRoll;
    private Aircraft _aircraft;

    public LandingGearForces()
    {
        
    }

    public void Init()
    {
        _gearPitch = new FFLinear(999, 0);
        _gearRoll = new FFLinear(999, 0);
        
        FFServer.I.AddContinuousEffect(_gearPitch, FFAxis.Pitch);
        FFServer.I.AddContinuousEffect(_gearRoll, FFAxis.Roll);
    }
    
    public void SetAircraft(Aircraft aircraft)
    {
        if (aircraft == null) return;
        _aircraft = aircraft;
        var parts = aircraft.GetAllParts();

        _gear.Clear();
        foreach (var part in parts)
        {
            var gear = part.GetComponentInChildren<LandingGear>();
            if (gear != null)
            {
                Plugin.Log("Gear found");
                _gear.Add(gear);
            }
            
        }

    }
    
    // Store the previous force to calculate the "Snap" (Delta)
    private float _prevTotalPitch = 0f;
    private float _prevTotalRoll = 0f;

    public void Update()
    {
        float currentPitchForce = 0f;
        float currentRollForce = 0f;

        foreach (var gear in _gear)
        {
            if (gear == null || !gear.enabled) continue;

            float force = (float)AccessTools.Field(typeof(LandingGear), "compressionForce").GetValue(gear);
            // Damping is key! It resists movement. 
            float damping = (float)AccessTools.Field(typeof(LandingGear), "dampingForce").GetValue(gear);
        
            Vector3 relPos = _aircraft.transform.InverseTransformPoint(gear.transform.position);
            
            float totalStrutForce = force + damping;

            currentRollForce += (totalStrutForce * relPos.x);
            currentPitchForce -= (totalStrutForce * relPos.z);
        }
        
        float pitchDelta = currentPitchForce - _prevTotalPitch;
        float rollDelta = currentRollForce - _prevTotalRoll;
        
        // 0.05f is the 'static' weight, 0.5f is the 'impact' kick. Adjust these!
        float finalPitch = (pitchDelta * 0.5f) + (currentPitchForce * 0.05f);
        float finalRoll = (rollDelta * 0.5f) + (currentRollForce * 0.05f);

        // Final Scaling and Clamping
        finalPitch = Mathf.Clamp(finalPitch / 2_000_000f, -1f, 1f);
        finalRoll = Mathf.Clamp(finalRoll / 2_000_000f, -1f, 1f);

        _gearPitch.SetForce(finalPitch);
        _gearRoll.SetForce(finalRoll);

        _prevTotalPitch = currentPitchForce;
        _prevTotalRoll = currentRollForce;
    }
}








