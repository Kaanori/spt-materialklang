using System;
using Audio.SpatialSystem.SpatialAudioCalculator;
using HarmonyLib;

namespace Materialklang
{
    // 0.8.4 - Spiel-Teil zu ReflexionDirektKern.cs (Ursache und Beleg dort). Gehoert zu "Steady occlusion" (1. General):
    // aus = Spielverhalten (nur zum Vergleich).
    [HarmonyPatch(typeof(ReflectionCalculator), nameof(ReflectionCalculator.UpdateCalculationStep))]
    internal static class ReflexionDirektweg
    {
        // Diagnose (alle 30 s ins Log)
        internal static int Gesichert, Verdeckt, Verhindert;

        static void Prefix(ReflectionCalculator __instance)
        {
            if (!Plugin.Aktiv.Value || !Stetigkeit.An) return;
            try
            {
                var c = __instance;
                if (c.bool_1 || !c.bool_2 || !c._isCalculationInProgress) return;
                if (!c._lastJobHandle.IsCompleted) return;

                if (ReflexionDirekt.StufeEinsSichern(true, true, c._currentReflectionLevel, c._maxReflections, c._directPathCommandIndex))
                {
                    c._lastJobHandle.Complete();               // ist fertig - nur fuer den sicheren Zugriff auf den Puffer
                    var buf = c._batchProcessingResultBuffer;
                    if (!buf.IsCreated || buf.Length == 0) return;
                    if (buf[0].foundDirectPathClearThisBatch) c.IsDirectPathClear = true;
                    else Verdeckt++;
                    c._directPathCommandIndex = -1;            // ab Stufe 2: kein (falscher) Direkt-Strahl mehr
                    Gesichert++;
                    return;
                }

                // Nur Diagnose: spaetere Stufe, Direktweg in Stufe 1 verdeckt - haette das Spiel hier "frei" gelesen?
                if (c._directPathCommandIndex < 0 && !c.IsDirectPathClear && c._currentReflectionLevel >= 2
                    && c._currentReflectionLevel < c._maxReflections && c._spherecastHits.IsCreated && c._spherecastHits.Length > 0)
                {
                    c._lastJobHandle.Complete();
                    if (c._spherecastHits[0].colliderInstanceID == 0) Verhindert++;
                }
            }
            catch (Exception e) { Statistik.Fehler(e); }
        }

        internal static void Loggen()
        {
            if (Gesichert == 0) return;
            Plugin.Log.LogInfo($"[Diagnostics] Reflection direct path: {Gesichert} calculations kept the direct-ray result of step 1 " +
                               $"({Verdeckt} blocked), {Verhindert} later steps where the game would have read a reflection ray as 'direct path clear' (R = 0).");
            Gesichert = Verdeckt = Verhindert = 0;
        }
    }
}
