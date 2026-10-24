using System;
using System.Reflection;
using BepInEx;
using BepInEx.Logging;
using HarmonyLib;
using JetBrains.Annotations;
using NOComponentWIP;
using NOComponentWIP.Patches;
using UnityEngine;

namespace NO_BSF;

[BepInPlugin(MyPluginInfo.PLUGIN_GUID, MyPluginInfo.PLUGIN_NAME, MyPluginInfo.PLUGIN_VERSION)]
[BepInDependency("com.minec.bote")]
public sealed class Plugin : BaseUnityPlugin
{
    private Harmony? _harmony;
    private static ManualLogSource Log { get; set; } = null!;
    
    private void Awake()
    {
        Log = Logger;
        try
        {
            _harmony = new Harmony(MyPluginInfo.PLUGIN_GUID);
            _harmony.CreateClassProcessor(typeof(BoteNullHangarPatch)).Patch();
            Log.LogInfo("BOTE null hangar fix applied to BOTE OnStartClient prefix.");
        }
        catch (Exception ex)
        {
            Log.LogError("Failed to patch BOTE OnStartClient prefix:\n" + ex);
        }
    }
    
    private void OnDestroy() => _harmony?.UnpatchSelf();
}

[HarmonyPatch]
internal static class BoteNullHangarPatch
{
    [UsedImplicitly]
    private static MethodBase TargetMethod() =>
        AccessTools.Method(typeof(AircraftPatches), nameof(AircraftPatches.OnStartClient_Prefix),
            [typeof(Aircraft), typeof(Vector3).MakeByRefType()]) ??
        throw new MissingMethodException(typeof(AircraftPatches).FullName,
            nameof(AircraftPatches.OnStartClient_Prefix));
    
    [HarmonyPrefix]
    [HarmonyPriority(Priority.First)]
    private static bool Prefix(Aircraft __0, ref Vector3 __1)
    {
        if ( __0 == null || __0.IsServer || !__0.definition.IsShipDefinition() ||
            __0.NetworkspawningHangar != null)
            return true;
        
        __1 = __0.definition.spawnOffset;
        return false;
    }
}