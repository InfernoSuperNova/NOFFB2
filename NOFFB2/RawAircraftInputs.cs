using System.Collections.Generic;
using UnityEngine;

namespace NOFFB2;

internal static class RawAircraftInputs
{
    private static readonly Dictionary<Aircraft, Vector3> InputsByAircraft = new();

    public static void Set(Aircraft aircraft, float pitch, float yaw, float roll)
    {
        if (aircraft == null)
        {
            return;
        }

        InputsByAircraft[aircraft] = new Vector3(pitch, yaw, roll);
    }

    public static bool TryGet(Aircraft aircraft, out Vector3 rawInputs)
    {
        if (aircraft != null && InputsByAircraft.TryGetValue(aircraft, out rawInputs))
        {
            return true;
        }

        rawInputs = Vector3.zero;
        return false;
    }

    public static void Remove(Aircraft aircraft)
    {
        if (aircraft == null)
        {
            return;
        }

        InputsByAircraft.Remove(aircraft);
    }
}
