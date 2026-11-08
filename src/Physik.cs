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
    //    0.8.4 Stufe 1 (Wege.cs, Abschnitt 14): D und R in echten dB, Energiesumme statt Minimum, Tiefpass aus der Bandkurve.
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
            Raumweg.Binden(c);   // 0.8.4: Raumweg aus Geometrie und Portalzustand
        }

        // Daemmung (dB) eines Stuecks Material der Gruppe g mit Dicke d (m).
        internal static float Daemmung(int g, float dicke) => Daemmung(g, dicke, Frequenz.Value, true);

        /// <summary>0.8.4 Stufe 1: Flaechenmasse (kg/m2) wie in Daemmung (hohles Objekt = zwei Schalen).</summary>
        internal static float Masse(int g, float dicke)
        {
            float mx = Massiv[g].Value;
            if (dicke > mx) dicke = 2f * mx;
            return Dichte[g].Value * Mathf.Max(dicke, 0.002f);
        }

        /// <summary>0.8.4 Stufe 1: Daemmung bei beliebiger Frequenz f (Oktavbaender); deckel = auf "Max. insulation" begrenzen.</summary>
        internal static float Daemmung(int g, float dicke, float frequenz, bool deckel)
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
            double f = frequenz;
            double r = Bauakustik.Massengesetz(m, f);
            if (Koinzidenz.Value && EModulGPa[g] > 0f)
            {
                double fc = Bauakustik.Koinzidenzfrequenz(Dichte[g].Value, Mathf.Max(schale, 0.002f), EModulGPa[g] * 1e9, Querkontraktion[g]);
                r = Bauakustik.Koinzidenz(r, m, f, fc, VerlustFaktor.Value);
            }
            return deckel ? Mathf.Clamp((float)r, 0f, Dynamik.Value) : Mathf.Max((float)r, 0f);
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
            Raumweg.Anwenden(h);   // 0.8.4
            Wege.Anwenden(h);      // 0.8.4 Stufe 1
            // 0.8.3: ein Rechner aus dem Pool traegt noch das Ergebnis seiner vorigen Quelle (siehe NeuerOccluderNach)
            try
            {
                int n = 0;
                foreach (var ctor in AccessTools.GetDeclaredConstructors(typeof(CombinedSpatialAudioOccluder)))
                {
                    h.Patch(ctor, postfix: new HarmonyMethod(typeof(Physik), nameof(NeuerOccluderNach)));
                    n++;
                }
                if (n == 0) Plugin.Log.LogWarning("Steady occlusion: CombinedSpatialAudioOccluder constructor not found -> pooled calculators keep the previous sound's result (game behaviour).");
            }
            catch (Exception e) { Statistik.Fehler(e); }
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
                // 0.8.4: physikalischer Raumweg -> Faktor draussen/drinnen der Klanggruppe entfaellt (Raumweg.cs)
                Raumweg.UmweltEntfernen(__instance);
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
        // 0.8.4 (Meldung "klingt roboterhaft", Labs): Das Spiel hebt mit der Verdeckung auch den HOCHPASS an
        // (hpfSettings.frequencyCurve(occlusion), im Labs-Log 70 Hz frei -> 300-460 Hz verdeckt). Zusammen mit dem Tiefpass
        // bei 800 Hz blieb ein Band von gut einer Oktave (Q 1 an beiden Kanten) - Telefon-/Blechdosenklang. Das widerspricht
        // dem Massengesetz, das hier den Tiefpass bestimmt: die Daemmung steigt um 6 dB je Oktave, Tiefen kommen also am
        // BESTEN durch die Wand. Im Physik-Modus gilt deshalb der Hochpass des Spiels bei offenem Weg (Verdeckung 0, nur
        // Richtung/Lage wie im Spiel, ohne den Isolations-Zug, den der Tiefpass hier ebenfalls nicht mehr hat).
        static void FilterNach(Audio.Effects.AudioOcclusionEQPreset preset, float occlusion, DirectionalWeights influencedWeights,
            ref float lowPassFreq, ref float highPassFreq)
        {
            if (!An || occlusion <= 0.001f) return;
            float lp = Abbildung(occlusion);
            // 0.8.4 Stufe 1: Hindernis-Weg mit Bandkurve (T/D/R je Band, Wege.cs) -> 2-poliger Fit statt der freien Kurve
            if (Wege.Tiefpass(Raumweg.AnwendOcc, out float lpBand)) lp = lpBand;
            // 0.8.4 Stufe 1: Raumweg + Wand zwischen Raeumen energetisch summiert -> Fit aus beiden Spektren
            else if (WandZwischenRaeumen.SummenTiefpass(Raumweg.AnwendOcc, out float lpSumme)) lp = lpSumme;
            // 0.8.4: physikalischer Raumweg - Abbildung nur fuer den Tuer-/Fensteranteil, die Beugung an offenen Oeffnungen
            // bringt ihren eigenen Tiefpass mit (Raumweg.cs / RaumwegKern.Tiefpass)
            else if (Raumweg.Tiefpass(out float lpBeugung, out float wTuer))
                lp = Mathf.Min(Abbildung(wTuer), lpBeugung);
            lowPassFreq = lp;
            float offen = highPassFreq;
            try { if (preset != null) offen = HochpassOffen(preset, influencedWeights); }
            catch (Exception e) { Statistik.Fehler(e); }
            float hp = Bauakustik.HochpassPhysik(highPassFreq, offen, lp);
            if (hp < highPassFreq - 0.5f) { HochpassGesenkt++; HochpassSummeHz += highPassFreq - hp; }
            highPassFreq = hp;
            Filter++;
        }

        /// <summary>Alte Abbildung Wertung -> Tiefpass (logarithmisch 22 kHz -> "Low-pass at full insulation").</summary>
        internal static float Abbildung(float w) => 22000f * Mathf.Pow(TiefpassMin.Value / 22000f, Mathf.Clamp01(w));

        internal static int HochpassGesenkt;
        internal static double HochpassSummeHz;

        // Wie FilterCutoffCalculator.Compute, aber mit Verdeckung 0 (time2 = 0) und ohne Isolations-Lerp.
        static float HochpassOffen(Audio.Effects.AudioOcclusionEQPreset preset, DirectionalWeights w)
        {
            var hs = preset.hpfSettings;
            float a = hs.frequencyCurve.Evaluate(0f);
            var t = hs.positionEqThresholds;
            float hp = w.Front * Mathf.Min(a, t.levelFreq) + w.Up * Mathf.Min(a, t.aboveFreq)
                     + w.Down * Mathf.Min(a, t.belowFreq) + w.Back * Mathf.Min(a, t.behindFreq);
            return Mathf.Clamp(hp, 20f, 20000f);
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
        // Bekannte Grenze (Review 0.8.3, Punkt 7, nur dokumentiert): T ist im Physik-Modus eine dB-Skala (Wertung = dB / Dynamik),
        // D (Beugung) und R (Reflexion) sind Heuristiken des Spiels auf eigener Skala 0..1. Das Minimum vergleicht also Werte
        // unterschiedlicher Herkunft; eine Kalibrierkurve fuer D/R gibt es nicht (keine Messdaten).
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
                // 0.8.4 Stufe 1: alle Wege in dB (und als Energiesumme) - Ergebnis der letzten FERTIGEN Rechnung, gehalten bis zur
                // naechsten (Wege.Schnappschuss), damit gilt die Stetigkeit von selbst.
                if (Wege.Wertung(__instance, out float wege))
                {
                    __result = wege;
                    Stetigkeit.Gerechnet++;
                    if (!fertig) Stetigkeit.Gehalten++;
                    return;
                }
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
            try { if (FertigTab.TryGetValue(__instance, out var z)) z.Gemerkt = false; Wege.Vergessen(__instance); }
            catch (Exception e) { Statistik.Fehler(e); }
        }

        internal static int PoolZurueckgesetzt;

        // 0.8.3 (Review-Befund 3): SpatialAudioSystemPoolService.ResetCombinedCalculator ruft beim Zurueckgeben nur
        // FinalizeAndGetAggregateResult, NICHT Reset: LastAggregateResult, _combinedStep und die _ran*-Merker bleiben die der
        // vorigen Quelle. Der naechste Occluder, der den Rechner bekommt, las 1-3 Bilder lang deren Wertung - im Physik-Modus
        // sogar als "fertige" Wertung gemerkt (Stetigkeit). Darum hier, sobald ein Occluder mit seinem Rechner entsteht, der
        // Reset, den das Spiel selbst beim Wechsel Hindernis <-> Raumweg macht (LastAggregateResult = frei, OnReset vergisst die
        // gemerkte Wertung). Bis zur ersten eigenen Rechnung gilt also "frei" wie nach jedem Reset im Spiel.
        static void NeuerOccluderNach(CombinedSpatialAudioOccluder __instance)
        {
            if (!Plugin.Aktiv.Value || !Stetigkeit.An) return;
            try
            {
                if (__instance._obstructionCalculator is SpatialAudioOcclusionCombinedCalculator c && !c.IsCalculating)
                {
                    c.Reset();
                    PoolZurueckgesetzt++;
                }
            }
            catch (Exception e) { Statistik.Fehler(e); }
        }

        internal static void Loggen()
        {
            ReflexionDirektweg.Loggen();   // 0.8.4
            Wege.Loggen();                 // 0.8.4 Stufe 1
            Raumweg.Loggen();              // 0.8.4
            if (Stetigkeit.Gerechnet + Stetigkeit.Geglaettet + Stetigkeit.SofortGelassen > 0)
            {
                Plugin.Log.LogInfo($"[Diagnostics] Steady occlusion: {Stetigkeit.Gehalten} of {Stetigkeit.Gerechnet} path scores held from the last finished calculation " +
                                   $"(0.8.0 would have mixed old and new there), {Stetigkeit.Geglaettet} filter jumps of playing sounds turned into glides, " +
                                   $"{Stetigkeit.SofortGelassen} set at once (new/silent sounds), {PoolZurueckgesetzt} pooled calculators reset for a new sound.");
                Stetigkeit.Gehalten = Stetigkeit.Gerechnet = Stetigkeit.Geglaettet = Stetigkeit.SofortGelassen = PoolZurueckgesetzt = 0;
            }
            if (!An || Lautstaerken == 0) return;
            Plugin.Log.LogInfo($"[Diagnostics] Physics mode: {Lautstaerken} volume updates (mean -{SummeDb / Lautstaerken:F1} dB), {Filter} low-pass updates, " +
                               $"{HochpassGesenkt} high-pass values lowered to the open-path value (mean -{(HochpassGesenkt > 0 ? HochpassSummeHz / HochpassGesenkt : 0):F0} Hz).");
            Lautstaerken = Filter = HochpassGesenkt = 0; SummeDb = 0; HochpassSummeHz = 0;
        }
    }
}
