using UnityEngine;

namespace NOFFB2;

public class FFLinear : FFEffect
{
    protected float _force;
    
    /// <summary>
    /// Creates a linear force.
    /// </summary>
    /// <param name="durationSeconds">How long the effect should run for.</param>
    /// <param name="force">The force to apply (mag > 1 saturates)</param>
    public FFLinear(float durationSeconds, float force) : base(durationSeconds)
    {
        _force = force;
    }

    public override float Calculate(long elapsed, float currentPos)
    {
        return _force;
    }

    public void SetForce(float newForce)
    {
        _force = newForce;
    }
}