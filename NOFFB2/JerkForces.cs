using UnityEngine;

namespace NOFFB2;

public class JerkForces
{
    private FFLinear _jerkPitch;
    private FFLinear _jerkRoll;

    private Vector3 _prevAngularVelocity;
    private Vector3 _prevAngularAccel;

    // Tuning Parameters
    private const float NoiseFloor = 0.000f;    // Ignore small physics jitters
    private const float Gain = 0.0003f;         // Overall sensitivity
    private const float Sharpness = 1.0f;      // Power curve (0.5-1.0). Lower is "snappier" but noisier.
    private const float Smoothing = 0.5f;      // Low-pass filter (0.1 = heavy, 1.0 = raw)

    private float _filteredPitch = 0;
    private float _filteredRoll = 0;
    
    
    private AircraftForceManager _afm;
    public JerkForces(AircraftForceManager afm)
    {
        _afm = afm;
    }

    private Aircraft Aircraft => _afm.Aircraft;

    public void Init()
    {
        _jerkPitch = new FFLinear(999, 0);
        _jerkRoll = new FFLinear(999, 0);
        
        FFServer.I.AddContinuousEffect(_jerkPitch, FFAxis.Pitch);
        FFServer.I.AddContinuousEffect(_jerkRoll, FFAxis.Roll);
    }
    
    public void FixedUpdate()
    {
        if (Aircraft == null || Aircraft.rb == null) return;

        // 1. Calculate derivatives
        Vector3 currentAngularVelocity = Aircraft.rb.angularVelocity;
        Vector3 currentAngularAccel = (currentAngularVelocity - _prevAngularVelocity) / Time.fixedDeltaTime;
        Vector3 angularJerk = (currentAngularAccel - _prevAngularAccel) / Time.fixedDeltaTime;

        _prevAngularVelocity = currentAngularVelocity;
        _prevAngularAccel = currentAngularAccel;

        // 2. Extract and Scale
        float rawPitch = angularJerk.x * Gain;
        float rawRoll = angularJerk.z * Gain;

        // 3. APPLY NOISE GATE & POWER CURVE
        // This is the secret sauce: It ignores the "shivering" (NoiseFloor)
        // and uses a power curve to make the big hits feel sharp.
        float processedPitch = ApplyJerkProcessing(rawPitch);
        float processedRoll = ApplyJerkProcessing(rawRoll);

        // 4. LOW PASS FILTER (Smoothing)
        // This stops the motor from "clacking" on single-frame spikes
        _filteredPitch = Mathf.Lerp(_filteredPitch, processedPitch, Smoothing);
        _filteredRoll = Mathf.Lerp(_filteredRoll, processedRoll, Smoothing);
        
        
        
        
        

        _jerkPitch.SetForce(Mathf.Clamp(_filteredPitch, -1f, 1f));
        _jerkRoll.SetForce(Mathf.Clamp(_filteredRoll, -1f, 1f));
    }

    private float ApplyJerkProcessing(float input)
    {
        float absInput = Mathf.Abs(input);
        
        // Noise Floor: Kill the micro-stutters
        if (absInput < NoiseFloor) return 0;

        // Power Curve: Boost the feeling of large impacts without clipping
        // We subtract the noise floor first to prevent a "jump" in force
        float signal = absInput - NoiseFloor;
        float result = Mathf.Pow(signal, Sharpness);

        return Mathf.Sign(input) * result;
    }
}