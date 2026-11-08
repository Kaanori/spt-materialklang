using System;
using Audio.SpatialSystem;
using HarmonyLib;

namespace Materialklang
{
    // 0.8.2: Einmal-Klaenge des MultiSource-Occluders bekommen nur noch ihre EIGENE Verdeckung (Ursache siehe EigeneWirkung.cs).
    internal static class MultiQuelle
    {
        internal static void Anwenden(Harmony h)
        {
            try
            {
                var m = AccessTools.DeclaredMethod(typeof(MultiSourceSpatialAudioOccluder), "OnUpdateOcclusionEffects");
                if (m == null) { Plugin.Log.LogWarning("One-shot sounds: MultiSourceSpatialAudioOccluder.OnUpdateOcclusionEffects not found -> game behaviour (may inherit occlusion of other sounds)."); return; }
                h.Patch(m, prefix: new HarmonyMethod(typeof(MultiQuelle), nameof(Vor)) { priority = Priority.First });
            }
            catch (Exception e) { Statistik.Fehler(e); }
        }

        // Prefix MultiSourceSpatialAudioOccluder.OnUpdateOcclusionEffects: laeuft direkt nach dem Setzen der Wertung dieses
        // Behaelters (OnLateUpdate) und vor CalculateFinalOcclusionParams/ApplyOcclusionToSource.
        static void Vor(MultiSourceSpatialAudioOccluder __instance, AudioSourceContainer sourceContainer)
        {
            if (!Plugin.Aktiv.Value || sourceContainer == null) return;
            try
            {
                int id = sourceContainer.ID;
                bool weg = __instance._activePropagationCalculatorsByContID.ContainsKey(id);
                bool hindernis = __instance._activeTransmissionCalculatorsByContID.ContainsKey(id);
                float w = __instance.PropagationEffect, o = __instance.ObstructionEffect;
                EigeneWirkung.Geprueft++;
                if (!EigeneWirkung.Bereinigen(weg, hindernis, ref w, ref o)) return;
                __instance.PropagationEffect = w;
                __instance.ObstructionEffect = o;
                EigeneWirkung.Korrigiert++;
            }
            catch (Exception e) { Statistik.Fehler(e); }
        }

        internal static void Loggen()
        {
            if (EigeneWirkung.Geprueft == 0) return;
            Plugin.Log.LogInfo($"[Diagnostics] One-shot sounds: {EigeneWirkung.Korrigiert} of {EigeneWirkung.Geprueft} would have inherited the occlusion of another sound (game shares one value for all one-shots) - now their own.");
            EigeneWirkung.Geprueft = EigeneWirkung.Korrigiert = 0;
        }
    }
}
