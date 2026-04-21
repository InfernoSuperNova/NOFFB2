using System;
using System.Collections;
using BepInEx;
using BepInEx.Bootstrap;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;

namespace NOFFB2;

[BepInPlugin(MyPluginInfo.PLUGIN_GUID, MyPluginInfo.PLUGIN_NAME, MyPluginInfo.PLUGIN_VERSION)]
public class Plugin : BaseUnityPlugin
{

    internal static new ManualLogSource Logger;
    private static FFServer _ffserver;
    private static AircraftForceManager _aircraftForceManager;
    private static Harmony _harmony;
    
    private static bool _applicationQuitting;
    public System.Random RNG;
    public Action UpdateDispatcher;
    public Action FixedUpdateDispatcher;
    private bool _loggedUpdateLoopStart;

    public static Plugin I;

    private void Awake()
    {
        if (I != null && I != this)
        {
            Logger = base.Logger;
            Logger.LogInfo("Duplicate Plugin instance detected, destroying the new one.");
            Destroy(this);
            return;
        }

        Logger = base.Logger;
        PreserveChainloaderManagerObject();
        Logger.LogInfo($"Plugin {MyPluginInfo.PLUGIN_GUID} is loading");

        I = this;
        _applicationQuitting = false;
        RNG = new System.Random();

        EnsureHarmonyPatched();
        EnsureForceFeedbackServer();
        EnsureAircraftForceManager();


        Logger.LogInfo($"Plugin {MyPluginInfo.PLUGIN_GUID} is loaded");
    }

    private static void PreserveChainloaderManagerObject()
    {
        var managerObject = Chainloader.ManagerObject;
        if (managerObject == null)
        {
            return;
        }

        managerObject.hideFlags = HideFlags.HideAndDontSave;
        DontDestroyOnLoad(managerObject);
        Logger?.LogWarning("Force Hide ManagerGameObject");
    }

    private static void EnsureHarmonyPatched()
    {
        if (_harmony != null)
        {
            return;
        }

        _harmony = new Harmony(MyPluginInfo.PLUGIN_GUID);
        _harmony.PatchAll();
        Logger?.LogInfo("Harmony patches applied.");
    }

    private static void EnsureForceFeedbackServer()
    {
        if (_ffserver != null)
        {
            return;
        }

        _ffserver = new FFServer();
        _ffserver.Start();
    }

    private void EnsureAircraftForceManager()
    {
        if (_aircraftForceManager != null)
        {
            return;
        }

        _aircraftForceManager = new AircraftForceManager(this);
    }

    private void OnEnable()
    {
        Logger?.LogInfo("Plugin OnEnable");
    }

    private void OnDisable()
    {
        Logger?.LogInfo("Plugin OnDisable");
    }

    private void OnDestroy()
    {
        Logger?.LogInfo("Plugin OnDestroy");
        if (I == this)
        {
            I = null;
        }

        if (_applicationQuitting)
        {
            _harmony?.UnpatchSelf();
            _harmony = null;
            _ffserver?.Stop();
            _ffserver = null;
        }
    }

    private void OnApplicationQuit()
    {
        Logger?.LogInfo("Plugin OnApplicationQuit");
        _applicationQuitting = true;
    }

    private void Update()
    {
        if (!_loggedUpdateLoopStart)
        {
            Logger?.LogInfo("Plugin Update loop is running.");
            _loggedUpdateLoopStart = true;
        }

        try
        {
            UpdateDispatcher?.Invoke();
        }
        catch (Exception ex)
        {
            Logger?.LogError($"Unhandled exception in UpdateDispatcher: {ex}");
        }
    }

    private void FixedUpdate()
    {
        try
        {
            FixedUpdateDispatcher?.Invoke();
        }   
        catch (Exception ex)
        {
            Logger?.LogError($"Unhandled exception in UpdateDispatcher: {ex}");
        }
    }

    public static void Log(object log, LogLevel logLevel = LogLevel.Info) => Logger.Log(logLevel, $"{MyPluginInfo.PLUGIN_GUID} : {log}");
    
    
}
