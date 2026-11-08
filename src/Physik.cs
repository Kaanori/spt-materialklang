using System;
using Audio.SpatialSystem;
using Audio.SpatialSystem.Utils;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;

namespace Materialklang
{
    // Physik-Modus (Physics Mode): reale Bauakustik statt Spielwerte, fuer ALLE Geraeusche.
    //  - Daemmung je Wandstueck nach Massengesetz: TL = 20*log10(Dichte*Dicke*f) - 47 dB (f = 250 Hz), gedeckelt (Flankenweg).
    //  - Einheitliche Skala: Wertung 0..1 = Daemmung 0..Dynamik dB (Hindernis, Raumweg, Tueren, Wand zwischen Raeumen).
    //  - Lautstaerke = 10^(-Dynamik*Wertung/20) statt Spielkurve (die endet bei -4.4 dB).
    //  - Tiefpass = 22 kHz -> Grenzfrequenz bei voller Daemmung, ohne die 8-kHz-Untergrenze des Spiels.
    //  - Der leichteste Weg zaehlt: Durchdringung/Beugung/Reflexion -> Minimum statt Mittelwert.
    internal static class Physik
    {
        internal static ConfigEntry<bool> Aktiv;
        internal static ConfigEntry<float> Dynamik, Frequenz, TiefpassMin, Freiweg, Lautheit;
        internal static readonly ConfigEntry<float>[] Dichte = new ConfigEntry<float>[Plugin.AnzahlGruppen];
        internal static readonly ConfigEntry<float>[] Massiv = new ConfigEntry<float>[Plugin.AnzahlGruppen];

        // kg/m^3 (Gitter/Zaun/Gras = offen -> 0). Reihenfolge wie Gruppe.
        static readonly float[] StartDichte = { 2300f, 1700f, 1600f, 7850f, 7850f, 600f, 500f, 2500f, 500f, 0f, 1000f, 1000f, 2000f, 600f };
        // Groesste plausible massive Dicke (m). Dicker gemessen = hohles Objekt (Auto, Container, Haus) -> 2 Waende dieser Staerke.
        static readonly float[] StartMassiv = { 1.0f, 3f, 3f, 0.01f, 0.003f, 0.3f, 0.05f, 0.02f, 0.1f, 0f, 0.5f, 3f, 0.5f, 0.06f };

        internal static bool An => Aktiv != null && Aktiv.Value && Plugin.Aktiv.Value;

        internal static void Binden(ConfigFile c)
        {
            const string s = "0. Physics Mode (real values)";
            Aktiv = c.Bind(s, "Enabled", true,
                "Real building acoustics (mass law) for all sounds. Thick walls = near silence, like in real life. Off = game values / the per-material sliders in sections 2.xx.");
            Dynamik = c.Bind(s, "Max. insulation (dB)", 60f,
                new ConfigDescription("Highest insulation of one path. In reality side paths (ceilings, pipes) limit it to about 55-65 dB.", new AcceptableValueRange<float>(20f, 90f)));
            Lautheit = c.Bind(s, "Loudness factor", 0.2f,
                new ConfigDescription("Compresses the real insulation to game loudness. A real gunshot is ~160 dB, headphones play ~90 dB. 1.0 = uncompressed (silence behind concrete), 0.2 = default, tuned by ear.", new AcceptableValueRange<float>(0.1f, 1f)));
            Frequenz = c.Bind(s, "Reference frequency (Hz)", 250f,
                new ConfigDescription("Frequency at which loudness is evaluated. 250 Hz = the bass you hear through walls.", new AcceptableValueRange<float>(63f, 1000f)));
            TiefpassMin = c.Bind(s, "Low-pass at full insulation (Hz)", 800f,
                new ConfigDescription("How muffled it sounds at maximum insulation.", new AcceptableValueRange<float>(150f, 4000f)));
            Freiweg = c.Bind(s, "Clear-path threshold", 0.02f,
                new ConfigDescription("Score from which a path counts as occluded (0.02 = about 1 dB). Game: 0.5 = 30 dB. Takes effect from the next raid.", new AcceptableValueRange<float>(0f, 0.5f)));
            string[] namen = { "Concrete/Stone", "Soil/Terrain", "Gravel/Snow", "Metal thick", "Metal thin", "Wood thick", "Wood thin",
                               "Glass", "Soft", "Grate/Fence/Grass", "Body", "Water", "Unknown", "Door leaf" };
            for (int i = 0; i < Plugin.AnzahlGruppen; i++)
            {
                Dichte[i] = c.Bind(s, $"Density {i + 1:00} {namen[i]} (kg/m3)", StartDichte[i],
                    new ConfigDescription("Material density used by the mass law (0 = open, no insulation).", new AcceptableValueRange<float>(0f, 12000f)));
                Massiv[i] = c.Bind(s, $"Solid up to {i + 1:00} {namen[i]} (m)", StartMassiv[i],
                    new ConfigDescription("Measured thicker than this = hollow object: counts as two walls of this thickness.", new AcceptableValueRange<float>(0f, 5f)));
            }
        }

        // Daemmung (dB) eines Stuecks Material der Gruppe g mit Dicke d (m).
        internal static float Daemmung(int g, float dicke)
        {
            float mx = Massiv[g].Value;
            if (dicke > mx) dicke = 2f * mx;                 // hohles Objekt: zwei Schalen
            float m = Dichte[g].Value * Mathf.Max(dicke, 0.002f);
            if (m <= 0f) return 0f;
            return Mathf.Clamp(20f * Mathf.Log10(m * Frequenz.Value) - 47f, 0f, Dynamik.Value);
        }

        internal static float EnergieAusDb(float db) => Mathf.Pow(10f, -db / 10f);
        internal static float DbAusEnergie(float e) => -10f * Mathf.Log10(Mathf.Max(e, 1e-12f));
        internal static float WertungAusDb(float db) => Mathf.Clamp01(db / Dynamik.Value);
        internal static float WertungAusEnergie(float e) => WertungAusDb(DbAusEnergie(e));

        internal static void Anwenden(Harmony h)
        {
            Patch(h, AccessTools.DeclaredMethod(typeof(SpatialAudioOccluder), "CalculateFinalOcclusionParams"), nameof(LautstaerkeNach), Priority.Normal);
            Patch(h, AccessTools.DeclaredMethod(typeof(FilterCutoffCalculator), "Compute"), nameof(FilterNach), Priority.First);
            Patch(h, AccessTools.DeclaredMethod(typeof(SpatialAudioOcclusionCombinedCalculator), "GetScore"), nameof(WertungNach), Priority.Normal);
        }

        static void Patch(Harmony h, System.Reflection.MethodInfo ziel, string methode, int prio)
        {
            if (ziel == null) { Plugin.Log.LogWarning($"Physics mode: target for {methode} not found -> this part stays vanilla."); return; }
            h.Patch(ziel, postfix: new HarmonyMethod(typeof(Physik), methode) { priority = prio });
        }

        internal static int Lautstaerken, Filter;
        internal static double SummeDb;

        // Lautstaerke direkt aus der Wertung (gilt fuer jede Quelle mit Verdeckung).
        static void LautstaerkeNach(SpatialAudioOccluder __instance)
        {
            if (!An) return;
            try
            {
                float w = __instance.OcclusionEffect;
                float db = Dynamik.Value * w * Lautheit.Value;
                __instance.CurrentParameters.OcclusionVolumeFactor = Mathf.Pow(10f, -db / 20f);
                Lautstaerken++; SummeDb += db;
            }
            catch (Exception e) { Statistik.Fehler(e); }
        }

        // Tiefpass ohne Spiel-Untergrenze; laeuft vor Klangfarbe/Luft (die nur noch weiter absenken).
        static void FilterNach(float occlusion, ref float lowPassFreq, ref float highPassFreq)
        {
            if (!An || occlusion <= 0.001f) return;
            float lp = 22000f * Mathf.Pow(TiefpassMin.Value / 22000f, Mathf.Clamp01(occlusion));
            lowPassFreq = lp;
            if (highPassFreq > lp) highPassFreq = lp;
            Filter++;
        }

        // Der Schall nimmt den leichtesten Weg: kleinste Teil-Wertung statt Mittelwert.
        static void WertungNach(SpatialAudioOcclusionCombinedCalculator __instance, ref float __result)
        {
            if (!An) return;
            var r = __instance.LastAggregateResult;
            float min = 2f;
            if (__instance._ranTransmission) min = Mathf.Min(min, r.transmissionScore);
            if (__instance._ranDiffraction) min = Mathf.Min(min, r.diffractionScore);
            if (__instance._ranReflection) min = Mathf.Min(min, r.reflectionScore);
            if (min <= 1f) __result = Mathf.Clamp01(min);
        }

        internal static void Loggen()
        {
            if (!An || Lautstaerken == 0) return;
            Plugin.Log.LogInfo($"[Diagnostics] Physics mode: {Lautstaerken} volume updates (mean -{SummeDb / Lautstaerken:F1} dB), {Filter} low-pass updates.");
            Lautstaerken = Filter = 0; SummeDb = 0;
        }
    }
}
