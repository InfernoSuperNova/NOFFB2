using System.Collections.Generic;
using System.Reflection;
using UnityEngine;

namespace NOFFB2;

public class AeroForces
{
    private const float MinAirspeed = 10f;
    private const float ReferenceMoment = 75000000f;
    private const float SurfaceMomentGain = 0.035f;
    private const float PitchSpringGain = 0.60f;
    private const float RollSpringGain = 0.60f;
    private const float PitchCenterMomentGain = 0.4f;
    private const float RollCenterMomentGain = 0.4f;
    private const float MinimumSpringGain = 0.00f;
    private const float BaseSmoothing = 0.16f;

    private static readonly FieldInfo AirfoilIndexField = typeof(AeroPart).GetField("airfoil", BindingFlags.Instance | BindingFlags.NonPublic);
    private static readonly FieldInfo WingEffectivenessField = typeof(AeroPart).GetField("wingEffectiveness", BindingFlags.Instance | BindingFlags.NonPublic);

    private readonly AircraftForceManager _afm;
    private readonly List<SurfaceInfo> _surfaces = new();
    private FFSpring _pitchSpring;
    private FFSpring _rollSpring;
    private float _filteredPitchGain;
    private float _filteredRollGain;
    private float _filteredPitchCenter;
    private float _filteredRollCenter;

    public AeroForces(AircraftForceManager afm)
    {
        _afm = afm;
    }

    private Aircraft Aircraft => _afm.Aircraft;

    public void Init()
    {
        _pitchSpring = new FFSpring(999);
        _rollSpring = new FFSpring(999);

        FFServer.I.AddContinuousEffect(_pitchSpring, FFAxis.Pitch);
        FFServer.I.AddContinuousEffect(_rollSpring, FFAxis.Roll);
    }

    public void AircraftChanged()
    {
        _surfaces.Clear();
        _filteredPitchGain = 0f;
        _filteredRollGain = 0f;
        _filteredPitchCenter = 0f;
        _filteredRollCenter = 0f;

        if (Aircraft != null)
        {
            CacheAircraftSurfaces(Aircraft);
        }

        ApplySpringState(0f, 0f, 0f, 0f);
    }

    public void FixedUpdate()
    {
        if (Aircraft == null)
        {
            ResetTowardsZero();
            return;
        }

        Rigidbody rigidbody = Aircraft.CockpitRB();
        if (rigidbody == null)
        {
            ResetTowardsZero();
            return;
        }

        Vector3 airVelocityWorld = rigidbody.velocity - Aircraft.GetWindVelocity();
        float airspeed = airVelocityWorld.magnitude;
        if (airspeed < MinAirspeed)
        {
            ResetTowardsZero();
            return;
        }

        float pitchSurfaceMoment = 0f;
        float rollSurfaceMoment = 0f;
        float totalSurfaceWeight = 0f;

        foreach (var surface in _surfaces)
        {
            if (surface.Part == null || surface.LiftTransform == null)
            {
                continue;
            }

            Vector3 localAirVelocity = surface.LiftTransform.InverseTransformDirection(airVelocityWorld);
            float localSpeedSq = localAirVelocity.sqrMagnitude;
            if (localSpeedSq < 1f)
            {
                continue;
            }

            float aoaRad = Mathf.Atan2(-localAirVelocity.y, Mathf.Max(localAirVelocity.z, 0.1f));
            float liftCoef = surface.Airfoil?.liftCoef != null
                ? surface.Airfoil.liftCoef.Evaluate(aoaRad)
                : Mathf.Clamp(aoaRad * 2.4f, -1.5f, 1.5f);

            float dragCoef = surface.Airfoil?.dragCoef != null
                ? surface.Airfoil.dragCoef.Evaluate(aoaRad)
                : 0.05f + aoaRad * aoaRad * 0.6f;

            float wingEffectiveness = GetWingEffectiveness(surface.Part);
            float area = Mathf.Max(surface.Part.GetWingArea(), 0f);
            float dragArea = Mathf.Max(surface.Part.dragArea, 0f);
            float dynamicPressure = 0.5f * Aircraft.GetAirDensity() * localSpeedSq;
            float liftForce = dynamicPressure * area * liftCoef * wingEffectiveness;
            float dragForce = dynamicPressure * Mathf.Max(dragArea, area * 0.08f) * dragCoef;

            Vector3 localForce = new Vector3(0f, liftForce, -dragForce);
            Vector3 worldForce = surface.LiftTransform.TransformDirection(localForce);
            Vector3 localMomentArm = Aircraft.transform.InverseTransformPoint(surface.LiftTransform.position);
            Vector3 localForceAircraft = Aircraft.transform.InverseTransformDirection(worldForce);
            Vector3 localMoment = Vector3.Cross(localMomentArm, localForceAircraft);

            float surfaceWeight = Mathf.Max(Mathf.Abs(liftForce) + Mathf.Abs(dragForce), 1f);
            pitchSurfaceMoment += -localMoment.x;
            rollSurfaceMoment += localMoment.z;
            totalSurfaceWeight += surfaceWeight;
        }

        float normalizedPitchMoment = Mathf.Clamp(pitchSurfaceMoment / ReferenceMoment, -1f, 1f);
        float normalizedRollMoment = Mathf.Clamp(rollSurfaceMoment / ReferenceMoment, -1f, 1f);
        float surfaceLoadFactor = Mathf.Clamp01(totalSurfaceWeight / (ReferenceMoment * SurfaceMomentGain));
        float pitchGainTarget = Mathf.Clamp(MinimumSpringGain + surfaceLoadFactor * PitchSpringGain, 0f, 1f);
        float rollGainTarget = Mathf.Clamp(MinimumSpringGain + surfaceLoadFactor * RollSpringGain, 0f, 1f);
        float pitchCenterTarget = Mathf.Clamp(normalizedPitchMoment * PitchCenterMomentGain, -0.7f, 0.7f);
        float rollCenterTarget = Mathf.Clamp(-normalizedRollMoment * RollCenterMomentGain, -0.7f, 0.7f);

        float smoothing = Mathf.Lerp(BaseSmoothing, 0.32f, Mathf.Clamp01(surfaceLoadFactor));
        _filteredPitchGain = Mathf.Lerp(_filteredPitchGain, pitchGainTarget, smoothing);
        _filteredRollGain = Mathf.Lerp(_filteredRollGain, rollGainTarget, smoothing);
        _filteredPitchCenter = Mathf.Lerp(_filteredPitchCenter, pitchCenterTarget, smoothing);
        _filteredRollCenter = Mathf.Lerp(_filteredRollCenter, rollCenterTarget, smoothing);

        ApplySpringState(_filteredPitchGain, _filteredRollGain, _filteredPitchCenter, _filteredRollCenter);
    }

    private void CacheAircraftSurfaces(Aircraft aircraft)
    {
        _surfaces.Clear();
        var parameters = aircraft.GetAircraftParameters();
        var parts = aircraft.GetAllParts();
        if (parts == null)
        {
            return;
        }

        foreach (var part in parts)
        {
            if (part is not AeroPart aeroPart)
            {
                continue;
            }

            float wingArea = aeroPart.GetWingArea();
            if (wingArea <= 0.01f)
            {
                continue;
            }

            int airfoilIndex = AirfoilIndexField != null ? (int)AirfoilIndexField.GetValue(aeroPart) : -1;
            Airfoil airfoil = null;
            if (parameters?.airfoils != null && airfoilIndex >= 0 && airfoilIndex < parameters.airfoils.Length)
            {
                airfoil = parameters.airfoils[airfoilIndex];
            }

            _surfaces.Add(new SurfaceInfo
            {
                Part = aeroPart,
                LiftTransform = aeroPart.GetLiftTransform(),
                Airfoil = airfoil
            });
        }
    }

    private static float GetWingEffectiveness(AeroPart part)
    {
        if (WingEffectivenessField?.GetValue(part) is float value)
        {
            return value;
        }

        return 1f;
    }

    private void ResetTowardsZero()
    {
        _filteredPitchGain = Mathf.Lerp(_filteredPitchGain, 0f, 0.25f);
        _filteredRollGain = Mathf.Lerp(_filteredRollGain, 0f, 0.25f);
        _filteredPitchCenter = Mathf.Lerp(_filteredPitchCenter, 0f, 0.25f);
        _filteredRollCenter = Mathf.Lerp(_filteredRollCenter, 0f, 0.25f);
        ApplySpringState(_filteredPitchGain, _filteredRollGain, _filteredPitchCenter, _filteredRollCenter);
    }

    private void ApplySpringState(float pitchGain, float rollGain, float pitchCenter, float rollCenter)
    {
        _pitchSpring.Gain = Mathf.Clamp01(pitchGain);
        _rollSpring.Gain = Mathf.Clamp01(rollGain);
        _pitchSpring.Center = Mathf.Clamp(pitchCenter, -1f, 1f);
        _rollSpring.Center = Mathf.Clamp(rollCenter, -1f, 1f);
    }

    private sealed class SurfaceInfo
    {
        public AeroPart Part;
        public Transform LiftTransform;
        public Airfoil Airfoil;
    }
}
