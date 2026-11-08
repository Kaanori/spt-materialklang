using System;
using System.Runtime.CompilerServices;
using Audio.SpatialSystem;
using HarmonyLib;

namespace Materialklang
{
    // 0.8.3: Filter jeder Quelle aus ihrer EIGENEN Verdeckung (Ursache und Rechnung siehe QuellWirkungKern.cs).
    // Aus mit "1. General / Steady occlusion" oder Plugin aus.
    internal static class QuellWirkung
    {
        static readonly ConditionalWeakTable<SpatialAudioOccluder, WirkungJeQuelle> Tabelle =
            new ConditionalWeakTable<SpatialAudioOccluder, WirkungJeQuelle>();
        static readonly ConditionalWeakTable<SpatialAudioOccluder, WirkungJeQuelle>.CreateValueCallback Neu = _ => new WirkungJeQuelle();

        internal static int Geprueft, Korrigiert;

        static bool An => Plugin.Aktiv.Value && Stetigkeit.An;

        internal static void Anwenden(Harmony h)
        {
            try
            {
                var calc = AccessTools.DeclaredMethod(typeof(SpatialAudioOccluder), "CalculateFinalOcclusionParams");
                var apply = AccessTools.DeclaredMethod(typeof(SpatialAudioOccluder), "ApplyOcclusionToSource", new[] { typeof(BetterSource), typeof(bool) });
                if (calc == null || apply == null)
                {
                    Plugin.Log.LogWarning("Per-source occlusion: SpatialAudioOccluder targets not found -> game behaviour (cached updates may use another sound's occlusion).");
                    return;
                }
                h.Patch(calc, postfix: new HarmonyMethod(typeof(QuellWirkung), nameof(RechnungNach)) { priority = Priority.Last });
                h.Patch(apply, prefix: new HarmonyMethod(typeof(QuellWirkung), nameof(AnwendenVor)) { priority = Priority.First });
            }
            catch (Exception e) { Statistik.Fehler(e); }
        }

        // Postfix SpatialAudioOccluder.CalculateFinalOcclusionParams: die Wirkung dieser Quelle merken.
        // Einmal-Klaenge (MultiSource) rechnen vor jedem Anwenden neu - dort ist nichts zu merken.
        static void RechnungNach(SpatialAudioOccluder __instance, BetterSource source)
        {
            if (source == null || __instance is MultiSourceSpatialAudioOccluder || !An) return;
            try { Tabelle.GetValue(__instance, Neu).Merken(source.GetInstanceID(), __instance.OcclusionEffect); }
            catch (Exception e) { Statistik.Fehler(e); }
        }

        // Prefix SpatialAudioOccluder.ApplyOcclusionToSource (vor allen anderen): die eigene Wirkung der Quelle einsetzen,
        // aus der FilterCutoffCalculator.Compute gleich den Tiefpass rechnet.
        static void AnwendenVor(SpatialAudioOccluder __instance, BetterSource source)
        {
            if (source == null || !An) return;
            try
            {
                if (!Tabelle.TryGetValue(__instance, out var t)) return;
                float w = t.Fuer(source.GetInstanceID(), __instance.OcclusionEffect, out bool bekannt, out bool korrigiert);
                if (!bekannt) return;
                Geprueft++;
                if (!korrigiert) return;
                __instance.OcclusionEffect = w;
                Korrigiert++;
            }
            catch (Exception e) { Statistik.Fehler(e); }
        }

        internal static void Loggen()
        {
            if (Geprueft == 0) return;
            Plugin.Log.LogInfo($"[Diagnostics] Per-source occlusion: {Korrigiert} of {Geprueft} filter updates would have used the occlusion of another sound of the same source (game keeps one value per occluder) - now their own.");
            Geprueft = Korrigiert = 0;
        }
    }
}
