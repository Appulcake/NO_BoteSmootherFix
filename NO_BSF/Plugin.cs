using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using JetBrains.Annotations;
using NOComponentWIP;
using NuclearOption.NetworkTransforms;
using UnityEngine;
using UnityEngine.Rendering;

namespace NO_BSF;

[BepInPlugin(MyPluginInfo.PLUGIN_GUID, MyPluginInfo.PLUGIN_NAME, MyPluginInfo.PLUGIN_VERSION)]
[BepInDependency("com.minec.bote")]
public sealed class Plugin : BaseUnityPlugin
{
    private ConfigEntry<bool> _debugLogsConfig = null!;
    private Harmony? _harmony;
    internal new static ManualLogSource Logger { get; private set; } = null!;
    internal static bool DebugLogs { get; private set; }
    
    private void Awake()
    {
        Logger = base.Logger;
        
        var headless = GameManager.IsHeadless || SystemInfo.graphicsDeviceType == GraphicsDeviceType.Null;
        _debugLogsConfig = Config.Bind("General", "Enable debug logs", headless);
        _debugLogsConfig.SettingChanged += (_, _) => DebugLogs = _debugLogsConfig.Value;
        DebugLogs = _debugLogsConfig.Value;
        
        _harmony = new Harmony(MyPluginInfo.PLUGIN_GUID);
        if (headless)
        {
            TryPatch(typeof(AmmoSendPatch));
        }
        else
        {
            TryPatch(typeof(SmootherPatch));
            TryPatch(typeof(AmmoReceivePatch));
        }
        
        Logger.LogInfo($"{MyPluginInfo.PLUGIN_GUID} loaded!");
    }
    
    private void OnDestroy() => _harmony?.UnpatchSelf();
    
    private void TryPatch(Type patchClass)
    {
        try
        {
            _harmony!.CreateClassProcessor(patchClass).Patch();
            Logger.LogInfo($"Applied {patchClass.Name}.");
        }
        catch (Exception ex)
        {
            Logger.LogError($"Failed to apply {patchClass.Name}:\n{ex}");
        }
    }
}

[HarmonyPatch(typeof(SmoothAircraftNetworkTransform), nameof(SmoothAircraftNetworkTransform.VisualUpdate))]
internal static class SmootherPatch
{
    private static readonly ConditionalWeakTable<SmoothAircraftNetworkTransform, SmootherState> States = new();
    private static float _nextWarningTime;
    
    [HarmonyPrefix]
    [HarmonyPriority(Priority.First)]
    // ReSharper disable once InconsistentNaming
    private static bool Prefix(SmoothAircraftNetworkTransform __instance)
    {
        var aircraft = __instance.Aircraft;
        if (aircraft == null)
            return false;
        
        var state = States.GetValue(__instance, _ => new SmootherState());
        if (__instance.HasAuthority || aircraft.LocalSim)
        {
            state.Reset();
            return false;
        }
        
        var rb = aircraft.rb;
        var smoother = __instance.networkSmoother;
        if (rb == null || rb.isKinematic || smoother == null)
        {
            state.Reset();
            return false;
        }
        
        if (state.InitializedRb == rb && ReferenceEquals(state.InitializedSmoother, smoother))
            return true;
        
        smoother.Initialize(rb);
        state.InitializedRb = rb;
        state.InitializedSmoother = smoother;
        return true;
    }
    
    
    [HarmonyFinalizer]
    [UsedImplicitly]
    // ReSharper disable once InconsistentNaming
    private static Exception? Finalizer(Exception? __exception)
    {
        if (__exception == null)
            return null;
        
        if (__exception is not NullReferenceException && __exception is not MissingReferenceException)
            return __exception;
        
        if (!Plugin.DebugLogs || !(Time.realtimeSinceStartup >= _nextWarningTime))
            return null;
        
        _nextWarningTime = Time.realtimeSinceStartup + 30f;
        Plugin.Logger.LogWarning("Suppressed exception in BOTE smoother VisualUpdate so remaining transforms and " +
                                 "network sends can continue:\n" + __exception);
        
        return null;
    }
    
    private sealed class SmootherState
    {
        internal Rigidbody? InitializedRb;
        internal NetworkPIDSmoother? InitializedSmoother;
        
        internal void Reset()
        {
            InitializedRb = null;
            InitializedSmoother = null;
        }
    }
}

[HarmonyPatch]
internal static class AmmoReceivePatch
{
    [UsedImplicitly]
    private static MethodBase TargetMethod()
    {
        Type[] signature = [typeof(byte), typeof(int)];
        var target = AccessTools.Method(typeof(Unit), "UserCode_RpcSyncAmmoCount_-1454761002", signature)
                     ?? AccessTools.Method(typeof(Unit), "UserCode_RpcSyncAmmoCount__002D1454761002", signature);
        if (target != null)
            return target;
        
        foreach (var method in AccessTools.GetDeclaredMethods(typeof(Unit)))
        {
            if (!method.Name.StartsWith("UserCode_RpcSyncAmmoCount", StringComparison.Ordinal))
                continue;
            
            var parameters = method.GetParameters();
            if (parameters.Length == 2 && parameters[0].ParameterType == typeof(byte) && parameters[1].ParameterType == typeof(int))
                return method;
        }
        
        throw new MissingMethodException("Could not locate Unit.UserCode_RpcSyncAmmoCount(byte, int).");
    }
    
    [HarmonyPrefix]
    [UsedImplicitly]
    // ReSharper disable once InconsistentNaming
    private static bool Prefix(Unit __instance, byte __0, int __1)
    {
        if (__instance == null)
            return false;
        
        var stations = __instance.weaponStations;
        var count = stations?.Count ?? 0;
        var valid = __0 < count && stations![__0] != null;
        if (valid)
            return true;
        
        if (Plugin.DebugLogs && AmmoDiagnostics.ShouldLog(__instance, __0))
            Plugin.Logger.LogWarning($"--- Ammo RPC received/ignored\nNetId: {__instance.NetId}, " +
                                     $"PersistentID: {__instance.persistentID}, Unit: {__instance.unitName}, " +
                                     $"Definition: {__instance.definition?.jsonKey}, StationIndex: {__0}, " +
                                     $"ClientStationCount: {count}, Ammo: {__1}, " +
                                     $"Reason: {(__0 >= count ? "out of range" : "null station")}");
        
        return false;
    }
}

[HarmonyPatch(typeof(Unit), nameof(Unit.RpcSyncAmmoCount))]
internal static class AmmoSendPatch
{
    [HarmonyPrefix]
    [UsedImplicitly]
    // ReSharper disable once InconsistentNaming
    private static bool Prefix(Unit __instance, byte stationIndex, int ammo)
    {
        if (__instance == null)
            return false;
        
        var stations = __instance.weaponStations;
        var count = stations?.Count ?? 0;
        var valid = stationIndex < count && stations![stationIndex] != null;
        
        if (!Plugin.DebugLogs || !AmmoDiagnostics.ShouldLog(__instance, stationIndex))
            return valid;
        
        var stationName = valid ? stations![stationIndex].WeaponInfo?.weaponName : "(missing)";
        var message = $"--- Ammo RPC sending{(valid ? "" : "/ignored")}\n" +
                  $"NetId: {__instance.NetId}, PersistentID: {__instance.persistentID}, " +
                  $"Unit: {__instance.unitName}, Definition: {__instance.definition?.jsonKey}, " +
                  $"StationIndex: {stationIndex}, ServerStationCount: {count}, " +
                  $"StationName: {stationName}, Ammo: {ammo}";
        if (valid)
            Plugin.Logger.LogInfo(message);
        else
            Plugin.Logger.LogWarning(message);
        
        return valid;
    }
}

internal static class AmmoDiagnostics
{
    private static readonly ConditionalWeakTable<Unit, PerUnitLogState> States = new();
    
    internal static bool ShouldLog(Unit unit, byte stationIndex)
    {
        var state = States.GetValue(unit, _ => new PerUnitLogState());
        var now = Time.realtimeSinceStartup;
        if (state.NextLog.TryGetValue(stationIndex, out var nextTime) && now < nextTime)
            return false;
        
        state.NextLog[stationIndex] = now + 30f;
        return true;
    }
    
    private sealed class PerUnitLogState
    {
        internal readonly Dictionary<byte, float> NextLog = new();
    }
}