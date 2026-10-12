using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
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
    private ConfigEntry<bool> _pluginEnabled = null!;
    internal static Plugin Instance { get; private set; } = null!;
    internal new static ManualLogSource Logger { get; private set; } = null!;
    internal static bool PluginEnabled { get; private set; }
    internal static bool DebugLogs { get; private set; }
    
    private void Awake()
    {
        Instance = this;
        Logger = base.Logger;
        
        var headless = GameManager.IsHeadless || SystemInfo.graphicsDeviceType == GraphicsDeviceType.Null;
        _pluginEnabled = Config.Bind("General", "Enable plugin", true);
        
        _pluginEnabled.SettingChanged += (_, _) =>
        {
            PluginEnabled = _pluginEnabled.Value;
            Logger.LogInfo($"BOTE Smoother Fix {(PluginEnabled ? "enabled" : "disabled")}.");
        };
        
        PluginEnabled = _pluginEnabled.Value;
        _debugLogsConfig = Config.Bind("General", "Enable debug logs", headless);
        _debugLogsConfig.SettingChanged += (_, _) => DebugLogs = _debugLogsConfig.Value;
        DebugLogs = _debugLogsConfig.Value;
        
        _harmony = new Harmony(MyPluginInfo.PLUGIN_GUID);
        if (headless)
        {
            TryPatch(typeof(AmmoSendPatch));
            TryPatch(typeof(TurretSendPatch));
            TryPatch(typeof(RearmSendPatch));
        }
        else
        {
            TryPatch(typeof(SmootherPatch));
            TryPatch(typeof(AmmoReceivePatch));
            TryPatch(typeof(TurretReceivePatch));
            TryPatch(typeof(RearmReceivePatch));
        }
        
        Logger.LogInfo($"{MyPluginInfo.PLUGIN_GUID} loaded!");
    }
    
    private void OnDestroy()
    {
        _harmony?.UnpatchSelf();
        if (ReferenceEquals(Instance, this))
            Instance = null!;
    }
    
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
        if (!Plugin.PluginEnabled)
            return true;
        
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
        if (!Plugin.PluginEnabled)
            return __exception;
        
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
            if (parameters.Length == 2 && parameters[0].ParameterType == typeof(byte) &&
                parameters[1].ParameterType == typeof(int))
                return method;
        }
        
        throw new MissingMethodException("Could not locate Unit.UserCode_RpcSyncAmmoCount(byte, int).");
    }
    
    [HarmonyPrefix]
    [UsedImplicitly]
    // ReSharper disable once InconsistentNaming
    private static bool Prefix(Unit __instance, byte __0, int __1)
    {
        if (!Plugin.PluginEnabled)
            return true;
        
        if (__instance == null)
            return false;
        
        var stations = __instance.weaponStations;
        var count = stations?.Count ?? 0;
        var valid = __0 < count && stations![__0] != null;
        if (valid)
            return true;
        
        if (Plugin.DebugLogs && AmmoDiagnostics.ShouldLog(__instance, __0))
        {
            Plugin.Logger.LogWarning($"--- Ammo RPC received/ignored\nNetId: {__instance.NetId}, " +
                                     $"PersistentID: {__instance.persistentID}, Unit: {__instance.unitName}, " +
                                     $"Definition: {__instance.definition?.jsonKey}, StationIndex: {__0}, " +
                                     $"ClientStationCount: {count}, Ammo: {__1}, " +
                                     $"Reason: {(__0 >= count ? "out of range" : "null station")}");
            StationFollowup.Queue(__instance, __0, "Ammo RPC", count);
        }
        
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
        if (!Plugin.PluginEnabled)
            return true;
        
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

[HarmonyPatch]
internal static class TurretReceivePatch
{
    [UsedImplicitly]
    private static MethodBase TargetMethod()
    {
        var target = AccessTools.Method(typeof(Aircraft), "UserCode_RpcSetTurretVector_-312569381")
                     ?? AccessTools.Method(typeof(Aircraft), "UserCode_RpcSetTurretVector__002D312569381");
        if (target != null)
            return target;
        
        foreach (var method in AccessTools.GetDeclaredMethods(typeof(Aircraft)))
        {
            if (!method.Name.StartsWith("UserCode_RpcSetTurretVector", StringComparison.Ordinal))
                continue;
            
            var parameters = method.GetParameters();
            if (parameters.Length == 2 && parameters[0].ParameterType == typeof(byte) &&
                parameters[1].ParameterType.Name == "Vector3Compressed")
                return method;
        }
        
        throw new MissingMethodException(
            "Could not locate Aircraft.UserCode_RpcSetTurretVector(byte, Vector3Compressed).");
    }
    
    [HarmonyPrefix]
    [UsedImplicitly]
    // ReSharper disable once InconsistentNaming
    private static bool Prefix(Aircraft __instance, byte __0)
    {
        if (!Plugin.PluginEnabled)
            return true;
        
        if (__instance == null)
            return false;
        
        if (__instance.LocalSim)
            return true;
        
        var stations = __instance.weaponStations;
        var count = stations?.Count ?? 0;
        if (__0 < count && stations![__0] != null)
            return true;
        
        if (Plugin.DebugLogs && TurretDiagnostics.ShouldLog(__instance, __0, true))
        {
            Plugin.Logger.LogWarning(
                $"--- Turret RPC received/ignored\n" +
                $"NetId: {__instance.NetId}, PersistentID: {__instance.persistentID}, " +
                $"Unit: {__instance.unitName}, Definition: {__instance.definition?.jsonKey}, " +
                $"StationIndex: {__0}, ClientStationCount: {count}, " +
                $"LocalSim: {__instance.LocalSim}, State: {__instance.unitState}, " +
                $"Reason: {(__0 >= count ? "out of range" : "null station")}\n" +
                $"StationLayout: {TurretDiagnostics.DescribeStations(__instance)}");
            StationFollowup.Queue(__instance, __0, "Turret RPC", count);
        }
        
        return false;
    }
}

[HarmonyPatch(typeof(Aircraft), nameof(Aircraft.RpcSetTurretVector))]
internal static class TurretSendPatch
{
    [HarmonyPrefix]
    [UsedImplicitly]
    // ReSharper disable once InconsistentNaming
    private static bool Prefix(Aircraft __instance, byte weaponStationIndex)
    {
        if (!Plugin.PluginEnabled)
            return true;
        
        if (__instance == null)
            return false;
        
        var stations = __instance.weaponStations;
        var count = stations?.Count ?? 0;
        var valid = weaponStationIndex < count && stations![weaponStationIndex] != null;
        if (!Plugin.DebugLogs || !TurretDiagnostics.ShouldLog(__instance, weaponStationIndex, !valid))
            return valid;
        
        var stationName = valid ? stations![weaponStationIndex].WeaponInfo?.weaponName : "(missing)";
        var message = $"--- Turret RPC sending{(valid ? "" : "/ignored")}\n" +
                      $"NetId: {__instance.NetId}, PersistentID: {__instance.persistentID}, " +
                      $"Unit: {__instance.unitName}, Definition: {__instance.definition?.jsonKey}, " +
                      $"StationIndex: {weaponStationIndex}, ServerStationCount: {count}, " +
                      $"StationName: {stationName}";
        
        if (valid)
            Plugin.Logger.LogInfo(message);
        else
            Plugin.Logger.LogWarning(message + "\nStationLayout: " + TurretDiagnostics.DescribeStations(__instance));
        
        return valid;
    }
}

internal static class TurretDiagnostics
{
    private static readonly ConditionalWeakTable<Aircraft, PerUnitLogState> States = new();
    
    internal static bool ShouldLog(Aircraft aircraft, byte stationIndex, bool invalid)
    {
        var state = States.GetValue(aircraft, _ => new PerUnitLogState());
        var key = stationIndex | (invalid ? 0x100 : 0);
        var now = Time.realtimeSinceStartup;
        if (state.NextLog.TryGetValue(key, out var nextTime) && now < nextTime)
            return false;
        
        state.NextLog[key] = now + 30f;
        return true;
    }
    
    internal static string DescribeStations(Unit aircraft)
    {
        var stations = aircraft.weaponStations;
        if (stations == null)
            return "(null list)";
        if (stations.Count == 0)
            return "(empty list)";
        
        var builder = new StringBuilder(256);
        var limit = Math.Min(stations.Count, 32);
        for (var i = 0; i < limit; i++)
        {
            if (i != 0)
                builder.Append(" | ");
            
            var station = stations[i];
            if (station == null)
            {
                builder.Append(i).Append(": (null)");
                continue;
            }
            
            builder.Append(i).Append(": #").Append(station.Number)
                .Append(" ").Append(station.WeaponInfo?.weaponName ?? "(unknown weapon)")
                .Append(" turrets=").Append(station.Turrets?.Count ?? 0);
        }
        
        if (stations.Count > limit)
            builder.Append(" | ... +").Append(stations.Count - limit).Append(" more");
        return builder.ToString();
    }
    
    private sealed class PerUnitLogState
    {
        internal readonly Dictionary<int, float> NextLog = new();
    }
}

internal static class StationFollowup
{
    private static readonly ConditionalWeakTable<Unit, State> States = new();
    
    internal static void Queue(Unit unit, byte stationIndex, string source, int initialCount)
    {
        if (unit == null || Plugin.Instance == null)
            return;
        
        var state = States.GetValue(unit, _ => new State());
        if (state.Queued)
            return;
        
        state.Queued = true;
        var identity = $"NetId: {unit.NetId}, PersistentID: {unit.persistentID}, " +
                       $"Unit: {unit.unitName}, Definition: {unit.definition?.jsonKey}";
        Plugin.Instance.StartCoroutine(CheckLater(unit, stationIndex, source, initialCount, identity));
    }
    
    private static IEnumerator CheckLater(Unit unit, byte stationIndex, string source, int initialCount,
        string identity)
    {
        yield return new WaitForSecondsRealtime(10f);
        if (!Plugin.PluginEnabled || !Plugin.DebugLogs)
            yield break;
        
        if (unit == null)
        {
            Plugin.Logger.LogWarning($"--- Station follow-up (+10s, {source})\n" +
                                     $"{identity}, StationIndex: {stationIndex}, " +
                                     $"InitialStationCount: {initialCount}, " +
                                     "Result: unit destroyed/unloaded before follow-up");
            yield break;
        }
        
        try
        {
            var stations = unit.weaponStations;
            var count = stations?.Count ?? 0;
            var nowValid = stationIndex < count && stations![stationIndex] != null;
            Plugin.Logger.LogWarning($"--- Station follow-up (+10s, {source})\n" +
                                     $"{identity}, StationIndex: {stationIndex}, " +
                                     $"InitialStationCount: {initialCount}, " +
                                     $"ClientStationCountNow: {count}, " +
                                     $"RequestedIndexValidNow: {nowValid}\n" +
                                     $"StationLayout: {TurretDiagnostics.DescribeStations(unit)}");
        }
        catch (Exception ex)
        {
            Plugin.Logger.LogWarning($"--- Station follow-up failed ({source})\n{identity}\n{ex}");
        }
    }
    
    private sealed class State
    {
        internal bool Queued;
    }
}

[HarmonyPatch(typeof(Unit), nameof(Unit.RpcRearm))]
internal static class RearmSendPatch
{
    [HarmonyPrefix]
    [UsedImplicitly]
    // ReSharper disable once InconsistentNaming
    private static void Prefix(Unit __instance, RearmEventArgs args)
    {
        if (!Plugin.PluginEnabled || !Plugin.DebugLogs || __instance == null ||
            !RearmDiagnostics.ShouldLog(__instance, 0))
            return;
        
        var stationCount = __instance.weaponStations?.Count ?? 0;
#pragma warning disable Harmony003
        var payloadCount = args.Stations?.Length ?? -1;
#pragma warning restore Harmony003
        var mismatch = payloadCount != stationCount;
        
        var message = $"--- Rearm RPC sending{(mismatch ? " (STATION COUNT MISMATCH)" : "")}\n" +
                      RearmDiagnostics.Describe(__instance, args, "Server");
        if (mismatch)
            Plugin.Logger.LogWarning(message);
        else
            Plugin.Logger.LogInfo(message);
    }
}

[HarmonyPatch]
internal static class RearmReceivePatch
{
    [UsedImplicitly]
    private static MethodBase TargetMethod()
    {
        var target = AccessTools.Method(typeof(Unit), "UserCode_RpcRearm_1512805087",
            [typeof(RearmEventArgs)]);
        if (target != null)
            return target;
        
        foreach (var method in AccessTools.GetDeclaredMethods(typeof(Unit)))
        {
            if (!method.Name.StartsWith("UserCode_RpcRearm", StringComparison.Ordinal))
                continue;
            var parameters = method.GetParameters();
            if (parameters.Length == 1 && parameters[0].ParameterType == typeof(RearmEventArgs))
                return method;
        }
        
        throw new MissingMethodException("Could not locate Unit.UserCode_RpcRearm(RearmEventArgs).");
    }
    
    [HarmonyPrefix]
    [UsedImplicitly]
    // ReSharper disable once InconsistentNaming
    private static bool Prefix(Unit __instance, RearmEventArgs args)
    {
        if (!Plugin.PluginEnabled)
            return true;
        if (__instance == null)
            return false;
        
        var stations = __instance.weaponStations;
        var stationCount = stations?.Count ?? 0;
#pragma warning disable Harmony003
        var payloadCount = args.Stations?.Length ?? -1;
#pragma warning restore Harmony003
        
        // The vanilla method loops over weaponStations.Count, indexing args.Stations[i], and
        // weaponStations[i].Weapons[0] without null check / validation
        var problem = stations == null ? "null weaponStations" :
            payloadCount < 0 ? "null Stations payload" :
            payloadCount != stationCount ? "station count mismatch" : null;
        
        if (problem == null && stations != null)
            for (var i = 0; i < stationCount; i++)
            {
                var station = stations[i];
                if (station == null || station.Weapons == null || station.Weapons.Count == 0)
                {
                    problem = $"station {i} has no weapon";
                    break;
                }
            }
        
        if (problem == null)
        {
            if (Plugin.DebugLogs && RearmDiagnostics.ShouldLog(__instance, 3))
                Plugin.Logger.LogInfo(
                    "--- Rearm RPC received\n" + RearmDiagnostics.Describe(__instance, args, "Client"));
            return true;
        }
        
        if (Plugin.DebugLogs && RearmDiagnostics.ShouldLog(__instance, 1))
            Plugin.Logger.LogWarning($"--- Rearm RPC received/ignored: {problem}\n" +
                                     RearmDiagnostics.Describe(__instance, args, "Client"));
        
        RearmDiagnostics.QueueFollowup(__instance, stationCount, payloadCount);
        return false;
    }
    
    [HarmonyFinalizer]
    [UsedImplicitly]
    // ReSharper disable once InconsistentNaming
    private static Exception? Finalizer(Unit __instance, RearmEventArgs args,
        // ReSharper disable once InconsistentNaming
        Exception? __exception)
    {
        if (__exception == null || !Plugin.PluginEnabled)
            return __exception;
        
        // This also catches issues later in the method, including CountermeasureManager.Rearm(args),
        // which the prefix cannot pre validate
        if (Plugin.DebugLogs && __instance != null && RearmDiagnostics.ShouldLog(__instance, 2))
            try
            {
                Plugin.Logger.LogError("--- Rearm RPC unexpected exception\n" +
                                       RearmDiagnostics.Describe(__instance, args, "Client") +
                                       "\nException: " + __exception);
            }
            catch (Exception diagnosticError)
            {
                Plugin.Logger.LogError("Rearm diagnostics failed: " + diagnosticError);
            }
        
        return __exception;
    }
}

internal static class RearmDiagnostics
{
    private static readonly ConditionalWeakTable<Unit, LogState> LogStates = new();
    private static readonly ConditionalWeakTable<Unit, FollowupState> FollowupStates = new();
    
    internal static bool ShouldLog(Unit unit, int kind)
    {
        var state = LogStates.GetValue(unit, _ => new LogState());
        var now = Time.realtimeSinceStartup;
        if (state.NextLog.TryGetValue(kind, out var next) && now < next)
            return false;
        state.NextLog[kind] = now + 30f;
        return true;
    }
    
    internal static string Describe(Unit unit, RearmEventArgs args, string side)
    {
        var values = args.Stations;
        var stationCount = unit.weaponStations?.Count ?? 0;
        var payloadCount = values?.Length ?? -1;
        
        var sb = new StringBuilder(350);
        sb.Append("NetId: ").Append(unit.NetId)
            .Append(", PersistentID: ").Append(unit.persistentID)
            .Append(", Unit: ").Append(unit.unitName)
            .Append(", Definition: ").Append(unit.definition?.jsonKey)
            .Append(", State: ").Append(unit.unitState).Append('\n')
            .Append(side).Append("StationCount: ").Append(stationCount)
            .Append(", RearmPayloadLength: ").Append(payloadCount)
            .Append(", RearmPayload: ");
        
        if (values == null)
        {
            sb.Append("(null)");
        }
        else
        {
            sb.Append('[');
            var limit = Math.Min(values.Length, 32);
            for (var i = 0; i < limit; i++)
            {
                if (i != 0) sb.Append(", ");
                sb.Append(values[i]);
            }
            
            if (values.Length > limit) sb.Append(", ...");
            sb.Append(']');
        }
        
        sb.Append("\nStationLayout: ").Append(TurretDiagnostics.DescribeStations(unit));
        return sb.ToString();
    }
    
    internal static void QueueFollowup(Unit unit, int originalCount, int payloadCount)
    {
        if (!Plugin.DebugLogs || unit == null || Plugin.Instance == null)
            return;
        
        var state = FollowupStates.GetValue(unit, _ => new FollowupState());
        if (state.Queued)
            return;
        state.Queued = true;
        
        var identity = $"NetId: {unit.NetId}, PersistentID: {unit.persistentID}, " +
                       $"Unit: {unit.unitName}, Definition: {unit.definition?.jsonKey}";
        Plugin.Instance.StartCoroutine(CheckLater(unit, originalCount, payloadCount, identity));
    }
    
    private static IEnumerator CheckLater(Unit unit, int originalCount, int payloadCount, string identity)
    {
        yield return new WaitForSecondsRealtime(10f);
        if (!Plugin.PluginEnabled || !Plugin.DebugLogs)
            yield break;
        
        if (unit == null)
        {
            Plugin.Logger.LogWarning($"--- Rearm follow-up (+10s)\n{identity}\n" +
                                     "Result: unit destroyed/unloaded");
            yield break;
        }
        
        try
        {
            var nowCount = unit.weaponStations?.Count ?? 0;
            Plugin.Logger.LogWarning($"--- Rearm follow-up (+10s)\n{identity}\n" +
                                     $"InitialClientStationCount: {originalCount}, " +
                                     $"RearmPayloadLength: {payloadCount}, " +
                                     $"ClientStationCountNow: {nowCount}, " +
                                     $"CountsMatchNow: {nowCount == payloadCount}\n" +
                                     $"StationLayout: {TurretDiagnostics.DescribeStations(unit)}");
        }
        catch (Exception ex)
        {
            Plugin.Logger.LogWarning($"--- Rearm follow-up failed\n{identity}\n{ex}");
        }
    }
    
    private sealed class LogState
    {
        internal readonly Dictionary<int, float> NextLog = new();
    }
    
    private sealed class FollowupState
    {
        internal bool Queued;
    }
}