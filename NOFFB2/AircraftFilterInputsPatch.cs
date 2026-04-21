using HarmonyLib;

namespace NOFFB2;

[HarmonyPatch(typeof(Aircraft))]
internal static class AircraftFilterInputsPatch
{
    [HarmonyPrefix]
    [HarmonyPatch(nameof(Aircraft.FilterInputs))]
    private static void FilterInputsPrefix(Aircraft __instance)
    {
        var inputs = __instance.GetInputs();
        RawAircraftInputs.Set(__instance, inputs.pitch, inputs.yaw, inputs.roll);
    }

    [HarmonyPostfix]
    [HarmonyPatch("OnDestroy")]
    private static void OnDestroyPostfix(Aircraft __instance)
    {
        RawAircraftInputs.Remove(__instance);
    }
}
