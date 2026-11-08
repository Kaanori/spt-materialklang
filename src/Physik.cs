using System;
using System.Runtime.CompilerServices;
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
        internal static ConfigEntry<bool> Aktiv, Koinzidenz;
        internal static ConfigEntry<float> Dynamik, Frequenz, TiefpassMin, Freiweg, Lautheit, VerlustFaktor;
        internal static readonly ConfigEntry<float>[] Dichte = new ConfigEntry<float>[Plugin.AnzahlGruppen];
        internal static readonly ConfigEntry<float>[] Massiv = new ConfigEntry<float>[Plugin.AnzahlGruppen];

        // kg/m^3 (Gitter/Zaun/Gras = offen -> 0). Reihenfolge wie Gruppe.
        // Quelle: uebliche Tabellenwerte; Beton/Stahl vgl. EN 1991-1-1 Anhang A (24 bzw. 77-78,5 kN/m3), Stahl 7850 = EN 1993-1-1,
        // Glas 2500 = EN 572-1. Weich/Unbekannt/Tuer/Kies sind Mischwerte nach Gehoer (siehe README "Physics and sources").
        // Schnee (eigene Gruppe seit 0.6.0): 300 kg/m3 = Mitte zwischen frischem (~100-300) und gepacktem Schnee (~300-500); Tabellenwerte der Schneekunde.
        static readonly float[] StartDichte = { 2300f, 1700f, 1600f, 7850f, 7850f, 600f, 500f, 2500f, 500f, 0f, 1000f, 1000f, 2000f, 600f, 300f };
        // Groesste plausible massive Dicke (m). Dicker gemessen = hohles Objekt (Auto, Container, Haus) -> 2 Waende dieser Staerke.
        // Keine Literaturwerte: Annahmen ueber die Spielgeometrie.
        static readonly float[] StartMassiv = { 1.0f, 3f, 3f, 0.01f, 0.003f, 0.3f, 0.05f, 0.02f, 0.1f, 0f, 0.5f, 3f, 0.5f, 0.06f, 3f };

        // Plattenmodell fuer die Koinzidenz-Korrektur (nur dicke, schwere Gruppen; 0 = keine Korrektur, das Massengesetz bleibt).
        // E-Modul in GPa und Querkontraktionszahl, typische Tabellenwerte: Beton ~25-30 GPa (nu 0.2), Stahl 200 GPa (nu 0.3),
        // Holz (Biegung laengs der Faser) ~9 GPa (nu 0.3). Duenne Gruppen (duenn Holz/Metall, Glas, Tuer) bleiben bewusst ohne
        // Korrektur: ihre Koinzidenzfrequenz liegt bei den Standarddicken oberhalb von 2x Bezugsfrequenz bzw. ihr Modell ist unsicher.
        static readonly float[] EModulGPa = { 25f, 0f, 0f, 200f, 0f, 9f, 0f, 0f, 0f, 0f, 0f, 0f, 0f, 0f, 0f };
        static readonly float[] Querkontraktion = { 0.2f, 0f, 0f, 0.3f, 0f, 0.3f, 0f, 0f, 0f, 0f, 0f, 0f, 0f, 0f, 0f };

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
                new ConfigDescription("Score from which a path counts as occluded (0.02 = about 1 dB). Game: 0.5 = 30 dB. Applies live.", new AcceptableValueRange<float>(0f, 0.5f)));
            Koinzidenz = c.Bind(s, "Coincidence correction", true,
                "Thick heavy walls (concrete/stone, thick metal, thick wood) insulate less than the plain mass law above their coincidence frequency (Sharp / Cremer, see README). Thin groups are never changed. Off = pure mass law.");
            VerlustFaktor = c.Bind(s, "Coincidence loss factor", 0.1f,
                new ConfigDescription("Effective total loss factor of the coincidence correction (internal damping + edges + radiation). Higher = shallower dip. 0.1 is fitted to typical measured curves of 100-200 mm concrete, not a literature value.", new AcceptableValueRange<float>(0.02f, 0.5f)));
            string[] namen = { "Concrete/Stone", "Soil/Terrain", "Gravel/Pebbles", "Metal thick", "Metal thin", "Wood thick", "Wood thin",
                               "Glass", "Soft", "Grate/Fence/Grass", "Body", "Water", "Unknown", "Door leaf", "Snow" };
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
            float schale = Mathf.Min(dicke, mx);               // Dicke einer einzelnen Schale (fuer die Koinzidenzfrequenz)
            if (dicke > mx) dicke = 2f * mx;                 // hohles Objekt: zwei Schalen
            float m = Dichte[g].Value * Mathf.Max(dicke, 0.002f);
            if (m <= 0f) return 0f;
            // Quelle: Massengesetz bei Feldeinfall (diffuser Schalleinfall), TL = 20*log10(m''*f) - 47 dB, m'' in kg/m2.
            // Senkrechter Einfall waere -42 dB; Feldeinfall ~5 dB weniger. Beranek & Ver, "Noise and Vibration Control
            // Engineering" (Wiley 1992/2006); Bies, Hansen & Howard, "Engineering Noise Control" (5. Aufl., CRC 2017).
            // Gilt unterhalb der Koinzidenzfrequenz. Darueber (dicke, schwere Waende) greift die Korrektur nach Bauakustik.cs
            // (Sharp 1978 / Cremer); duenne Gruppen bleiben unveraendert. Deckel = Flankenwege
            // (EN ISO 12354-1:2017), der Wert 60 dB ist eine Annahme.
            double f = Frequenz.Value;
            double r = Bauakustik.Massengesetz(m, f);
            if (Koinzidenz.Value && EModulGPa[g] > 0f)
            {
                double fc = Bauakustik.Koinzidenzfrequenz(Dichte[g].Value, Mathf.Max(schale, 0.002f), EModulGPa[g] * 1e9, Querkontraktion[g]);
                r = Bauakustik.Koinzidenz(r, m, f, fc, VerlustFaktor.Value);
            }
            return Mathf.Clamp((float)r, 0f, Dynamik.Value);
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
            // 0.8.1: nach einem Reset (Pool, Wechsel Hindernis <-> Raumweg) gilt die gemerkte Wertung nicht mehr
            Patch(h, AccessTools.DeclaredMethod(typeof(SpatialAudioOcclusionCombinedCalculator), "OnReset"), nameof(ResetNach), Priority.Normal);
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
                // dB -> Amplitudenfaktor 10^(-dB/20) (Schalldruck). Lautheit 0.2 = nach Gehoer, keine Physik.
                float db = Dynamik.Value * w * Lautheit.Value;
                __instance.CurrentParameters.OcclusionVolumeFactor = Mathf.Pow(10f, -db / 20f);
                Lautstaerken++; SummeDb += db;
            }
            catch (Exception e) { Statistik.Fehler(e); }
        }

        // Tiefpass ohne Spiel-Untergrenze; laeuft vor Klangfarbe/Luft (die nur noch weiter absenken).
        // Begruendung: Massengesetz steigt um 6 dB je Oktave -> Hoehen werden staerker gedaempft. Die Abbildung
        // (logarithmisch 22 kHz -> 800 Hz) ist eine eigene Naeherung, keine Formel aus der Literatur.
        static void FilterNach(float occlusion, ref float lowPassFreq, ref float highPassFreq)
        {
            if (!An || occlusion <= 0.001f) return;
            float lp = 22000f * Mathf.Pow(TiefpassMin.Value / 22000f, Mathf.Clamp01(occlusion));
            lowPassFreq = lp;
            if (highPassFreq > lp) highPassFreq = lp;
            Filter++;
        }

        // Wertung der letzten fertigen Rechnung je Rechner (0.8.1, siehe Stetigkeit.cs).
        sealed class Fertig { public bool Gemerkt; public float Letzte; }
        static readonly ConditionalWeakTable<SpatialAudioOcclusionCombinedCalculator, Fertig> FertigTab =
            new ConditionalWeakTable<SpatialAudioOcclusionCombinedCalculator, Fertig>();
        static readonly ConditionalWeakTable<SpatialAudioOcclusionCombinedCalculator, Fertig>.CreateValueCallback FertigNeu = _ => new Fertig();

        // Der Schall nimmt den leichtesten Weg: kleinste Teil-Wertung statt Mittelwert.
        // Quelle: parallele Wege addieren sich energetisch, der schwaechste dominiert (Flankenuebertragung,
        // EN ISO 12354-1:2017). Minimum statt Energiesumme ist eine Vereinfachung.
        // 0.8.1: Waehrend einer Neuberechnung passen die _ran*-Merker (schon die neue Rechnung) nicht zum Ergebnis (noch das alte);
        // das Minimum ueber diese Mischung sprang zwischen 0 und 1. Jetzt gilt bis zum Abschluss die letzte fertige Wertung.
        static void WertungNach(SpatialAudioOcclusionCombinedCalculator __instance, ref float __result)
        {
            if (!An) return;
            try
            {
                var r = __instance.LastAggregateResult;
                bool t = __instance._ranTransmission, d = __instance._ranDiffraction, rr = __instance._ranReflection;
                var schritt = __instance._combinedStep;
                bool fertig = schritt == SpatialAudioOcclusionCombinedCalculator.ECombinedStep.Complete
                              || schritt == SpatialAudioOcclusionCombinedCalculator.ECombinedStep.Idle;
                var z = FertigTab.GetValue(__instance, FertigNeu);
                float stetig = Stetigkeit.Waehlen(fertig, t, d, rr, r.transmissionScore, r.diffractionScore, r.reflectionScore,
                                                  ref z.Gemerkt, ref z.Letzte, out bool gehalten);
                if (Stetigkeit.An)
                {
                    __result = stetig;
                    Stetigkeit.Gerechnet++;
                    if (gehalten) Stetigkeit.Gehalten++;
                }
                else
                {
                    // 0.8.0 (nur zum Vergleich): Minimum ueber die aktuellen Merker
                    float min = 2f;
                    if (t) min = Mathf.Min(min, r.transmissionScore);
                    if (d) min = Mathf.Min(min, r.diffractionScore);
                    if (rr) min = Mathf.Min(min, r.reflectionScore);
                    if (min <= 1f) __result = Mathf.Clamp01(min);
                }
            }
            catch (Exception e) { Statistik.Fehler(e); }
        }

        static void ResetNach(SpatialAudioOcclusionCombinedCalculator __instance)
        {
            try { if (FertigTab.TryGetValue(__instance, out var z)) z.Gemerkt = false; }
            catch (Exception e) { Statistik.Fehler(e); }
        }

        internal static void Loggen()
        {
            if (Stetigkeit.Gerechnet + Stetigkeit.Geglaettet + Stetigkeit.SofortGelassen > 0)
            {
                Plugin.Log.LogInfo($"[Diagnostics] Steady occlusion: {Stetigkeit.Gehalten} of {Stetigkeit.Gerechnet} path scores held from the last finished calculation " +
                                   $"(0.8.0 would have mixed old and new there), {Stetigkeit.Geglaettet} filter jumps of playing sounds turned into glides, " +
                                   $"{Stetigkeit.SofortGelassen} set at once (new/silent sounds).");
                Stetigkeit.Gehalten = Stetigkeit.Gerechnet = Stetigkeit.Geglaettet = Stetigkeit.SofortGelassen = 0;
            }
            if (!An || Lautstaerken == 0) return;
            Plugin.Log.LogInfo($"[Diagnostics] Physics mode: {Lautstaerken} volume updates (mean -{SummeDb / Lautstaerken:F1} dB), {Filter} low-pass updates.");
            Lautstaerken = Filter = 0; SummeDb = 0;
        }
    }
}
