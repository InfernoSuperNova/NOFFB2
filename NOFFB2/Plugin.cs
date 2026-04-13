using System;
using BepInEx;
using BepInEx.Logging;
using System.Threading;
using UnityEngine;

namespace NOFFB2;

[BepInPlugin(MyPluginInfo.PLUGIN_GUID, MyPluginInfo.PLUGIN_NAME, MyPluginInfo.PLUGIN_VERSION)]
public class Plugin : BaseUnityPlugin
{
    internal static new ManualLogSource Logger;
    private static FFServer _ffserver;
    private static bool _applicationQuitting;
    public System.Random RNG;

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
        DontDestroyOnLoad(gameObject);
        Logger.LogInfo($"Plugin {MyPluginInfo.PLUGIN_GUID} is loading");
        I = this;
        _applicationQuitting = false;
        RNG = new System.Random();

        if (_ffserver == null)
        {
            _ffserver = new FFServer();
            _ffserver.Start();
        }

        MarchOfTheSith();
        //GunsTest();
        //_ffserver.PlayNativeSine(0.1f, 20, 0.2f);
        
        Logger.LogInfo($"Plugin {MyPluginInfo.PLUGIN_GUID} is loaded");
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
            _ffserver?.Stop();
            _ffserver = null;
        }
    }

    private void OnApplicationQuit()
    {
        Logger?.LogInfo("Plugin OnApplicationQuit");
        _applicationQuitting = true;
    }


    private void GunsTest()
    {
        ThreadPool.QueueUserWorkItem(_ =>
        {
            while (true)
            {

                _ffserver?.AddEffect(new FFDecayingSine(0.1f, 20, 0.1f));
                Thread.Sleep(50);
            }

        });

    }
    

    private void MarchOfTheSith()
    {
        ThreadPool.QueueUserWorkItem(_ =>
        {
            // Rough haptic adaptation of the first 9 notes.
            var notes = new (float Frequency, float Duration, float Amplitude, int DelayMs)[]
            {
                (196.00f, 0.25f, 0.2f,   0),  // G3
                (196.00f, 0.25f, 0.2f, 300),  // G3
                (196.00f, 0.25f, 0.2f, 300),  // G3
                (155.56f, 0.20f, 0.16f, 300),  // Eb3
                (233.08f, 0.10f, 0.24f, 200),  // Bb3
                (196.00f, 0.25f, 0.2f, 150),  // G3
                (155.56f, 0.20f, 0.16f, 300),  // Eb3
                (233.08f, 0.10f, 0.24f, 200),  // Bb3
                (196.00f, 0.50f, 0.2f, 150),  // G3
            };

            foreach (var note in notes)
            {
                if (note.DelayMs > 0)
                {
                    Thread.Sleep(note.DelayMs);
                }

                //_ffserver?.AddEffect(new FFDecayingSine(note.Duration, note.Frequency, note.Amplitude));
                _ffserver.PlayNativeSine(note.Duration, note.Frequency, note.Amplitude);
            }
        });
    }
}
