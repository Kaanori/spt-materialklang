using System;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;
using Audio.SpatialSystem;
using Audio.SpatialSystem.SpatialAudioCalculator;
using BepInEx.Configuration;
using HarmonyLib;
using Unity.Mathematics;
using UnityEngine;

namespace Materialklang
{
    // ------------------------------------------------------------------------------------------
    // 0.8.4 - Ausbaustufe 1 der Machbarkeitsstudie "True Acoustics" (Rechnung: WegeKern.cs). Spiel-Teil, nur im Physik-Modus.
    //  1. D und R in echten dB (UTD-Kantenbeugung aus Valve Steam Audio am Kantenpunkt des Spiels, Spiegelquelle aus Weglaenge + Absorption der
    //     getroffenen Flaechen), je Band 250 Hz (Bezug) / 1 kHz / 4 kHz.
    //  2. T je Band (Massegesetz + Koinzidenz bei jeder Frequenz, TransmissionPatch) und EIN Tiefpass aus der Bandkurve
    //     (2-poliger Fit, RaumwegKern/WegeKern) statt der freien Kurve "22 kHz -> 800 Hz ueber die Wertung". Hochpass wie seit 0.8.4.
    //  3. Energiesumme aller Wege statt Minimum (T/D/R, und Raumweg + Wand zwischen Raeumen in WandZwischenRaeumen). Die Spiel-
    //     Schwelle fuer R (T > 0,55 und D > 0,5) wird im Physik-Modus gesenkt, damit R nicht ploetzlich dazukommt.
    //  4. Empfangsraum (EN ISO 12354-1: L2 = L1 - R + 10 lg(S/A)) und 5. Flankenwege (EN ISO 12354-1 4.4, Anhang E) fuer den Weg
    //     durch eine Wand, mit den Raumdaten aus HallZonen.
    // Jeder Teil hat einen Schalter (Abschnitt 14), aus = Verhalten davor.
    // ------------------------------------------------------------------------------------------
    internal static class Wege
    {
        internal const string Sek = "14. Sound Paths (physics)";
        internal static ConfigEntry<bool> DbAn, BaenderAn, SummeAn, RImmer, RaumAn, FlankeAn;
        internal static ConfigEntry<float> RMaxDist;
        internal static ConfigEntry<float> RVerlust, RaumDiffus, RaumMax, RaumMaxFlaeche, BandStaerke, Keil;

        internal static bool An => Physik.An && DbAn != null && DbAn.Value;
        internal static bool Baender => Physik.An && BaenderAn != null && BaenderAn.Value;
        internal static bool Summe => An && SummeAn.Value;
        internal static bool RImmerAn => An && RImmer.Value;
        internal static bool RaumKorrektur => Physik.An && RaumAn != null && RaumAn.Value;
        internal static bool FlankeKorrektur => Physik.An && FlankeAn != null && FlankeAn.Value;

        internal static readonly double[] F = { 250, 1000, 4000 };
        internal static double[] Frequenzen() { F[0] = Physik.Frequenz != null ? Physik.Frequenz.Value : 250; return F; }

        internal static void Binden(ConfigFile c)
        {
            DbAn = c.Bind(Sek, "Diffraction and reflection in dB", true,
                "Physics mode: the path around an edge (diffraction) and the path over a reflecting wall get real decibels - UTD for the edge (from Valve's Steam Audio), " +
                "image source with the absorption of the hit surfaces for the reflection - per band 250 Hz / 1 kHz / 4 kHz. " +
                "Off = the game's 0..1 values (path length / range), as in 0.8.3. Applies live.");
            Keil = c.Bind(Sek, "Edge diffraction: wedge angle (deg)", 90f,
                new ConfigDescription("Edge diffraction uses the UTD implementation from Valve's Steam Audio (Apache 2.0). The game only knows the edge point, not the " +
                    "two faces, so the wedge is assumed: 90 = house corner (default), 0 = thin edge (wall top, door frame; Steam Audio's value).", new AcceptableValueRange<float>(0f, 150f)));
            BaenderAn = c.Bind(Sek, "Octave bands for the low-pass", true,
                "Physics mode: wall insulation is calculated at 250 Hz, 1 kHz and 4 kHz (mass law + coincidence) and the low-pass is fitted to that curve " +
                "(the game has one 2-pole low-pass per sound). Off = the old curve 22 kHz -> 800 Hz over the insulation. Applies live.");
            BandStaerke = c.Bind(Sek, "Band low-pass strength", 1.0f,
                new ConfigDescription("Multiplies the extra loss of the high bands before the low-pass fit. 1 = physical; lower = brighter. Tuning by ear.",
                    new AcceptableValueRange<float>(0.2f, 1.5f)));
            SummeAn = c.Bind(Sek, "Energy sum of all paths", true,
                "Physics mode: through the wall, around the edge and over a reflection - all paths add up (energy), as in reality, instead of only the " +
                "loudest one counting. Removes level jumps where the loudest path changes. Also sums the door/window route and the wall between rooms. " +
                "Off = the loudest path wins (minimum). Applies live.");
            RImmer = c.Bind(Sek, "Reflections for every occluded sound", true,
                "The game only looks for a reflection when the wall AND the edge path are both very bad (T > 0.55 and D > 0.5); with the energy sum the " +
                "reflection would then suddenly add up. On = look for it for every occluded sound (costs extra reflection rays, see the diagnostic line). Applies live.");
            RMaxDist = c.Bind(Sek, "Reflections for every occluded sound: max. distance (m)", 50f,
                new ConfigDescription("Cost limit: farther sounds keep the game's rule. The diagnostic line shows the extra casts.", new AcceptableValueRange<float>(0f, 250f)));
            RVerlust = c.Bind(Sek, "Reflection: extra loss per bounce (dB)", 3f,
                new ConfigDescription("On top of the material absorption. The game's last leg to you is a visibility test from the hit point, not a mirror path; " +
                    "finite, scattering surfaces send less than an infinite mirror (assumption, see README).", new AcceptableValueRange<float>(0f, 10f)));
            RaumAn = c.Bind(Sek, "Receiving room", true,
                "Physics mode: sound through a wall fills the room you are in (EN ISO 12354-1: L2 = L1 - R + 10 lg(S/A)) - a small hard room behind the wall " +
                "gets a bit louder, a large or soft one quieter. Uses the rooms measured by section 9 (Reverb by Material). Applies live.");
            RaumDiffus = c.Bind(Sek, "Receiving room: diffuse share", 0.5f,
                new ConfigDescription("Weight of the room's diffuse field (4P/A). Meta XR may already add reverb in your room; 1 = full ISO value.", new AcceptableValueRange<float>(0f, 1f)));
            RaumMax = c.Bind(Sek, "Receiving room: max. correction (dB)", 6f,
                new ConfigDescription("Limits the room correction both ways.", new AcceptableValueRange<float>(0f, 12f)));
            RaumMaxFlaeche = c.Bind(Sek, "Receiving room: max. wall area (m2)", 20f,
                new ConfigDescription("Largest wall area that radiates into your room (the room box side towards the sound, capped).", new AcceptableValueRange<float>(2f, 100f)));
            FlankeAn = c.Bind(Sek, "Flanking transmission", true,
                "Physics mode: sound also travels around a wall through the floor, ceiling and side walls (EN ISO 12354-1, junction values of a rigid cross junction). " +
                "Replaces the fixed 60 dB cap where the room is known: heavy walls between light floors let less through than the mass law says. Applies live.");
            SettingsLive(DbAn); SettingsLive(SummeAn); SettingsLive(RImmer);
        }

        static void SettingsLive(ConfigEntry<bool> e) { e.SettingChanged += (_, __) => { try { FreiwegPatch.AlleNeu(); } catch { } }; }

        internal static void Anwenden(Harmony h)
        {
            try
            {
                var t = typeof(SpatialAudioOcclusionCombinedCalculator);
                var spaet = AccessTools.DeclaredMethod(t, "OnLateUpdate");
                var start = AccessTools.DeclaredMethod(t, "StartCalculation", new[] { typeof(Vector3), typeof(Vector3), typeof(float), typeof(float) });
                var rStart = AccessTools.DeclaredMethod(typeof(ReflectionCalculator), "StartCalculation");
                var rSchritt = AccessTools.DeclaredMethod(typeof(ReflectionCalculator), "UpdateCalculationStep");
                if (spaet == null || start == null || rStart == null || rSchritt == null)
                {
                    Plugin.Log.LogWarning("Sound paths (14.): targets not found (game update?) -> diffraction/reflection keep the game values.");
                    return;
                }
                h.Patch(spaet, prefix: new HarmonyMethod(typeof(Wege), nameof(SpaetVor)), postfix: new HarmonyMethod(typeof(Wege), nameof(SpaetNach)));
                h.Patch(start, postfix: new HarmonyMethod(typeof(Wege), nameof(StartNach)));
                h.Patch(rStart, postfix: new HarmonyMethod(typeof(Wege), nameof(RStartNach)));
                h.Patch(rSchritt, prefix: new HarmonyMethod(typeof(Wege), nameof(RSchrittVor)) { priority = Priority.Low });
                Bereit = true;
            }
            catch (Exception e) { Statistik.Fehler(e); }
        }

        internal static bool Bereit;

        // ================================================================ T je Band (aus TransmissionPatch)

        internal sealed class TBand
        {
            public bool Gueltig;
            public readonly float[] Db = new float[WegeKern.N];   // Daemmung je Band (dB, >= 0), Energie-Mittel ueber alle Strahlen
            public readonly float[] DbWand = new float[WegeKern.N]; // nur Energie durch Waende (freie Strahlen = 0); 1000 = keine
            public float Frei;                                     // Anteil freier Strahlen
            public float RaumDb = float.NaN, FlankeDb = float.NaN;  // Korrektur im Bezugsband (Log)
            public string Raum;
        }
        static readonly ConditionalWeakTable<TransmissionCalculator, TBand> TTab = new ConditionalWeakTable<TransmissionCalculator, TBand>();
        internal static TBand THole(TransmissionCalculator c) => TTab.GetValue(c, _ => new TBand());
        internal static bool TFinde(TransmissionCalculator c, out TBand t) { t = null; return c != null && TTab.TryGetValue(c, out t) && t.Gueltig; }

        // ---- Kontext einer Durchdringungsrechnung (Hauptfaden, eine Rechnung zur Zeit) ----
        static bool kRaum, kFlanke;
        static HallZonen.RaumInfo kInfo;
        static double kD, kS;
        static readonly double[] kA2 = new double[WegeKern.N];
        static readonly double[] kRF = new double[2 * WegeKern.N], kMF = new double[2];
        static readonly double[] fR = new double[4], fM = new double[4], fL = new double[4];
        // Merker der letzten Korrektur (Strahlen treffen fast immer dieselbe Wand)
        static int mG = -1; static float mTh, mR1, mR2; static readonly float[] mAus = new float[WegeKern.N]; static float mRaum0, mFlanke0;
        internal static float LetzteRaumDb, LetzteFlankeDb;

        /// <summary>Vor den Treffern einer Rechnung: Raum des Hoerers (Empfangsraum, Flanken) bestimmen.</summary>
        internal static void TBeginn(Vector3 hoerer, Vector3 quelle)
        {
            Frequenzen();
            mG = -1; LetzteRaumDb = LetzteFlankeDb = float.NaN;
            kD = Mathf.Max(Vector3.Distance(hoerer, quelle), 0.1f);
            kRaum = kFlanke = false;
            if (!RaumKorrektur && !FlankeKorrektur) return;
            if (!HallZonen.RaumFuer(hoerer, quelle, (float)F[0], out kInfo) || kInfo.QuelleDrin)
            {
                // Hoerer draussen (keine Box), Quelle in einem gemessenen Raum: nur die Direktabstrahlung der Wand (Halbraum),
                // Wandflaeche = Seite des Quellraums Richtung Hoerer. Keine Flanken (kein Empfangsraum).
                if (RaumKorrektur && kInfo.Name == null && HallZonen.RaumFuer(quelle, hoerer, (float)F[0], out var qi) && !qi.QuelleDrin)
                {
                    kInfo = qi; kInfo.Name = qi.Name + " (source room, you outside)";
                    kS = Math.Min(qi.WandFlaeche, RaumMaxFlaeche.Value);
                    kA2[0] = kA2[1] = kA2[2] = -1;
                    kRaum = true;
                }
                return;
            }
            kS = Math.Min(kInfo.WandFlaeche, RaumMaxFlaeche.Value);
            kA2[0] = kInfo.Oberflaeche * kInfo.Alpha0; kA2[1] = kInfo.Oberflaeche * kInfo.Alpha1; kA2[2] = kInfo.Oberflaeche * kInfo.Alpha2;
            kRaum = RaumKorrektur;
            if (FlankeKorrektur)
            {
                // Boden und Decke: Gruppe aus der Raummessung, Dicke wie eine Einzelflaeche (Schale bzw. Gruppendicke)
                float schale = WandZwischenRaeumen.Schale != null ? WandZwischenRaeumen.Schale.Value : 0.15f;
                kFlanke = true;
                for (int j = 0; j < 2; j++)
                {
                    int g = j == 0 ? kInfo.GruppeUnten : kInfo.GruppeOben;
                    if (g < 0) g = (int)Gruppe.ConcreteStone;
                    float th = Bauakustik.EinzelDicke(g, schale);
                    kMF[j] = Physik.Masse(g, th);
                    for (int b = 0; b < WegeKern.N; b++) kRF[j * WegeKern.N + b] = Physik.Daemmung(g, th, (float)F[b], false);
                    if (kMF[j] <= 0.5) kFlanke = false;    // offener Boden (Gitter) -> keine Aussage
                }
            }
        }

        /// <summary>Ein Wandstueck (Gruppe g, Dicke th, Abstand Quelle r1 / Hoerer r2): Daemmung je Band mit Flanken und Empfangsraum.
        /// db = Massegesetz je Band (Eingang, ungedeckelt), Ausgang gedeckelt bei der Hoechstdaemmung.</summary>
        internal static void TStueck(int g, float th, float r1, float r2, float[] db)
        {
            if (!kRaum && !kFlanke) { float dyn = Physik.Dynamik.Value; for (int b = 0; b < WegeKern.N; b++) db[b] = Mathf.Clamp(db[b], 0f, dyn); return; }
            if (g == mG && Mathf.Abs(th - mTh) < 0.01f && Mathf.Abs(r1 - mR1) < 0.25f && Mathf.Abs(r2 - mR2) < 0.25f)
            { Array.Copy(mAus, db, WegeKern.N); LetzteRaumDb = mRaum0; LetzteFlankeDb = mFlanke0; return; }
            Korrigieren(Physik.Masse(g, th), r1, r2, db);
            mG = g; mTh = th; mR1 = r1; mR2 = r2; Array.Copy(db, mAus, WegeKern.N); mRaum0 = LetzteRaumDb; mFlanke0 = LetzteFlankeDb;
        }

        /// <summary>Wie TStueck, fuer eine ganze Wand (mehrere Schichten, Flaechenmasse mD gesamt) - Wand zwischen Raeumen.
        /// Vorher TBeginn mit Hoerer/Quelle rufen.</summary>
        internal static void Korrigieren(double mD, float r1, float r2, float[] db)
        {
            float dyn = Physik.Dynamik.Value;
            LetzteRaumDb = LetzteFlankeDb = float.NaN;
            if (!kRaum && !kFlanke) { for (int b = 0; b < WegeKern.N; b++) db[b] = Mathf.Clamp(db[b], 0f, dyn); return; }
            float raum0 = float.NaN, flanke0 = float.NaN;
            for (int b = 0; b < WegeKern.N; b++)
            {
                double r = db[b];
                if (kFlanke && r > 1.0 && mD > 0.5)
                {
                    // Boden, Decke (Stossstellenlaenge = Wandbreite), zwei Seitenwaende wie die Trennwand (Laenge = Hoehe)
                    fR[0] = kRF[b]; fM[0] = kMF[0]; fL[0] = kInfo.WandBreite;
                    fR[1] = kRF[WegeKern.N + b]; fM[1] = kMF[1]; fL[1] = kInfo.WandBreite;
                    fR[2] = fR[3] = r; fM[2] = fM[3] = mD; fL[2] = fL[3] = kInfo.WandHoehe;
                    double rs = WegeKern.Flanke(r, mD, kS, fR, fM, fL, 4);
                    if (b == 0) flanke0 = (float)(rs - r);
                    r = rs;
                }
                if (kRaum && r > 0.5)
                {
                    double a2 = kA2[b];
                    double k = WegeKern.Empfangsraum(kS, r1, r2, kD, a2, RaumDiffus.Value, RaumMax.Value);
                    k *= Math.Min(1.0, r / 10.0);      // stetig: duenne/offene Teile (R -> 0) bekommen keine Raumkorrektur
                    if (b == 0) raum0 = (float)k;
                    r -= k;
                }
                db[b] = Mathf.Clamp((float)r, 0f, dyn);
            }
            LetzteRaumDb = raum0; LetzteFlankeDb = flanke0;
        }

        internal static string TRaumName => kRaum || kFlanke ? kInfo.Name : null;

        // ================================================================ R: getroffene Flaechen je Strahl mitschreiben

        sealed class RSpur
        {
            public int[] Anzahl = new int[0], Ids = new int[0];
            public int2[] Karte = new int2[0];
            public bool Aktiv;
            public int Stufen, Befehle;
        }
        static readonly ConditionalWeakTable<ReflectionCalculator, RSpur> RTab = new ConditionalWeakTable<ReflectionCalculator, RSpur>();
        const int MaxRefl = 4;

        // Postfix ReflectionCalculator.StartCalculation: Befehle der Stufe 1 = [Direktstrahl], dann ein Strahl je Startrichtung.
        static void RStartNach(ReflectionCalculator __instance)
        {
            if (!An) return;
            try
            {
                var c = __instance;
                var s = RTab.GetValue(c, _ => new RSpur());
                s.Aktiv = false;
                if (!c._isCalculationInProgress) return;
                int n = c._initialRayCount, cur = c._currentInitialRayCount;
                if (s.Anzahl.Length != n) { s.Anzahl = new int[n]; s.Ids = new int[n * MaxRefl]; s.Karte = new int2[n]; }
                int off = c._directPathCommandIndex >= 0 ? 1 : 0;
                for (int i = 0; i < n; i++)
                {
                    s.Anzahl[i] = 0;
                    s.Karte[i] = new int2(i < cur ? i + off : -1, -1);
                }
                s.Aktiv = true; s.Stufen = 1; s.Befehle = cur + off;
            }
            catch (Exception e) { Statistik.Fehler(e); }
        }

        // Prefix ReflectionCalculator.UpdateCalculationStep: genau wenn das Spiel gleich die naechste Stufe plant, liegen in
        // _spherecastHits die Treffer der fertigen Stufe. Treffer x eines Strahls, dessen reflectionCount gestiegen ist = Reflexion.
        static void RSchrittVor(ReflectionCalculator __instance)
        {
            try
            {
                var c = __instance;
                if (c.bool_1 || !c.bool_2 || !c._isCalculationInProgress || !c._lastJobHandle.IsCompleted) return;
                if (c._currentReflectionLevel >= c._maxReflections) return;
                if (!RTab.TryGetValue(c, out var s) || !s.Aktiv) return;
                c._lastJobHandle.Complete();
                var hits = c._spherecastHits; var zust = c._rayStates; var karte = c._commandIndicesMap;
                if (!hits.IsCreated || !zust.IsCreated || !karte.IsCreated) { s.Aktiv = false; return; }
                int n = Math.Min(s.Anzahl.Length, Math.Min(zust.Length, karte.Length));
                for (int i = 0; i < n; i++)
                {
                    int x = s.Karte[i].x;
                    if (x < 0 || x >= hits.Length) continue;
                    int rc = zust[i].reflectionCount;
                    if (rc > s.Anzahl[i] && s.Anzahl[i] < MaxRefl)
                    {
                        s.Ids[i * MaxRefl + s.Anzahl[i]] = hits[x].colliderInstanceID;
                        s.Anzahl[i] = rc;
                    }
                }
                for (int i = 0; i < n; i++) s.Karte[i] = karte[i];       // Befehle der naechsten Stufe
                s.Stufen++; s.Befehle += c._maxCommandsPerBatch;
            }
            catch (Exception e) { Statistik.Fehler(e); }
        }

        // ================================================================ Ergebnis je Combined-Rechner

        sealed class Info
        {
            public readonly WegZustand Z = new WegZustand();
            public SpatialAudioOcclusionCombinedCalculator.ECombinedStep Vor;
            public bool RExtra;              // R lief nur wegen der gesenkten Schwelle
            public string RMat;              // Materialien des R-Wegs (Log)
            public float DEnergie;           // Winkelfaktor des Spiels (nur Log)
        }
        static readonly ConditionalWeakTable<SpatialAudioOcclusionCombinedCalculator, Info> Tab =
            new ConditionalWeakTable<SpatialAudioOcclusionCombinedCalculator, Info>();
        static readonly ConditionalWeakTable<SpatialAudioOcclusionCombinedCalculator, Info>.CreateValueCallback Neu = _ => new Info();

        // Diagnose
        static int dSchnapp, dFrei, dBestT, dBestD, dBestR, dDGehalten, dRGehalten, dRHyst, dRLauf, dRExtra, dTStrahlen, dDStrahlen, dRBefehle, dRBefehleExtra;
        static double dSumMin, dSumSumme; static int dSumN;
        internal static int TStrahlenZaehlen { set { dTStrahlen += value; } }

        static void SpaetVor(SpatialAudioOcclusionCombinedCalculator __instance)
        {
            if (!An) return;
            try
            {
                var c = __instance;
                Tab.GetValue(c, Neu).Vor = c._combinedStep;
                if (c._combinedStep == SpatialAudioOcclusionCombinedCalculator.ECombinedStep.TDScheduled)
                {
                    // Kostenbremse: gesenkte R-Schwelle nur bis "max. distance" (die Schwelle wird genau hier vom Spiel gelesen)
                    FreiwegPatch.Originale(c, out float ot, out float od);
                    float max = RMaxDist.Value;
                    bool extra = RImmer.Value && (c._sourcePosition - c._listenerPosition).sqrMagnitude <= max * max;
                    c._transmissionThreshold = extra ? c._minClearPathScore : ot;
                    c._diffractionThreshold = extra ? 0f : od;
                }
            }
            catch (Exception e) { Statistik.Fehler(e); }
        }

        static void SpaetNach(SpatialAudioOcclusionCombinedCalculator __instance)
        {
            if (!An) return;
            try
            {
                if (!Tab.TryGetValue(__instance, out var inf)) return;
                var s = __instance._combinedStep;
                if (inf.Vor == SpatialAudioOcclusionCombinedCalculator.ECombinedStep.TDScheduled && s == SpatialAudioOcclusionCombinedCalculator.ECombinedStep.RScheduled)
                {
                    // haette das Spiel mit seinen Schwellen R gestartet?
                    float t = __instance._lastTScore, d = __instance._lastDScore;
                    FreiwegPatch.Originale(__instance, out float ot, out float od);
                    inf.RExtra = !(t > ot && d > od);
                }
                else if (inf.Vor != SpatialAudioOcclusionCombinedCalculator.ECombinedStep.RScheduled) inf.RExtra = false;
                if (s == SpatialAudioOcclusionCombinedCalculator.ECombinedStep.Complete && inf.Vor != SpatialAudioOcclusionCombinedCalculator.ECombinedStep.Complete)
                    Schnappschuss(__instance, inf);
            }
            catch (Exception e) { Statistik.Fehler(e); }
        }

        // StartCalculation kann sofort fertig sein (Quelle ausser Reichweite -> frei)
        static void StartNach(SpatialAudioOcclusionCombinedCalculator __instance)
        {
            if (!An) return;
            try
            {
                if (__instance._combinedStep != SpatialAudioOcclusionCombinedCalculator.ECombinedStep.Complete) return;
                var inf = Tab.GetValue(__instance, Neu);
                Schnappschuss(__instance, inf);
            }
            catch (Exception e) { Statistik.Fehler(e); }
        }

        /// <summary>Aus Physik.ResetNach: Rechner wurde zurueckgesetzt (Pool, Wechsel Hindernis/Raumweg).</summary>
        internal static void Vergessen(SpatialAudioOcclusionCombinedCalculator c)
        {
            if (Tab.TryGetValue(c, out var inf)) { inf.Z.Vergessen(); inf.RMat = null; }
        }

        static readonly double[] bT = new double[WegeKern.N], bD = new double[WegeKern.N], bR = new double[WegeKern.N];
        static readonly double[] alphaB = new double[MaxRefl];
        static readonly int[] rIds = new int[MaxRefl];

        static void Schnappschuss(SpatialAudioOcclusionCombinedCalculator c, Info inf)
        {
            Frequenzen();
            float dyn = Physik.Dynamik.Value;
            var res = c.LastAggregateResult;
            V3 q = V(c._sourcePosition), h = V(c._listenerPosition);
            double d = Math.Max(V3.Abstand(q, h), 0.1);
            var z = inf.Z;
            z.Direkt = d;
            bool frei = !c._ranTransmission || (!c._ranDiffraction && !c._ranReflection && res.transmissionScore <= c._minClearPathScore);
            dSchnapp++;
            if (frei)
            {
                // Direkttest des Spiels unter der Freiweg-Schwelle: dessen kleiner T-Wert gilt (stetig an der Schwelle)
                double[] freiT = null;
                if (c._ranTransmission && TFinde(c._transmissionCalculator, out var tf) && Math.Abs(tf.Db[0] - c._lastTScore * dyn) < 0.5)
                {
                    for (int b = 0; b < WegeKern.N; b++) bT[b] = -tf.Db[b];
                    freiT = bT;
                }
                WegeKern.Auswerten(z, true, false, null, 0, false, false, false, null, false, false, false, null, q, h, dyn, Summe, freiT: freiT);
                dFrei++;
                return;
            }

            // ---- T ----
            double tDb = res.transmissionScore * dyn, tFrei = 0;
            if (TFinde(c._transmissionCalculator, out var tb) && Math.Abs(tDb - tb.Db[0]) < 0.5)
            {
                tFrei = tb.Frei;
                for (int b = 0; b < WegeKern.N; b++) bT[b] = tb.DbWand[b] >= 999f ? WegeKern.KeinWeg : -tb.DbWand[b];
            }
            else for (int b = 0; b < WegeKern.N; b++) bT[b] = -tDb;   // ohne Bandmessung: Spielwertung, flach

            // ---- D ----
            bool dDirekt = false, dHat = false;
            if (c._ranDiffraction && c._diffractionCalculator is EdgeDiffractionCalculator dc && dc._buffersAllocated && dc._resultBuffer.IsCreated && dc._resultBuffer.Length > 0)
            {
                var dr = dc._resultBuffer[0];
                if (dr.isDirectPath) dDirekt = true;
                else if (dr.FoundPath)
                {
                    dHat = true;
                    z.LD = dr.pathLength; inf.DEnergie = dr.energyFactor;
                    V3 kante = V(dr.diffractionPoint);
                    double n = WegeKern.KeilN(Keil.Value);
                    for (int b = 0; b < WegeKern.N; b++) bD[b] = WegeKern.Beugung(q, kante, h, F[b], n);
                }
                dDStrahlen += 2 + 4 * dc._dynamicEdgeSearchRayCount;
            }

            // ---- R ----
            bool rDirekt = false, rHat = false;
            if (c._ranReflection && c._reflectionCalculator is ReflectionCalculator rc)
            {
                dRLauf++;
                if (RTab.TryGetValue(rc, out var spur)) { dRBefehle += spur.Befehle; if (inf.RExtra) { dRBefehleExtra += spur.Befehle; dRExtra++; } }
                if (rc.IsDirectPathClear) rDirekt = true;
                else
                {
                    var best = rc._currentBestOverallData;
                    if (best.found && best.pathLength < float.MaxValue)
                    {
                        rHat = true;
                        int n = 1;
                        int ray = best.rayIndex;
                        if (spur != null && spur.Aktiv && ray >= 0 && ray < spur.Anzahl.Length) n = Math.Max(1, Math.Min(MaxRefl, spur.Anzahl[ray]));
                        else if (rc._rayStates.IsCreated && ray >= 0 && ray < rc._rayStates.Length) n = Math.Max(1, Math.Min(MaxRefl, rc._rayStates[ray].reflectionCount));
                        for (int k = 0; k < n; k++) rIds[k] = spur != null && spur.Aktiv && ray >= 0 && ray < spur.Anzahl.Length ? spur.Ids[ray * MaxRefl + k] : 0;
                        for (int b = 0; b < WegeKern.N; b++)
                        {
                            for (int k = 0; k < n; k++) alphaB[k] = rIds[k] != 0 ? HallZonen.AlphaCollider(rIds[k], (float)F[b]) : 0.1;
                            bR[b] = WegeKern.Reflexion(d, best.pathLength, alphaB, n, RVerlust.Value);
                        }
                        z.LR = best.pathLength; z.RAnzahl = n;
                        inf.RMat = KlangLog.An ? RMaterialien(n) : null;
                    }
                }
            }

            WegeKern.Auswerten(z, false, c._ranTransmission, bT, tFrei, c._ranDiffraction, dDirekt, dHat, bD, c._ranReflection, rDirekt, rHat, bR, q, h, dyn, Summe);
            if (z.Bester == 1) dBestT++; else if (z.Bester == 2) dBestD++; else if (z.Bester == 3) dBestR++;
            if (z.DGehalten) dDGehalten++;
            if (z.RGehalten) dRGehalten++;
            if (z.RHysterese) dRHyst++;
            // Vergleich Minimum (lautester Weg) gegen Summe im Bezugsband
            double minDb = z.Bester == 1 ? z.T[0] : z.Bester == 2 ? z.D[0] : z.Bester == 3 ? z.R[0] : -dyn;
            dSumMin += -minDb; dSumSumme += -z.Ergebnis[0]; dSumN++;
        }

        static string RMaterialien(int n)
        {
            var sb = new StringBuilder(32);
            for (int k = 0; k < n; k++)
            {
                if (k > 0) sb.Append('/');
                sb.Append(rIds[k] != 0 ? ((Gruppe)MaterialCache.Get(rIds[k])).ToString() : "?");
            }
            return sb.ToString();
        }

        /// <summary>Aus Physik.WertungNach: Wertung der letzten fertigen Rechnung (gehalten bis zur naechsten).</summary>
        internal static bool Wertung(SpatialAudioOcclusionCombinedCalculator c, out float w)
        {
            w = 0f;
            if (!An || !Tab.TryGetValue(c, out var inf) || !inf.Z.Gueltig) return false;
            w = (float)inf.Z.Wertung;
            return true;
        }

        // ================================================================ Filter

        /// <summary>Tiefpass aus der Bandkurve des Hindernis-Wegs der Quelle, die gerade angewendet wird (Combined: Wege-Ergebnis,
        /// MultiSource: T-Baender). false = keine Bandkurve -> alte Abbildung.</summary>
        internal static bool Tiefpass(SpatialAudioOccluder occ, out float lp)
        {
            lp = 22000f;
            if (!Baender || occ == null || occ.IsPropagationActive) return false;
            double m1, m2;
            if (occ is CombinedSpatialAudioOccluder comb && comb._obstructionCalculator is SpatialAudioOcclusionCombinedCalculator c)
            {
                if (An && Tab.TryGetValue(c, out var inf) && inf.Z.Gueltig)
                {
                    m1 = inf.Z.Ergebnis[0] - inf.Z.Ergebnis[1]; m2 = inf.Z.Ergebnis[0] - inf.Z.Ergebnis[2];
                }
                else if (TFinde(c._transmissionCalculator, out var tb) && TBestimmt(c)) { m1 = tb.Db[1] - tb.Db[0]; m2 = tb.Db[2] - tb.Db[0]; }
                else return false;
            }
            else if (occ is MultiSourceSpatialAudioOccluder && TFinde(Kontext.MultiCalc, out var tm)) { m1 = tm.Db[1] - tm.Db[0]; m2 = tm.Db[2] - tm.Db[0]; }
            else return false;
            lp = FitLp(m1, m2);
            BandFilter++;
            return true;
        }

        // Ohne "D/R in dB" (Minimum der Spielwertungen): Bandkurve von T nur, wenn T der leichteste Weg ist.
        static bool TBestimmt(SpatialAudioOcclusionCombinedCalculator c)
        {
            var r = c.LastAggregateResult;
            if (!c._ranTransmission) return false;
            return (!c._ranDiffraction || r.transmissionScore <= r.diffractionScore) && (!c._ranReflection || r.transmissionScore <= r.reflectionScore);
        }

        internal static float FitLp(double m1, double m2)
        {
            double k = BandStaerke.Value;
            return (float)WegeKern.TiefpassFit(m1 * k, m2 * k, F[1], F[2], Physik.TiefpassMin.Value);
        }

        internal static int BandFilter, SummeRaumweg, BandFilterRaumweg;

        // ================================================================ Klang-Log / Diagnose

        static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

        /// <summary>Klang-Log: Beitrag jedes Wegs in dB (Bezug/1k/4k) und die Summe (nur Hindernis-Weg, Physik-Modus).</summary>
        internal static string LogText(SpatialAudioOccluder occ)
        {
            if (!An || !(occ is CombinedSpatialAudioOccluder comb) || !(comb._obstructionCalculator is SpatialAudioOcclusionCombinedCalculator c)) return null;
            if (!Tab.TryGetValue(c, out var inf) || !inf.Z.Gueltig) return null;
            var z = inf.Z;
            var sb = new StringBuilder(200);
            sb.Append(" | paths dB@").Append(F[0].ToString("F0", Inv)).Append("/1k/4k:");
            if (z.Frei) { sb.Append(" clear (game), direct test T "); B(sb, z.T); return sb.ToString(); }
            if (z.HatT) { sb.Append(" T "); B(sb, z.T); sb.Append(" (free rays ").Append((z.TFrei * 100).ToString("F0", Inv)).Append(" %)"); if (TFinde(c._transmissionCalculator, out var tb) && (!float.IsNaN(tb.RaumDb) || !float.IsNaN(tb.FlankeDb))) { sb.Append(" ("); if (!float.IsNaN(tb.FlankeDb)) { sb.Append("flank "); V(sb, tb.FlankeDb); sb.Append(' '); } if (!float.IsNaN(tb.RaumDb)) { sb.Append("room "); V(sb, tb.RaumDb); } if (tb.Raum != null) sb.Append(" \"").Append(tb.Raum).Append('"'); sb.Append(')'); } }
            if (z.HatD)
            {
                sb.Append(" D ");
                if (z.DDirekt) sb.Append("line of sight 0");
                else { B(sb, z.D); sb.Append(" (L ").Append(z.LD.ToString("F1", Inv)).Append(" m, detour ").Append((z.LD - z.Direkt).ToString("F2", Inv)).Append(" m"); if (z.DGehalten) sb.Append(", held"); sb.Append(')'); }
            }
            else if (c._ranDiffraction) sb.Append(" D none");
            if (z.HatR)
            {
                sb.Append(" R ");
                if (z.RDirekt) sb.Append("direct 0");
                else { B(sb, z.R); sb.Append(" (L ").Append(z.LR.ToString("F1", Inv)).Append(" m, n=").Append(z.RAnzahl); if (inf.RMat != null) sb.Append(' ').Append(inf.RMat); if (z.RGehalten) sb.Append(", held"); if (z.RHysterese) sb.Append(", kept"); if (inf.RExtra) sb.Append(", extra"); sb.Append(')'); }
            }
            else if (c._ranReflection) sb.Append(" R none");
            sb.Append(Summe ? " => sum " : " => loudest ");
            B(sb, z.Ergebnis);
            sb.Append(" (d ").Append(z.Direkt.ToString("F1", Inv)).Append(" m)");
            return sb.ToString();
        }

        /// <summary>Klang-Log (Raumweg): Route, Wand zwischen Raeumen (mit Raum/Flanke) und ihre Summe im Bezugsband.</summary>
        internal static string RaumLogText(SpatialAudioOccluder occ, ParallelSoundPropagationCalculator pc)
        {
            if (!Physik.An || pc == null) return null;
            if (!WandZwischenRaeumen.WandFuer(pc, out float ws, out float[] wdb, out float raum, out float flanke)) return null;
            float dyn = Physik.Dynamik.Value;
            float route = pc._propagationScore * dyn;
            var sb = new StringBuilder(120);
            sb.Append(" | paths dB@").Append(F[0].ToString("F0", Inv)).Append("/1k/4k: route -").Append(route.ToString("F1", Inv))
              .Append(" wall -").Append(wdb[0].ToString("F1", Inv)).Append("/-").Append(wdb[1].ToString("F1", Inv)).Append("/-").Append(wdb[2].ToString("F1", Inv));
            if (!float.IsNaN(flanke) || !float.IsNaN(raum))
            {
                sb.Append(" (");
                if (!float.IsNaN(flanke)) { sb.Append("flank "); V(sb, flanke); sb.Append(' '); }
                if (!float.IsNaN(raum)) { sb.Append("room "); V(sb, raum); }
                sb.Append(')');
            }
            double e = WegeKern.Energie(-route) + WegeKern.Energie(-wdb[0]);
            sb.Append(Summe ? " => sum " : " => loudest ").Append((Summe ? WegeKern.DbAus(e, -dyn) : -Math.Min(route, wdb[0])).ToString("F1", Inv));
            return sb.ToString();
        }

        static void B(StringBuilder sb, double[] v)
        {
            for (int b = 0; b < WegeKern.N; b++)
            {
                if (b > 0) sb.Append('/');
                sb.Append(v[b] <= WegeKern.KeinWeg ? "-" : v[b].ToString("F1", Inv));
            }
        }
        static void V(StringBuilder sb, float v) { if (v >= 0) sb.Append('+'); sb.Append(v.ToString("F1", Inv)); }

        internal static void Loggen()
        {
            if (dSchnapp > 0)
            {
                int verdeckt = dSchnapp - dFrei;
                string kosten = "";
                int alle = dTStrahlen + dDStrahlen + dRBefehle;
                if (alle > 0)
                    kosten = $" Rays/casts in 30 s: transmission {dTStrahlen}, diffraction {dDStrahlen}, reflection {dRBefehle} ({dRLauf} runs); " +
                             $"{dRExtra} reflection runs only because of 'Reflections for every occluded sound' = {dRBefehleExtra} casts (+{100.0 * dRBefehleExtra / Math.Max(1, alle - dRBefehleExtra):F0} % of the other audio casts).";
                Plugin.Log.LogInfo($"[Diagnostics] Sound paths: {dSchnapp} finished path calculations ({dFrei} clear), loudest path T {dBestT} / D {dBestD} / R {dBestR}; " +
                                   $"held D {dDGehalten}, held R {dRGehalten}, R kept (< 1 dB change) {dRHyst}; " +
                                   (dSumN > 0 ? $"mean insulation loudest path {dSumMin / dSumN:F1} dB -> {(Summe ? "energy sum" : "result")} {dSumSumme / dSumN:F1} dB ({verdeckt} occluded)." : ".") + kosten);
            }
            if (BandFilter + BandFilterRaumweg + SummeRaumweg > 0)
                Plugin.Log.LogInfo($"[Diagnostics] Sound paths: {BandFilter} low-pass values fitted to the band curve (obstruction), {BandFilterRaumweg} on room paths, {SummeRaumweg} room routes summed with the wall between rooms.");
            dSchnapp = dFrei = dBestT = dBestD = dBestR = dDGehalten = dRGehalten = dRHyst = dRLauf = dRExtra = dTStrahlen = dDStrahlen = dRBefehle = dRBefehleExtra = 0;
            dSumMin = dSumSumme = 0; dSumN = 0; BandFilter = BandFilterRaumweg = SummeRaumweg = 0;
        }

        static V3 V(Vector3 v) => new V3(v.x, v.y, v.z);

        // ================================================================ Pruefschritt Meta-XR-Beugung (nur lesen, nichts umschalten)

        internal static string MetaText;
        static float metaZeit = -1f;

        /// <summary>Raidstart (Raumspeicher fertig): Meta-Einstellung ein paar Sekunden spaeter lesen (Szene ganz geladen).</summary>
        internal static void RaidStart() { metaZeit = Time.realtimeSinceStartup + 5f; }

        internal static void Tick()
        {
            if (metaZeit < 0f || Time.realtimeSinceStartup < metaZeit) return;
            metaZeit = -1f;
            MetaPruefen();
        }

        internal static void MetaPruefen()
        {
            try { MetaText = MetaLesen(); }
            catch (Exception e) { MetaText = "Meta XR acoustics settings not readable (" + e.GetType().Name + ")"; }
            Plugin.Log.LogInfo("[Diagnostics] " + MetaText);
            KlangLog.Text(MetaText);
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        static string MetaLesen()
        {
            var s = MetaXRAcousticSettings.Instance;
            if (s == null) return "Meta XR acoustics: no settings object";
            int geo = UnityEngine.Object.FindObjectsOfType<MetaXRAcousticGeometry>().Length;
            int karten = UnityEngine.Object.FindObjectsOfType<MetaXRAcousticMap>().Length;
            return $"Meta XR acoustics (read only, not changed): acoustic model = {s.AcousticModel}, diffraction enabled = {s.DiffractionEnabled}; " +
                   $"{geo} acoustic geometry objects, {karten} acoustic maps in the scene. With diffraction on, Meta XR also bends sound around its baked geometry " +
                   "(on top of the game's occlusion volume) - to be compared at a corner with the sound log.";
        }
    }
}
