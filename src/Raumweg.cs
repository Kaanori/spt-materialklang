using System;
using System.Collections.Generic;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;
using Audio.SpatialSystem;
using BepInEx.Configuration;
using EFT.DataProviding;
using HarmonyLib;
using UnityEngine;

namespace Materialklang
{
    // 0.8.4 - Raumweg im Physik-Modus aus Geometrie und Portalzustand statt aus der Wegkosten-Heuristik (Ursache und Formel:
    // RaumwegKern.cs). Spiel-Teil: liest nach dem Job dieselben Routen/Portaldaten wie PropagationJob, rechnet jede Route
    // physikalisch (dieselben Portalpunkte wie das Spiel), nimmt die lauteste und schreibt deren Wertung (dB / Max. insulation)
    // in ParallelSoundPropagationCalculator._propagationScore. Alles danach (Wand zwischen Raeumen als Minimum, Lautstaerke,
    // Lautheit, Klang-Log) bleibt wie es ist. Zusaetzlich im Physik-Modus fuer solche Raumwege:
    //  - Der Faktor draussen<->drinnen der Klanggruppe (GetEnvironmentFactor, Heuristik auf die Wegkosten) entfaellt;
    //    der Uebergang steckt jetzt in der Oeffnung selbst.
    //  - Tiefpass = min(Abbildung des Physik-Modus fuer den Tuer-/Fensteranteil, Beugungs-Tiefpass).
    internal static class Raumweg
    {
        internal static ConfigEntry<bool> Aktiv;
        internal static ConfigEntry<float> Alpha;

        const double FHoch = 4000.0;
        const float MinWertung = 0.005f;   // Propagation muss "aktiv" bleiben (Effekt > 0), sonst schaltet das Spiel auf Hindernis

        sealed class Zustand
        {
            public Vector3 A, B;               // Job-Richtung (A = Emitter des Raumpaars)
            public bool Umgekehrt, Hat;
            public uint Paar;
            public ISpatialAudioRoom RaumA, RaumB;
            public float Wertung = -1f, Spiel = -1f, TiefpassBeugung = 22000f, WertungTuer, Umwelt = float.NaN;
            public string Kette;               // fuers Klang-Log
            // 0.8.5: alle Routen mit weichem Rang-Gewicht und zeitlicher Ueberblendung (RaumwegKern.RoutenMischer)
            public readonly RoutenMischer Mix = new RoutenMischer();
            public int Bild = -1;              // letztes Bild mit geschriebenem Wert (Diagnose Pegelsprung)
            public float LetztDb, LetztAlt;
            public V3 Austritt;                // energiegewichteter Austrittspunkt (nur Log, siehe Richtung unten)
            // 0.8.5 Leistung: Bild-Budget (aufgeschobene Rechnung) und Tiefpass-Fit nur bei geaenderter Bandkurve
            public int Aufschub;               // Bilder in Folge aufgeschoben
            public bool Nachholen;             // Rechnung steht aus -> Occluder neu rechnen lassen
            public uint PaarGerechnet;         // Raumpaar der geltenden Wertung
            public double FitM1 = double.NaN, FitM2 = double.NaN; public float FitK, FitTp, FitLp;
            public void Vergessen() { Mix.Leeren(); Wertung = -1f; Bild = -1; Aufschub = 0; Nachholen = false; FitM1 = double.NaN; }
        }

        static readonly ConditionalWeakTable<ParallelSoundPropagationCalculator, Zustand> Tabelle =
            new ConditionalWeakTable<ParallelSoundPropagationCalculator, Zustand>();
        static readonly ConditionalWeakTable<ParallelSoundPropagationCalculator, Zustand>.CreateValueCallback Neu = _ => new Zustand();

        // Kontext: welcher Rechner gehoert zum Klang, der gerade angewendet wird
        internal static ParallelSoundPropagationCalculator Multi;     // MultiSource: OnUpdateOcclusionEffects
        static SpatialAudioOccluder applyOcc;

        // 0.8.5: Routen je Rang voll (Demo: 3), Rest weich ueber 'Soft rank width'
        internal const int RangN = 3;
        static double BlendS => Wege.Summe && Wege.BlendMs != null ? Wege.BlendMs.Value / 1000.0 : 0.0;
        static int RangNJetzt => Wege.Summe ? RangN : 1;
        static double BreiteJetzt => Wege.Summe && Wege.RangBreite != null ? Wege.RangBreite.Value : 0.0;
        static int hauptThread = -1;

        // Diagnose
        static int gerechnet, routen, ohneDaten;
        static int dMisch, dMischRouten, dGeister, dNeue, dZwang, dBilder, dSpruengeNeu, dSpruengeAlt;
        static double dMaxNeu, dMaxAlt;
        static double summeSpiel, summeNeu;
        static float letzteUmwelt = float.NaN;

        internal static bool An => Physik.An && Aktiv != null && Aktiv.Value;

        // 0.8.5 Leistung: Bild-Budget (Interchange-Gefecht: SpatialAudioSystem.LateUpdate bis 28 ms, Ursache RaumwegNetz.cs)
        internal static ConfigEntry<float> BudgetMs;
        const int MaxAufschub = 4;
        internal static readonly Laufzeit ZeitGesamt = new Laufzeit("room paths (all)"), ZeitRechnen = new Laufzeit("route calculation"),
            ZeitSchreiben = new Laufzeit("route mix/low-pass"), ZeitWege = new Laufzeit("sound paths snapshot (T/D/R)"),
            ZeitLaut = new Laufzeit("volume + audibility"), ZeitFilter = new Laufzeit("low-/high-pass + audibility");
        static int dAufgeschoben, dUeberBudget, dNachgeholt, dBudgetBilder, letztesUeberBild = -1;

        internal static void Binden(ConfigFile c)
        {
            const string s = "0. Physics Mode (real values)";
            Aktiv = c.Bind(s, "Room paths: physical openings", true,
                "Sound between rooms (via doors/windows): computed from the path geometry and the portal state instead of the game's route cost. " +
                "Open openings: edge diffraction (Kurze-Anderson) and the sound field of the rooms in between; closed doors/windows keep the values of section 4. " +
                "Off = the game's route cost read as insulation (0.8.3).");
            Alpha = c.Bind(s, "Room paths: room absorption", 0.1f,
                new ConfigDescription("Mean absorption coefficient of the rooms on a room path (how much sound a room swallows). 0.1 = the game's baked reverb data. " +
                    "Lower = rooms pass more sound on.", new AcceptableValueRange<float>(0.02f, 0.5f)));
            BudgetMs = c.Bind(s, "Room paths: max. ms per frame", 1.0f,
                new ConfigDescription("CPU budget of the room-path calculation per frame (main thread). New sounds (shots, impacts) are always calculated at once; " +
                    "sounds that already have a value wait for the next frame when the budget is used up (at most 4 frames). The values stay the same, only later. " +
                    "0 = no limit. The diagnostic line every 30 s shows the time used.", new AcceptableValueRange<float>(0f, 10f)));
        }

        internal static void Anwenden(Harmony h)
        {
            try
            {
                var t = typeof(ParallelSoundPropagationCalculator);
                var upd = AccessTools.DeclaredMethod(t, "OnUpdate");
                var spaet = AccessTools.DeclaredMethod(t, "OnLateUpdate");
                var multi = AccessTools.DeclaredMethod(typeof(MultiSourceSpatialAudioOccluder), "OnUpdateOcclusionEffects");
                var apply = AccessTools.DeclaredMethod(typeof(SpatialAudioOccluder), "ApplyOcclusionToSource", new[] { typeof(BetterSource), typeof(bool) });
                if (upd == null || spaet == null || multi == null || apply == null)
                {
                    Plugin.Log.LogWarning("Room paths (physics): targets not found (game update?) -> the game's route cost is used.");
                    return;
                }
                h.Patch(upd, postfix: new HarmonyMethod(typeof(Raumweg), nameof(UpdateNach)));
                h.Patch(spaet, prefix: new HarmonyMethod(typeof(Raumweg), nameof(SpaetVor)), postfix: new HarmonyMethod(typeof(Raumweg), nameof(SpaetNach)));
                h.Patch(multi, prefix: new HarmonyMethod(typeof(Raumweg), nameof(MultiVor)) { priority = Priority.First },
                    finalizer: new HarmonyMethod(typeof(Raumweg), nameof(MultiEnde)));
                h.Patch(apply, prefix: new HarmonyMethod(typeof(Raumweg), nameof(ApplyVor)) { priority = Priority.First },
                    finalizer: new HarmonyMethod(typeof(Raumweg), nameof(ApplyEnde)));
                // 0.8.5: Pool-Reset vergisst die Routen; laufende Ueberblendung haelt Continuous-Occluder in Bewegung
                hauptThread = System.Threading.Thread.CurrentThread.ManagedThreadId;
                var reset = AccessTools.DeclaredMethod(t, "OnReset");
                if (reset != null) h.Patch(reset, postfix: new HarmonyMethod(typeof(Raumweg), nameof(ResetNach)));
                foreach (var ot in new[] { typeof(ContinuousPropagatedSpatialAudioOccluder), typeof(ContinuousSpatialAudioOccluder) })
                {
                    var ou = AccessTools.DeclaredMethod(ot, "OnUpdate");
                    if (ou != null) h.Patch(ou, prefix: new HarmonyMethod(typeof(Raumweg), nameof(OccUpdateVor)));
                    else Plugin.Log.LogWarning($"Room paths (physics): {ot.Name}.OnUpdate not found -> route blends of continuous sounds only advance when they move.");
                }
            }
            catch (Exception e) { Statistik.Fehler(e); }
        }

        // ---------------------------------------------------------------- Harmony

        static void UpdateNach(ParallelSoundPropagationCalculator __instance, AudioSourceContainer sourceContainer, Vector3 listenerPosition)
        {
            if (!An || __instance._earlyOutThisFrame || sourceContainer == null) return;
            try
            {
                var sys = __instance._audioSystem;
                if (sys == null || !sys.TryGetMatchingRoomPair(sourceContainer.CurrentAudioRoom, out var rp, out bool rev)) return;
                var z = Tabelle.GetValue(__instance, Neu);
                Vector3 q = sourceContainer.CurrentPosition;
                z.Umgekehrt = rev;
                z.A = rev ? listenerPosition : q; z.B = rev ? q : listenerPosition;
                z.RaumA = rev ? sys.ListenerCurrentRoom : sourceContainer.CurrentAudioRoom;
                z.RaumB = rev ? sourceContainer.CurrentAudioRoom : sys.ListenerCurrentRoom;
                z.Paar = rp.ID; z.Hat = true;
            }
            catch (Exception e) { Statistik.Fehler(e); }
        }

        // Genau die Bedingung, unter der OnLateUpdate das Job-Ergebnis uebernimmt.
        static void SpaetVor(ParallelSoundPropagationCalculator __instance, out bool __state)
        {
            __state = false;
            try
            {
                __state = An && (__instance._isPositionChanged || __instance._forceUpdate) && !__instance._earlyOutThisFrame
                          && __instance._propagationJobScheduler != null && __instance._propagationJobScheduler.LastHandle.IsCompleted;
            }
            catch (Exception e) { Statistik.Fehler(e); }
        }

        // 0.8.5: laeuft jedes Mal (nicht nur, wenn ein Job-Ergebnis kommt): neue Rechnung -> Routen uebernehmen; sonst die
        // Ueberblendung zeitlich fortschreiben, solange der Spielwert noch unserer ist.
        static void SpaetNach(ParallelSoundPropagationCalculator __instance, bool __state)
        {
            if (!An) return;
            long t0 = Laufzeit.Jetzt;
            int bild = Time.frameCount;
            try
            {
                if (!Tabelle.TryGetValue(__instance, out var z)) return;
                if (__instance._earlyOutThisFrame) { if (z.Wertung >= 0f || z.Mix.Hat) z.Vergessen(); return; }
                double jetzt = Time.realtimeSinceStartup;
                if (__state && z.Hat)
                {
                    // 0.8.5 Bild-Budget: ein Klang, der schon seinen physikalischen Wert (gleiches Raumpaar) hat, wartet, wenn das
                    // Budget verbraucht ist; neue Klaenge (Einmal-Klaenge) werden immer sofort gerechnet.
                    float budget = BudgetMs != null ? BudgetMs.Value : 0f;
                    if (budget > 0f && ZeitRechnen.ImBild(bild) >= budget)
                    {
                        if (letztesUeberBild != bild) { letztesUeberBild = bild; dBudgetBilder++; }
                        if (z.Wertung >= 0f && z.PaarGerechnet == z.Paar && z.Aufschub < MaxAufschub)
                        {
                            z.Aufschub++; z.Nachholen = true; dAufgeschoben++;
                            __instance._propagationScore = z.Wertung;          // unser letzter Wert gilt weiter (nicht die Wegkosten)
                            NeuAnfordern(__instance);
                            if (z.Mix.Laeuft) Schreiben(__instance, z, jetzt, false);
                            return;
                        }
                        dUeberBudget++;
                    }
                    float spiel = __instance._propagationScore;
                    long tr = Laufzeit.Jetzt;
                    bool ok = Rechnen(__instance, z, spiel, jetzt);
                    ZeitRechnen.Dazu(tr, bild);
                    if (z.Nachholen) dNachgeholt++;
                    z.Aufschub = 0; z.Nachholen = false;
                    if (!ok) { ohneDaten++; z.Vergessen(); return; }
                    z.Spiel = spiel; z.PaarGerechnet = z.Paar;
                    Schreiben(__instance, z, jetzt, true);
                    gerechnet++; summeSpiel += spiel * Physik.Dynamik.Value; summeNeu += z.Wertung * Physik.Dynamik.Value;
                }
                else if (z.Wertung >= 0f && z.Mix.Laeuft && __instance._propagationScore == z.Wertung) Schreiben(__instance, z, jetzt, false);
            }
            catch (Exception e) { Statistik.Fehler(e); }
            finally { ZeitGesamt.Dazu(t0, bild); }
        }

        /// <summary>0.8.5: aufgeschobene Rechnung im naechsten Bild anfordern - der Rechner haelt die Position fuer "geaendert"
        /// (Combined/MultiSource), Continuous-Occluder bekommen den Zwang in OccUpdateVor (z.Nachholen).</summary>
        static void NeuAnfordern(ParallelSoundPropagationCalculator pc)
        {
            var c = pc._positionChangeChecker;
            if (c != null) c._previousListenerPos = new Vector3(1e9f, 1e9f, 1e9f);
        }

        static readonly double[] mixDb = new double[3];

        /// <summary>Summe der gehaltenen Routen als Wertung und Tiefpass schreiben (neu = gerade uebernommen).</summary>
        static void Schreiben(ParallelSoundPropagationCalculator pc, Zustand z, double jetzt, bool neu)
        {
            int bild = Time.frameCount;
            long t0 = Laufzeit.Jetzt;
            float dyn = Physik.Dynamik.Value;
            if (!neu) z.Mix.Fortschreiben(jetzt, BlendS);
            if (!z.Mix.Summe(dyn, mixDb, out var aus, out _)) { mixDb[0] = mixDb[1] = mixDb[2] = dyn; }
            float w = Mathf.Clamp((float)(mixDb[0] / dyn), MinWertung, 1f);
            z.Wertung = w; pc._propagationScore = w;
            z.WertungTuer = 0f;                                        // der Tuer-Tiefpass steckt schon in den Baendern
            // 0.8.5: Fit (Minimumsuche) nur, wenn sich die Bandkurve oder die Einstellungen geaendert haben
            double m1 = mixDb[1] - mixDb[0], m2 = mixDb[2] - mixDb[0];
            float k = Wege.BandStaerke.Value, tp = Physik.TiefpassMin.Value;
            if (m1 != z.FitM1 || m2 != z.FitM2 || k != z.FitK || tp != z.FitTp)
            {
                z.FitLp = Wege.FitLp(m1, m2);
                z.FitM1 = m1; z.FitM2 = m2; z.FitK = k; z.FitTp = tp;
            }
            z.TiefpassBeugung = z.FitLp;
            z.Austritt = aus;
            ZeitSchreiben.Dazu(t0, bild);
            // Diagnose: groesster Pegelsprung je Bild (Summe) gegen "lauteste Route allein"
            if (bild == z.Bild) { z.LetztDb = w * dyn; return; }
            float db = w * dyn;
            float alt = double.IsNaN(z.Mix.GewinnerDb) ? db : Mathf.Clamp((float)z.Mix.GewinnerDb, MinWertung * dyn, dyn);
            if (z.Bild >= 0)
            {
                float sn = Mathf.Abs(db - z.LetztDb), sa = Mathf.Abs(alt - z.LetztAlt);
                dBilder++;
                if (sn > dMaxNeu) dMaxNeu = sn;
                if (sa > dMaxAlt) dMaxAlt = sa;
                if (sn > 1f) dSpruengeNeu++;
                if (sa > 1f) dSpruengeAlt++;
            }
            z.Bild = bild; z.LetztDb = db; z.LetztAlt = alt;
        }

        static void ResetNach(ParallelSoundPropagationCalculator __instance)
        {
            try
            {
                if (System.Threading.Thread.CurrentThread.ManagedThreadId != hauptThread) return;
                if (Tabelle.TryGetValue(__instance, out var z)) z.Vergessen();
            }
            catch (Exception e) { Statistik.Fehler(e); }
        }

        // Continuous(Propagated)SpatialAudioOccluder rechnen nur nach 1 m Bewegung neu (oder auf Zwang) und wenden nur dann an.
        // Solange eine Ueberblendung laeuft, wird der Zwang gesetzt - wie bei einer Tuer, die aufgeht (OnPortalStatusChanged).
        static void OccUpdateVor(SpatialAudioOccluder __instance)
        {
            if (!An) return;
            try
            {
                var pc = RechnerVon(__instance);
                if (pc == null || !Tabelle.TryGetValue(pc, out var z) || z.Wertung < 0f || (!z.Mix.Laeuft && !z.Nachholen)) return;
                switch (__instance)
                {
                    case ContinuousPropagatedSpatialAudioOccluder p: if (!p._forceUpdate) { p._forceUpdate = true; dZwang++; } break;
                    case ContinuousSpatialAudioOccluder c: if (!c._forceUpdate) { c._forceUpdate = true; dZwang++; } break;
                }
            }
            catch (Exception e) { Statistik.Fehler(e); }
        }

        static void MultiVor(MultiSourceSpatialAudioOccluder __instance, AudioSourceContainer sourceContainer)
        {
            Multi = null;
            try
            {
                if (sourceContainer != null && __instance._activePropagationCalculatorsByContID.TryGetValue(sourceContainer.ID, out var c)) Multi = c;
            }
            catch (Exception e) { Statistik.Fehler(e); }
        }

        static Exception MultiEnde(Exception __exception) { Multi = null; return __exception; }
        static void ApplyVor(SpatialAudioOccluder __instance, BetterSource source) { applyOcc = __instance; applySrc = source; }
        static BetterSource applySrc;
        /// <summary>0.8.5: Quelle, die gerade angewendet wird (Hoerbarkeit je Band in Physik.FilterNach).</summary>
        internal static BetterSource AnwendQuelle => applySrc;
        /// <summary>0.8.4 Stufe 1: Occluder, dessen Quelle gerade angewendet wird (fuer den Band-Tiefpass in Physik.FilterNach).</summary>
        internal static SpatialAudioOccluder AnwendOcc => applyOcc;

        /// <summary>0.8.4 Stufe 1: Wertung und Tiefpass der physikalischen Route (fuer die Summe mit der Wand zwischen Raeumen).
        /// false = Route nicht physikalisch gerechnet (dann gilt die Spielwertung und die alte Abbildung).</summary>
        internal static bool Route(SpatialAudioOccluder occ, ParallelSoundPropagationCalculator pc, out float wertung, out float lp)
        {
            wertung = 0f; lp = 22000f;
            try
            {
                if (!An || pc == null || !Tabelle.TryGetValue(pc, out var z) || z.Wertung < 0f || pc._propagationScore != z.Wertung) return false;
                wertung = z.Wertung;
                lp = Mathf.Min(Physik.Abbildung(z.WertungTuer), z.TiefpassBeugung);
                return true;
            }
            catch (Exception e) { Statistik.Fehler(e); return false; }
        }
        static Exception ApplyEnde(Exception __exception) { applyOcc = null; applySrc = null; return __exception; }

        // ---------------------------------------------------------------- fuer Physik / Klang-Log

        /// <summary>Raumweg-Rechner eines Occluders (MultiSource: der des gerade bearbeiteten Einmal-Klangs).</summary>
        internal static ParallelSoundPropagationCalculator RechnerVon(SpatialAudioOccluder occ)
        {
            switch (occ)
            {
                case MultiSourceSpatialAudioOccluder _: return Multi;
                case CombinedSpatialAudioOccluder c: return c._propagationCalculator as ParallelSoundPropagationCalculator;
                case ContinuousPropagatedSpatialAudioOccluder p: return p._propagationCalculator as ParallelSoundPropagationCalculator;
                case ContinuousSpatialAudioOccluder s: return s._propagationCalculator as ParallelSoundPropagationCalculator;
            }
            return null;
        }

        static Zustand Gilt(SpatialAudioOccluder occ, out ParallelSoundPropagationCalculator pc)
        {
            pc = null;
            if (!An || occ == null || !occ.IsPropagationActive) return null;
            pc = RechnerVon(occ);
            if (pc == null || !Tabelle.TryGetValue(pc, out var z) || z.Wertung < 0f) return null;
            // nur solange der Spielwert noch unserer ist (sonst: Fruehabbruch/neuer Raum)
            return pc._propagationScore == z.Wertung ? z : null;
        }

        /// <summary>Aus Physik.LautstaerkeNach: Faktor draussen/drinnen der Klanggruppe entfernen (Heuristik auf Wegkosten).</summary>
        internal static void UmweltEntfernen(SpatialAudioOccluder occ)
        {
            try
            {
                var z = Gilt(occ, out _);
                if (z == null) return;
                float pe = Mathf.Clamp01(occ.PropagationEffect);
                if (pe > 1e-4f) z.Umwelt = letzteUmwelt = occ.OcclusionEffect / pe;
                occ.OcclusionEffect = pe;
            }
            catch (Exception e) { Statistik.Fehler(e); }
        }

        /// <summary>Aus Physik.FilterNach: Tiefpass fuer den Klang, der gerade angewendet wird (nur wenn die Route gewinnt).</summary>
        internal static bool Tiefpass(out float beugung, out float wertungTuer) => Tiefpass(applyOcc, out beugung, out wertungTuer);

        /// <summary>0.8.5: wie Tiefpass, fuer einen beliebigen Occluder (Physik.LautstaerkeNach / Klang-Log).</summary>
        internal static bool Tiefpass(SpatialAudioOccluder occ, out float beugung, out float wertungTuer)
        {
            beugung = 22000f; wertungTuer = 0f;
            try
            {
                var z = Gilt(occ, out var pc);
                if (z == null) return false;
                // Wand zwischen Raeumen gewann? Aus dem UNGEGLAETTETEN Wand-Wert entscheiden (wie ScorePostfix: min(Route, Wand)).
                // Bis 0.8.5: Vergleich mit occ.PropagationEffect - das ist der vom Spiel geglaettete Wert (Lerp); stieg die
                // Wertung minimal, hinkte er hinterher und der Tiefpass sprang bildweise auf die Abbildung (~10 kHz) = Flattern.
                if (WandZwischenRaeumen.WandFuer(pc, out float wand, out _, out _, out _) && wand < z.Wertung - 1e-4f) return false;
                beugung = z.TiefpassBeugung; wertungTuer = z.WertungTuer;
                return true;
            }
            catch (Exception e) { Statistik.Fehler(e); return false; }
        }

        /// <summary>Klang-Log: Portalkette und Spielwert der Route (nur path=rooms).</summary>
        internal static string LogText(SpatialAudioOccluder occ, out ParallelSoundPropagationCalculator pc)
        {
            var z = Gilt(occ, out pc);
            if (z == null) return null;
            var inv = CultureInfo.InvariantCulture;
            return z.Kette + (float.IsNaN(z.Umwelt) ? "" : "; indoor/outdoor factor x" + z.Umwelt.ToString("F2", inv) + " ignored")
                   + "; rolloff x" + occ.CurrentParameters.OcclusionRolloffScale.ToString("F2", inv) + RoutenText(z);
        }

        /// <summary>0.8.5 Klang-Log: je Route Pegel, Rang, Gewicht (aktuell -> Ziel) und Blendzustand, dann die Summe und die Richtung.</summary>
        static string RoutenText(Zustand z)
        {
            var inv = CultureInfo.InvariantCulture;
            var sb = new StringBuilder(200);
            float dyn = Physik.Dynamik.Value;
            sb.Append(" | routes (").Append(Wege.Summe ? "energy sum, soft rank N " + RangN + " width " + BreiteJetzt.ToString("F0", inv) + " dB, blend " + (BlendS * 1000).ToString("F0", inv) + " ms" : "loudest route").Append("):");
            int k = 0;
            foreach (var e in z.Mix.Eintraege)
            {
                if (k++ >= 6) { sb.Append(" +").Append(z.Mix.Eintraege.Count - 6).Append(" more"); break; }
                sb.Append(k == 1 ? " " : "; ");
                for (int i = 0; i < e.N; i++)
                {
                    var p = e.Portale[i] < portalJeIndex.Length ? portalJeIndex[e.Portale[i]] : null;
                    if (i > 0) sb.Append('>');
                    sb.Append(p != null ? (string.IsNullOrEmpty(p.portalName) ? p.name : p.portalName) : "").Append('#').Append(e.Portale[i]);
                }
                sb.Append(" -").Append(e.Db[0].ToString("F1", inv)).Append(" dB");
                if (e.Rang > 0) sb.Append(" rank ").Append(e.Rang);
                sb.Append(" w ").Append(e.W.ToString("F2", inv));
                if (Math.Abs(e.W - e.Ziel) > 1e-3) sb.Append("->").Append(e.Ziel.ToString("F2", inv));
                sb.Append(e.Geist ? " ghost fading out" : !e.Da ? " gone, fading out" : e.W < e.Ziel - 1e-3 ? " fading in" : e.W > e.Ziel + 1e-3 ? " fading out" : " steady");
            }
            sb.Append(" => -").Append((z.Wertung * dyn).ToString("F1", inv)).Append(" dB");
            if (!double.IsNaN(z.Mix.GewinnerDb)) sb.Append(" (loudest alone -").Append(Math.Min(dyn, z.Mix.GewinnerDb).ToString("F1", inv)).Append(')');
            // Richtung nur zur Information: das Spiel hat keine virtuelle Quellposition (siehe CHANGELOG 0.8.5)
            V3 h = V(z.Umgekehrt ? z.A : z.B);
            if (!z.Austritt.IstNull && !double.IsNaN(z.Mix.GewinnerDb))
                sb.Append("; weighted exit vs. loudest door ").Append(RoutenMischer.Winkel(h, z.Austritt, z.Mix.GewinnerAustritt).ToString("F0", inv)).Append(" deg");
            return sb.ToString();
        }

        internal static void Loggen()
        {
            // 0.8.5 Leistung: Zeit auf dem Hauptfaden je Teil (Summe in 30 s, groesster Wert in einem Bild)
            if (!ZeitGesamt.Leer || !ZeitWege.Leer || !ZeitLaut.Leer)
            {
                string budget = BudgetMs != null && BudgetMs.Value > 0f ? $"budget {BudgetMs.Value:F1} ms/frame" : "no budget";
                Plugin.Log.LogInfo($"[Diagnostics] Timing (main thread, 30 s): {ZeitGesamt.Text()}; of it {ZeitRechnen.Text()} and {ZeitSchreiben.Text()}; " +
                                   $"{dTreffer} route sets reused, {dFehl} calculated ({Netz.DRaumA} room absorptions, {Netz.DRaumAGespart} from the frame cache, " +
                                   $"{Netz.CacheAnzahl} cached sets); {budget}: {dAufgeschoben} calculations deferred, {dNachgeholt} caught up, " +
                                   $"{dUeberBudget} new sounds over budget, {dBudgetBilder} frames at the budget | {ZeitWege.Text()}; {ZeitLaut.Text()}; {ZeitFilter.Text()}.");
                dTreffer = dFehl = dAufgeschoben = dNachgeholt = dUeberBudget = dBudgetBilder = 0; Netz.DRaumA = Netz.DRaumAGespart = 0;
            }
            if (dMisch > 0)
                Plugin.Log.LogInfo($"[Diagnostics] Room paths (physics): {dMisch} route sets mixed ({(Wege.Summe ? $"soft rank N {RangN}, width {BreiteJetzt:F0} dB, blend {BlendS * 1000:F0} ms" : "loudest route")}), " +
                                   $"on average {(double)dMischRouten / dMisch:F1} routes contributing, {dNeue} faded in, {dGeister} ghosts (same route jumped > {RoutenMischer.GeistDb:F0} dB), " +
                                   $"{dZwang} forced updates of continuous sounds while blending; largest room-path level step per frame {dMaxNeu:F1} dB " +
                                   $"(loudest route alone {dMaxAlt:F1} dB), frames with a step > 1 dB {dSpruengeNeu} (loudest alone {dSpruengeAlt}) of {dBilder}.");
            dMisch = dMischRouten = dGeister = dNeue = dZwang = dBilder = dSpruengeNeu = dSpruengeAlt = 0; dMaxNeu = dMaxAlt = 0;
            if (gerechnet + ohneDaten == 0) return;
            Plugin.Log.LogInfo($"[Diagnostics] Room paths (physics): {gerechnet} routes recalculated ({routen} candidates), game route cost read as insulation on average " +
                               $"{(gerechnet > 0 ? summeSpiel / gerechnet : 0):F1} dB -> physical {(gerechnet > 0 ? summeNeu / gerechnet : 0):F1} dB, {ohneDaten} without data (game value kept)" +
                               (float.IsNaN(letzteUmwelt) ? "." : $", indoor/outdoor factor of the sound group ignored (last x{letzteUmwelt:F2})."));
            gerechnet = routen = ohneDaten = 0; summeSpiel = summeNeu = 0;
        }

        // ---------------------------------------------------------------- Rechnung

        // 0.8.5 Leistung: Raumnetz einmal je Raid, Routenschleife als reine Rechnung (RaumwegNetz.cs). Dieser Teil liest nur noch
        // die Job-Daten ein und haelt die Unity-Objekte.
        sealed class SpielNetz : RaumwegNetz
        {
            public SpatialAudioRoom[] Raeume = new SpatialAudioRoom[0];
            public readonly Dictionary<SpatialAudioRoom, int> Index = new Dictionary<SpatialAudioRoom, int>(Gleich.Ref);

            protected override void Messen(int raum, out double flaeche, out double vol)
            {
                flaeche = 0; vol = 0;
                var sr = raum < Raeume.Length ? Raeume[raum] : null;
                if (sr == null) return;
                var cols = sr.Colliders;
                if (cols != null)
                    foreach (var bc in cols)
                    {
                        if (bc == null) continue;
                        Vector3 g = Vector3.Scale(bc.size, bc.transform.lossyScale);
                        g = new Vector3(Mathf.Abs(g.x), Mathf.Abs(g.y), Mathf.Abs(g.z));
                        flaeche += 2.0 * (g.x * g.y + g.y * g.z + g.z * g.x);
                        vol += (double)g.x * g.y * g.z;
                    }
                if (flaeche <= 0)
                {
                    Vector3 g = sr.Bounds.size;
                    flaeche = 2.0 * (g.x * g.y + g.y * g.z + g.z * g.x);
                    vol = (double)g.x * g.y * g.z;
                }
            }

            /// <summary>Index eines Raums (ISpatialAudioRoom, der kein SpatialAudioRoom ist: -1 wie bisher).</summary>
            public int Von(ISpatialAudioRoom r)
            {
                var sr = r as SpatialAudioRoom;
                if ((object)sr == null) return -1;
                if (Index.TryGetValue(sr, out int i)) return i;
                i = RaumDazu(sr.ID, sr.IsOutdoor);
                if (i >= Raeume.Length) Array.Resize(ref Raeume, Math.Max(16, Raeume.Length * 2));
                Raeume[i] = sr; Index[sr] = i;
                return i;
            }
        }

        sealed class Gleich : IEqualityComparer<SpatialAudioRoom>, IEqualityComparer<BaseSpatialAudioPortal>
        {
            public static readonly Gleich Ref = new Gleich();
            public bool Equals(SpatialAudioRoom x, SpatialAudioRoom y) => ReferenceEquals(x, y);
            public int GetHashCode(SpatialAudioRoom o) => RuntimeHelpers.GetHashCode(o);
            public bool Equals(BaseSpatialAudioPortal x, BaseSpatialAudioPortal y) => ReferenceEquals(x, y);
            public int GetHashCode(BaseSpatialAudioPortal o) => RuntimeHelpers.GetHashCode(o);
        }

        static readonly SpielNetz Netz = new SpielNetz();
        static SpatialAudioDataContainer letzterContainer;
        static BaseSpatialAudioPortal[] portalJeIndex = new BaseSpatialAudioPortal[0];
        static int portalAnzahl = -1;
        static int dTreffer, dFehl;

        static bool Rechnen(ParallelSoundPropagationCalculator c, Zustand z, float spiel, double jetzt)
        {
            var s = c._propagationJobScheduler;
            if (s == null || !s._routesSettingsData.IsCreated || !s._routesPortalIndicesData.IsCreated || !s._staticPortalDataList.IsCreated) return false;
            if (!DataProvider.TryGetData<SpatialAudioDataContainer>(out var data) || data == null) return false;
            PortalTabelle(data);
            var routes = s._routesSettingsData; var indizes = s._routesPortalIndicesData; var liste = s._staticPortalDataList;
            int len = routes.Length;
            if (len == 0) return false;
            int anzahl = Mathf.Min(len, Mathf.Max(Mathf.Min(350, len), Mathf.CeilToInt(len * c._qualityFactor)));   // wie Schedule
            if (Netz.BildSetzen(Time.frameCount)) BildAuffrischen();

            // Job-Daten einlesen (wie die Schleife bis 0.8.5: ungueltige Route -> uebersprungen)
            int listeN = liste.Length, idxN = indizes.Length;
            Netz.RechnungBeginn(listeN);
            if (Netz.RStart.Length < anzahl) { Array.Resize(ref Netz.RStart, anzahl * 2); Array.Resize(ref Netz.RN, anzahl * 2); }
            int f = 0;
            for (int r = 0; r < anzahl; r++)
            {
                var rd = routes[r];
                int n = rd.PortalDataLength;
                Netz.RN[r] = -1;
                if (n <= 0 || n > RaumwegNetz.MaxPortale || rd.StartIndex + n > idxN) continue;
                if (f + n > Netz.RIdx.Length) Array.Resize(ref Netz.RIdx, Math.Max(Netz.RIdx.Length * 2, f + n));
                bool ok = true;
                for (int i = 0; i < n; i++)
                {
                    int pi = indizes[rd.StartIndex + i];
                    if (pi < 0 || pi >= listeN) { ok = false; break; }
                    Netz.RIdx[f + i] = pi;
                    if (!Netz.JobGeladen(pi))
                    {
                        var d = liste[pi];
                        var geo = new RaumPortal
                        {
                            Mitte = new V3(d.center.x, d.center.y, d.center.z),
                            Rechts = new V3(d.portalRight.x, d.portalRight.y, d.portalRight.z),
                            Oben = new V3(d.portalUp.x, d.portalUp.y, d.portalUp.z),
                            Normale = new V3(d.portalNormal.x, d.portalNormal.y, d.portalNormal.z),
                            HalbX = d.portalHalfSize.x, HalbY = d.portalHalfSize.y,
                        };
                        Netz.JobSetzen(pi, geo, d.closureLevel, WandFaktor(d), GeoSchluessel(d));
                    }
                }
                if (!ok) continue;
                Netz.RStart[r] = f; Netz.RN[r] = n; f += n;
            }

            var ein = new RaumwegNetz.Eingabe
            {
                A = V(z.A), B = V(z.B), Umgekehrt = z.Umgekehrt, Paar = z.Paar,
                RaumA = Netz.Von(z.RaumA), RaumB = Netz.Von(z.RaumB),
                RaumAId = z.RaumA != null ? z.RaumA.ID : (short)-1, RaumADraussen = z.RaumA != null && z.RaumA.IsOutdoor,
                Dyn = Physik.Dynamik.Value, Alpha = Alpha.Value, TiefpassMin = Physik.TiefpassMin.Value,
                FRef = Physik.Frequenz.Value, FHoch = FHoch, Fit1 = (float)Wege.F[1], Fit2 = (float)Wege.F[2], RoutenGesamt = len,
            };
            bool log = KlangLog.An;
            bool hat = Netz.Rechne(in ein, anzahl, z.Mix, log, out int n0, out bool treffer);
            routen += n0;
            if (treffer) dTreffer++; else dFehl++;
            if (!hat && !z.Mix.Hat) return false;
            // 0.8.5: alle Routen in den Mischer (weiches Rang-Gewicht, verschwindende blenden aus); Wertung schreibt Schreiben()
            z.Mix.Uebernehmen(jetzt, BlendS, RangNJetzt, BreiteJetzt);
            dGeister += z.Mix.Geister; dNeue += z.Mix.Neue; z.Mix.Geister = 0; z.Mix.Neue = 0;
            dMisch++;
            foreach (var en in z.Mix.Eintraege) if (en.W > 0.005) dMischRouten++;
            z.Kette = log && hat ? Kette(spiel, ein.Dyn, Netz.Best, Netz.BestN, len, anzahl) : null;
            return true;
        }

        /// <summary>Einmal je Bild: Schliessung und Transmission aller Portale (Felder der Objekte, keine Unity-Aufrufe).</summary>
        static void BildAuffrischen()
        {
            int n = Math.Min(Netz.PortalN, portalJeIndex.Length);
            for (int pi = 0; pi < n; pi++)
            {
                if (!Netz.HatObjekt[pi]) continue;
                var p = portalJeIndex[pi];
                Netz.Schliessung[pi] = p.PortalClosureLevel;
                Netz.Voll[pi] = Mathf.Lerp(1f, 0.1f, p._transmission);
            }
        }

        // Wie PropagationJob: ein Raum draussen -> Wert des Innenraums, sonst Mittel.
        static float WandFaktor(in RoomPair.AudioPortalData d)
        {
            if (d.isFrontRoomOutdoor != d.isBackRoomOutdoor) return d.isFrontRoomOutdoor ? d.backRoomWallOcclusion : d.frontRoomWallOcclusion;
            return (d.frontRoomWallOcclusion + d.backRoomWallOcclusion) * 0.5f;
        }

        /// <summary>Schluessel eines Portals aus seiner Geometrie (Mitte auf 5 cm, Halbgroessen auf 5 cm).</summary>
        static int GeoSchluessel(in RoomPair.AudioPortalData d)
        {
            unchecked
            {
                int h = 17;
                h = h * 31 + Mathf.RoundToInt(d.center.x * 20f); h = h * 31 + Mathf.RoundToInt(d.center.y * 20f);
                h = h * 31 + Mathf.RoundToInt(d.center.z * 20f);
                h = h * 31 + Mathf.RoundToInt(d.portalHalfSize.x * 20f); h = h * 31 + Mathf.RoundToInt(d.portalHalfSize.y * 20f);
                return h;
            }
        }

        /// <summary>Portal-/Raumtabelle (einmal je Raid bzw. wenn sich die Portalzahl aendert): je Portal Flaeche, Raeume vorn/hinten;
        /// je Raum seine Oeffnungen (eindeutig, Reihenfolge wie bis 0.8.5).</summary>
        static void PortalTabelle(SpatialAudioDataContainer data)
        {
            var map = data._portalDataBufferService?._indexMap;
            if (map == null) return;
            if (ReferenceEquals(data, letzterContainer) && map.Count == portalAnzahl) return;
            long t0 = Laufzeit.Jetzt;
            letzterContainer = data; portalAnzahl = map.Count;
            int max = 0;
            foreach (var kv in map) max = Math.Max(max, kv.Value + 1);
            portalJeIndex = new BaseSpatialAudioPortal[max];
            foreach (var kv in map)
                if (data.PortalsByID.TryGetValue(kv.Key, out var p)) portalJeIndex[kv.Value] = p;
            // Raeume (dicht) und Oeffnungen je Raum
            var raeume = new List<SpatialAudioRoom>();
            var raumIdx = new Dictionary<SpatialAudioRoom, int>(Gleich.Ref);
            var listen = new List<List<int>>();
            var gesehen = new List<HashSet<BaseSpatialAudioPortal>>();
            Netz.PortaleAnlegen(max);
            int eintraege = 0;
            for (int pi = 0; pi < max; pi++)
            {
                var p = portalJeIndex[pi];
                if (p == null) { portalJeIndex[pi] = null; continue; }
                Netz.HatObjekt[pi] = true;
                var g = p.GetPortalSize();
                Netz.FlaecheP[pi] = Math.Abs((double)g.x * g.y);
                Netz.Voll[pi] = Mathf.Lerp(1f, 0.1f, p._transmission);
                Netz.Schliessung[pi] = p.PortalClosureLevel;
                for (int j = 0; j < 2; j++)
                {
                    var raum = j == 0 ? p.FrontRoom : p.BackRoom;
                    if (raum == null) continue;
                    if (!raumIdx.TryGetValue(raum, out int ri))
                    {
                        ri = raeume.Count; raeume.Add(raum); raumIdx[raum] = ri;
                        listen.Add(new List<int>(8)); gesehen.Add(new HashSet<BaseSpatialAudioPortal>(Gleich.Ref));
                    }
                    if (j == 0) Netz.Vorn[pi] = ri; else Netz.Hinten[pi] = ri;
                    if (gesehen[ri].Add(p)) { listen[ri].Add(pi); eintraege++; }
                }
            }
            Netz.RaeumeAnlegen(raeume.Count, eintraege);
            Netz.Raeume = raeume.ToArray();
            Netz.Index.Clear();
            int k = 0;
            for (int ri = 0; ri < raeume.Count; ri++)
            {
                var raum = raeume[ri];
                Netz.Index[raum] = ri;
                Netz.RaumId[ri] = raum.ID; Netz.Draussen[ri] = raum.IsOutdoor;
                Netz.PStart[ri] = k; Netz.PAnz[ri] = listen[ri].Count;
                foreach (int pi in listen[ri]) Netz.PListe[k++] = pi;
            }
            Plugin.Log.LogInfo($"[Diagnostics] Room paths (physics): room network built - {max} portals, {raeume.Count} rooms, {eintraege} openings, " +
                               $"{Laufzeit.Ms(Laufzeit.Jetzt - t0):F1} ms (once per raid).");
        }

        static V3 V(Vector3 v) => new V3(v.x, v.y, v.z);

        static string Kette(float spiel, float dyn, RaumwegErgebnis e, int n, int routenGesamt, int routenGerechnet)
        {
            var inv = CultureInfo.InvariantCulture;
            var sb = new StringBuilder(160);
            sb.Append(" | room path: game cost ").Append(spiel.ToString("F2", inv)).Append(" (=").Append((spiel * dyn).ToString("F1", inv))
              .Append(" dB) -> loudest route ").Append(e.Db.ToString("F1", inv)).Append(" dB [open ").Append(e.DbGeo.ToString("F1", inv))
              .Append(", rooms ").Append(e.DbDiffus.ToString("F1", inv)).Append(" (4k ").Append(e.DbDiffusHoch.ToString("F1", inv)).Append(")").Append(", doors ").Append(e.DbTuer.ToString("F1", inv))
              .Append("] length ").Append(e.Laenge.ToString("F1", inv)).Append('/').Append(e.Direkt.ToString("F1", inv)).Append(" m, ")
              .Append(routenGerechnet).Append('/').Append(routenGesamt).Append(" routes; source ").Append(Netz.BestR1.ToString("F1", inv))
              .Append(" m from 1st opening (").Append((Netz.BestOmega * 100).ToString("F1", inv)).Append(" % of its power); rooms A/V:");
            for (int i = 1; i <= n; i++)
                sb.Append(i == 1 ? " " : ", ").Append(Netz.BestA[i] > 0 ? Netz.BestA[i].ToString("F0", inv) + " m2/" + Netz.BestV[i].ToString("F0", inv) + " m3" : "outdoors");
            sb.Append("; portals:");
            for (int i = 0; i < n; i++)
            {
                var p = Netz.BestIdx[i] < portalJeIndex.Length ? portalJeIndex[Netz.BestIdx[i]] : null;
                sb.Append(i == 0 ? " " : " > ").Append(p != null ? (string.IsNullOrEmpty(p.portalName) ? p.name : p.portalName) : "").Append('#').Append(Netz.BestIdx[i]);
                sb.Append('(');
                if (p != null) sb.Append(p.portalType).Append(' ').Append(p.state).Append(' ');
                sb.Append("open ").Append(Netz.BestOffen[i].ToString("F2", inv));
                if (Netz.BestOffen[i] < 0.999) sb.Append(" door ").Append(Netz.BestTauDb[i].ToString("F0", inv)).Append(" dB");
                if (p != null) { var g = p.GetPortalSize(); sb.Append(' ').Append((g.x * g.y).ToString("F1", inv)).Append(" m2"); }
                sb.Append(Netz.BestUm[i] >= 0 ? " detour " : " clear ").Append(Math.Abs(Netz.BestUm[i]).ToString("F2", inv)).Append(" m ")
                  .Append(Netz.BestBeug[i].ToString("F1", inv)).Append(" dB)");
            }
            return sb.ToString();
        }
    }
}
