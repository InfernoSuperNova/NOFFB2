using HarmonyLib;

namespace NOFFB2;

[HarmonyPatch(typeof(CameraCockpitState))]
internal static class CameraCockpitStatePatch
{
    [HarmonyPostfix]
    [HarmonyPatch(nameof(CameraCockpitState.AddShake))]
    private static void AddShakePostfix(float lowFreqShake, float highFreqShake)
    {
        var shake = new Aircraft.OnShake();
        shake.lowFreqShake = lowFreqShake;
        shake.highFreqShake = highFreqShake;
        AircraftForceManager.I?.OnShake(shake);
    }
}
