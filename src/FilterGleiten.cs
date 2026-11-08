using System;
using Audio.Effects;
using HarmonyLib;
using UnityEngine;

namespace Materialklang
{
    // 0.8.1: Die Filter der Spielquellen (AudioEQFilterBase, auch die der SEB-Schuesse) gleiten in Oktaven statt linear in Hz.
    // Ersetzt AudioEQFilterBase.ManualUpdate 1:1, nur die beiden Grenzfrequenzen laufen ueber Stetigkeit.FrequenzSchritt
    // (Guete Q wie im Spiel). Aus ("1. General / Steady occlusion" oder Plugin aus) = Spielcode unveraendert.
    // Ein sofortiges Setzen (applyImmediately) geht wie bisher direkt an den Filter; wann das noch passiert,
    // regelt SebAkustik.SofortPruefen.
    internal static class FilterGleiten
    {
        internal static void Anwenden(Harmony h)
        {
            try
            {
                var m = AccessTools.DeclaredMethod(typeof(AudioEQFilterBase), nameof(AudioEQFilterBase.ManualUpdate));
                if (m == null) { Plugin.Log.LogWarning("Steady occlusion: AudioEQFilterBase.ManualUpdate not found -> filters glide as in the game."); return; }
                h.Patch(m, prefix: new HarmonyMethod(typeof(FilterGleiten), nameof(UpdateVor)));
            }
            catch (Exception e) { Statistik.Fehler(e); }
        }

        static bool UpdateVor(AudioEQFilterBase __instance)
        {
            if (!Plugin.Aktiv.Value || !Stetigkeit.An) return true;
            try
            {
                if (!__instance.enabled || !__instance._isInitialized) return true;   // Spiel: tut dann nichts
                float dt = Time.deltaTime, k = dt * __instance.lerpSpeed;
                bool neu = false;
                if (!Mathf.Approximately(__instance._currentHpf, __instance._targetHpf))
                {
                    __instance._currentHpf = Stetigkeit.FrequenzSchritt(__instance._currentHpf, __instance._targetHpf, dt);
                    neu = true;
                }
                if (!Mathf.Approximately(__instance._currentHpfQ, __instance._targetHpfQ))
                {
                    __instance._currentHpfQ = Mathf.Lerp(__instance._currentHpfQ, __instance._targetHpfQ, k);
                    neu = true;
                }
                if (!Mathf.Approximately(__instance._currentLpf, __instance._targetLpf))
                {
                    __instance._currentLpf = Stetigkeit.FrequenzSchritt(__instance._currentLpf, __instance._targetLpf, dt);
                    neu = true;
                }
                if (!Mathf.Approximately(__instance._currentLpfQ, __instance._targetLpfQ))
                {
                    __instance._currentLpfQ = Mathf.Lerp(__instance._currentLpfQ, __instance._targetLpfQ, k);
                    neu = true;
                }
                if (neu) __instance.method_0();
                __instance.OnUpdate();
                return false;
            }
            catch (Exception e) { Statistik.Fehler(e); return true; }
        }
    }
}
