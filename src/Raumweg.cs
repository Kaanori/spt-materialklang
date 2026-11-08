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
        }

        static readonly ConditionalWeakTable<ParallelSoundPropagationCalculator, Zustand> Tabelle =
            new ConditionalWeakTable<ParallelSoundPropagationCalculator, Zustand>();
        static readonly ConditionalWeakTable<ParallelSoundPropagationCalculator, Zustand>.CreateValueCallback Neu = _ => new Zustand();

        // Kontext: welcher Rechner gehoert zum Klang, der gerade angewendet wird
        internal static ParallelSoundPropagationCalculator Multi;     // MultiSource: OnUpdateOcclusionEffects
        static SpatialAudioOccluder applyOcc;

        // Diagnose
        static int gerechnet, routen, ohneDaten;
        static double summeSpiel, summeNeu;
        static float letzteUmwelt = float.NaN;

        internal static bool An => Physik.An && Aktiv != null && Aktiv.Value;

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

        static void SpaetNach(ParallelSoundPropagationCalculator __instance, bool __state)
        {
            if (!__state) return;
            try
            {
                if (!Tabelle.TryGetValue(__instance, out var z) || !z.Hat) return;
                float spiel = __instance._propagationScore;
                if (!Rechnen(__instance, z, spiel)) { ohneDaten++; z.Wertung = -1f; return; }
                z.Spiel = spiel;
                __instance._propagationScore = z.Wertung;
                gerechnet++; summeSpiel += spiel * Physik.Dynamik.Value; summeNeu += z.Wertung * Physik.Dynamik.Value;
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
        static void ApplyVor(SpatialAudioOccluder __instance) { applyOcc = __instance; }
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
        static Exception ApplyEnde(Exception __exception) { applyOcc = null; return __exception; }

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
        internal static bool Tiefpass(out float beugung, out float wertungTuer)
        {
            beugung = 22000f; wertungTuer = 0f;
            try
            {
                var occ = applyOcc;
                var z = Gilt(occ, out _);
                if (z == null || occ.PropagationEffect < z.Wertung - 1e-4f) return false;    // Wand zwischen Raeumen gewann
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
                   + "; rolloff x" + occ.CurrentParameters.OcclusionRolloffScale.ToString("F2", inv);
        }

        internal static void Loggen()
        {
            if (gerechnet + ohneDaten == 0) return;
            Plugin.Log.LogInfo($"[Diagnostics] Room paths (physics): {gerechnet} routes recalculated ({routen} candidates), game route cost read as insulation on average " +
                               $"{(gerechnet > 0 ? summeSpiel / gerechnet : 0):F1} dB -> physical {(gerechnet > 0 ? summeNeu / gerechnet : 0):F1} dB, {ohneDaten} without data (game value kept)" +
                               (float.IsNaN(letzteUmwelt) ? "." : $", indoor/outdoor factor of the sound group ignored (last x{letzteUmwelt:F2})."));
            gerechnet = routen = ohneDaten = 0; summeSpiel = summeNeu = 0;
        }

        // ---------------------------------------------------------------- Rechnung

        static SpatialAudioDataContainer letzterContainer;
        static BaseSpatialAudioPortal[] portalJeIndex = new BaseSpatialAudioPortal[0];
        static int portalAnzahl = -1;
        static readonly Dictionary<int, double> FlaecheJeRaum = new Dictionary<int, double>();

        static RaumPortal[] rp = new RaumPortal[16];
        static V3[] pkt = new V3[16], pktJob = new V3[16];
        static double[] raumA = new double[17], beug = new double[16], umweg = new double[16];
        static int[] idx = new int[16], bestIdx = new int[16];
        static double[] bestBeug = new double[16], bestUm = new double[16], bestOffen = new double[16], bestTauDb = new double[16];

        static bool Rechnen(ParallelSoundPropagationCalculator c, Zustand z, float spiel)
        {
            var s = c._propagationJobScheduler;
            if (s == null || !s._routesSettingsData.IsCreated || !s._routesPortalIndicesData.IsCreated || !s._staticPortalDataList.IsCreated) return false;
            if (!DataProvider.TryGetData<SpatialAudioDataContainer>(out var data) || data == null) return false;
            PortalTabelle(data);
            var routes = s._routesSettingsData; var indizes = s._routesPortalIndicesData; var liste = s._staticPortalDataList;
            int len = routes.Length;
            if (len == 0) return false;
            int anzahl = Mathf.Min(len, Mathf.Max(Mathf.Min(350, len), Mathf.CeilToInt(len * c._qualityFactor)));   // wie Schedule
            float dyn = Physik.Dynamik.Value, alpha = Alpha.Value;
            double fRef = Physik.Frequenz.Value;
            V3 a = V(z.A), b = V(z.B);
            V3 quelle = z.Umgekehrt ? b : a, hoerer = z.Umgekehrt ? a : b;
            bool hat = false; RaumwegErgebnis best = default; int bestN = 0; int bestRoute = -1;
            short raumAId = z.RaumA != null ? z.RaumA.ID : (short)-1;

            for (int r = 0; r < anzahl; r++)
            {
                var rd = routes[r];
                int n = rd.PortalDataLength;
                if (n <= 0 || n > rp.Length || rd.StartIndex + n > indizes.Length) continue;
                // Portalpunkte genau wie der Job (Richtung A -> B, Austritt immer B)
                V3 ein = a; int m = 0;
                short vorRaum = raumAId; bool vorDraussen = z.RaumA != null && z.RaumA.IsOutdoor;
                bool ok = true;
                for (int i = 0; i < n; i++)
                {
                    int pi = indizes[rd.StartIndex + i];
                    if (pi < 0 || pi >= liste.Length) { ok = false; break; }
                    var d = liste[pi];
                    var p = new RaumPortal
                    {
                        Mitte = new V3(d.center.x, d.center.y, d.center.z),
                        Rechts = new V3(d.portalRight.x, d.portalRight.y, d.portalRight.z),
                        Oben = new V3(d.portalUp.x, d.portalUp.y, d.portalUp.z),
                        Normale = new V3(d.portalNormal.x, d.portalNormal.y, d.portalNormal.z),
                        HalbX = d.portalHalfSize.x, HalbY = d.portalHalfSize.y,
                    };
                    V3 punkt = RaumwegKern.PortalPunkt(p, ein, b);
                    if (punkt.IstNull) continue;
                    // Zustand: offener Anteil aus der Schliessung des Spiels, geschlossener Teil mit dem Wert der Mod (Abschnitt 4)
                    var portal = pi < portalJeIndex.Length ? portalJeIndex[pi] : null;
                    float wand = WandFaktor(d);
                    double zu;
                    if (portal != null)
                    {
                        float voll = Mathf.Lerp(1f, 0.1f, portal._transmission);
                        zu = voll > 0.001f ? Mathf.Clamp01(portal.PortalClosureLevel / voll) : 0f;
                    }
                    else zu = d.closureLevel > 0.001f ? 1.0 : 0.0;
                    double tlDb = zu > 1e-3 ? Math.Min(dyn, d.closureLevel / zu * wand * dyn) : 0.0;
                    p.Offen = 1.0 - zu;
                    p.Tau = Math.Pow(10, -tlDb / 10);
                    rp[m] = p; pktJob[m] = punkt; idx[m] = pi;
                    // Raum hinter dem Portal (in Job-Richtung)
                    raumA[m + 1] = RaumNach(portal, ref vorRaum, ref vorDraussen, alpha);
                    ein = punkt; m++;
                }
                if (!ok || m == 0) continue;
                raumA[0] = RaumFlaeche(z.RaumA, alpha);
                raumA[m] = RaumFlaeche(z.RaumB, alpha);
                // offene Flaechen der Route schlucken im jeweiligen Raum mit
                for (int i = 0; i < m; i++)
                {
                    double off = rp[i].Flaeche * rp[i].Offen;
                    if (raumA[i] > 0) raumA[i] += off;
                    if (raumA[i + 1] > 0) raumA[i + 1] += off;
                }
                // in echte Richtung (Quelle -> Hoerer) bringen
                if (z.Umgekehrt) Umdrehen(m);
                for (int i = 0; i < m; i++) pkt[i] = pktJob[i];
                var e = RaumwegKern.Route(quelle, hoerer, rp, pkt, m, raumA, fRef, FHoch, dyn, beug, umweg);
                routen++;
                if (!e.Gueltig) continue;
                if (!hat || e.Db < best.Db)
                {
                    hat = true; best = e; bestN = m; bestRoute = r;
                    if (KlangLog.An)
                        for (int i = 0; i < m; i++) { bestIdx[i] = idx[i]; bestBeug[i] = beug[i]; bestUm[i] = umweg[i]; bestOffen[i] = rp[i].Offen; bestTauDb[i] = -10 * Math.Log10(Math.Max(rp[i].Tau, 1e-12)); }
                }
            }
            if (!hat) return false;
            float w = Mathf.Clamp((float)(best.Db / dyn), MinWertung, 1f);
            z.Wertung = w;
            z.WertungTuer = Mathf.Clamp01((float)(best.DbTuer / dyn));
            z.TiefpassBeugung = (float)RaumwegKern.Tiefpass(best.DbHoch - best.Db, FHoch, Physik.TiefpassMin.Value);
            z.Kette = KlangLog.An ? Kette(spiel, dyn, best, bestN, len, anzahl) : null;
            return true;
        }

        static void Umdrehen(int m)
        {
            Array.Reverse(rp, 0, m); Array.Reverse(pktJob, 0, m); Array.Reverse(idx, 0, m);
            Array.Reverse(raumA, 0, m + 1);
        }

        // Wie PropagationJob: ein Raum draussen -> Wert des Innenraums, sonst Mittel.
        static float WandFaktor(in RoomPair.AudioPortalData d)
        {
            if (d.isFrontRoomOutdoor != d.isBackRoomOutdoor) return d.isFrontRoomOutdoor ? d.backRoomWallOcclusion : d.frontRoomWallOcclusion;
            return (d.frontRoomWallOcclusion + d.backRoomWallOcclusion) * 0.5f;
        }

        // Raum hinter dem Portal: der andere als der davor. Passt keiner (z. B. anderes Aussengebiet), der mit dem anderen
        // Drinnen/Draussen-Zustand.
        static double RaumNach(BaseSpatialAudioPortal p, ref short vorRaum, ref bool vorDraussen, float alpha)
        {
            if (p == null) return -1;
            var f = p.FrontRoom; var bk = p.BackRoom;
            SpatialAudioRoom nach;
            if (f != null && f.ID == vorRaum) nach = bk;
            else if (bk != null && bk.ID == vorRaum) nach = f;
            else if (f != null && bk != null) nach = f.IsOutdoor != vorDraussen ? f : bk;
            else nach = bk ?? f;
            if (nach == null) return -1;
            vorRaum = nach.ID; vorDraussen = nach.IsOutdoor;
            return RaumFlaeche(nach, alpha);
        }

        /// <summary>Absorptionsflaeche (ohne Oeffnungen) eines Innenraums, -1 = im Freien/unbekannt.</summary>
        static double RaumFlaeche(ISpatialAudioRoom raum, float alpha)
        {
            if (raum == null || raum.IsOutdoor) return -1;
            var sr = raum as SpatialAudioRoom;
            if (sr == null) return -1;
            int key = sr.GetInstanceID();
            if (!FlaecheJeRaum.TryGetValue(key, out double flaeche))
            {
                flaeche = 0;
                var cols = sr.Colliders;
                if (cols != null)
                    foreach (var bc in cols)
                    {
                        if (bc == null) continue;
                        Vector3 g = Vector3.Scale(bc.size, bc.transform.lossyScale);
                        g = new Vector3(Mathf.Abs(g.x), Mathf.Abs(g.y), Mathf.Abs(g.z));
                        flaeche += 2.0 * (g.x * g.y + g.y * g.z + g.z * g.x);
                    }
                if (flaeche <= 0)
                {
                    Vector3 g = sr.Bounds.size;
                    flaeche = 2.0 * (g.x * g.y + g.y * g.z + g.z * g.x);
                }
                FlaecheJeRaum[key] = flaeche;
            }
            return flaeche > 0 ? RaumwegKern.Absorption(flaeche, alpha, 0) : -1;
        }

        static void PortalTabelle(SpatialAudioDataContainer data)
        {
            var map = data._portalDataBufferService?._indexMap;
            if (map == null) return;
            if (ReferenceEquals(data, letzterContainer) && map.Count == portalAnzahl) return;
            if (!ReferenceEquals(data, letzterContainer)) FlaecheJeRaum.Clear();
            letzterContainer = data; portalAnzahl = map.Count;
            int max = 0;
            foreach (var kv in map) max = Math.Max(max, kv.Value + 1);
            portalJeIndex = new BaseSpatialAudioPortal[max];
            foreach (var kv in map)
                if (data.PortalsByID.TryGetValue(kv.Key, out var p)) portalJeIndex[kv.Value] = p;
        }

        static V3 V(Vector3 v) => new V3(v.x, v.y, v.z);

        static string Kette(float spiel, float dyn, RaumwegErgebnis e, int n, int routenGesamt, int routenGerechnet)
        {
            var inv = CultureInfo.InvariantCulture;
            var sb = new StringBuilder(160);
            sb.Append(" | room path: game cost ").Append(spiel.ToString("F2", inv)).Append(" (=").Append((spiel * dyn).ToString("F1", inv))
              .Append(" dB) -> physical ").Append(e.Db.ToString("F1", inv)).Append(" dB [open ").Append(e.DbGeo.ToString("F1", inv))
              .Append(", rooms ").Append(e.DbDiffus.ToString("F1", inv)).Append(", doors ").Append(e.DbTuer.ToString("F1", inv))
              .Append("] length ").Append(e.Laenge.ToString("F1", inv)).Append('/').Append(e.Direkt.ToString("F1", inv)).Append(" m, ")
              .Append(routenGerechnet).Append('/').Append(routenGesamt).Append(" routes; portals:");
            for (int i = 0; i < n; i++)
            {
                var p = bestIdx[i] < portalJeIndex.Length ? portalJeIndex[bestIdx[i]] : null;
                sb.Append(i == 0 ? " " : " > ").Append(p != null ? (string.IsNullOrEmpty(p.portalName) ? p.name : p.portalName) : "#" + bestIdx[i]);
                sb.Append('(');
                if (p != null) sb.Append(p.portalType).Append(' ').Append(p.state).Append(' ');
                sb.Append("open ").Append(bestOffen[i].ToString("F2", inv));
                if (bestOffen[i] < 0.999) sb.Append(" door ").Append(bestTauDb[i].ToString("F0", inv)).Append(" dB");
                if (p != null) { var g = p.GetPortalSize(); sb.Append(' ').Append((g.x * g.y).ToString("F1", inv)).Append(" m2"); }
                sb.Append(bestUm[i] >= 0 ? " detour " : " clear ").Append(Math.Abs(bestUm[i]).ToString("F2", inv)).Append(" m ")
                  .Append(bestBeug[i].ToString("F1", inv)).Append(" dB)");
            }
            return sb.ToString();
        }
    }
}
