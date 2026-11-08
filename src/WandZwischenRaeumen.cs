using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;
using Audio.SpatialSystem;
using BepInEx.Configuration;
using HarmonyLib;
using Unity.Collections;
using Unity.Jobs;
using UnityEngine;

namespace Materialklang
{
    // Vanilla: sind Quelle und Hoerer in verschiedenen Audio-Raeumen, entscheiden nur die vorberechneten
    // Portal-Wege (Tueren/Fenster) - die Wand dazwischen zaehlt nicht. Hier wird zusaetzlich die gerade Linie
    // Quelle -> Ohr durch die Waende gemessen (5 Linien, jede in beide Richtungen) und das Ergebnis genommen,
    // das weniger daempft: dort, wo der Schall also wirklich "durch die Wand" leichter durchkommt als ueber die Tuer.
    // Die Strahlen laufen asynchron (RaycastCommand-Batch); nie wird auf einen unfertigen Auftrag gewartet.
    internal static class WandZwischenRaeumen
    {
        internal static ConfigEntry<bool> Aktiv;
        internal static ConfigEntry<float> MaxDist, Zuschlag, Schale;

        const int Linien = 5, Strahlen = Linien * 2, MaxTreffer = 8;
        const float Kreis = 0.25f, NeuBewegung = 0.3f, NeuAlter = 2f, MinDist = 0.5f, MinWert = 0.02f;
        const int MaxStartsProBild = 8;
        static readonly float[] Gewicht = { 0.4f, 0.15f, 0.15f, 0.15f, 0.15f };

        sealed class WandWeg
        {
            public WeakReference<ParallelSoundPropagationCalculator> Besitzer;
            public NativeArray<RaycastCommand> Befehle;
            public NativeArray<RaycastHit> Treffer;
            public JobHandle Handle;
            public bool Laeuft;
            public bool Hat;               // es gibt eine Messung (gestartet) zum Vergleichen
            public float Score = -1f;      // Wanddaempfung 0..1, -1 = keine Aussage (Vanilla gilt)
            public Vector3 PA, PB;         // Positionen beim Messen
            public float Laenge, Zeit;
            public short RA = -2, RB = -2;
            public int Schmutz;
            // Klang-Log: Stuecke der mittleren Linie (nur gefuellt, solange das Log an ist)
            public int LogAnzahl;
            public readonly int[] LogGruppe = new int[LogMax];
            public readonly float[] LogDicke = new float[LogMax], LogDb = new float[LogMax];
            public readonly int[] LogId = new int[LogMax];
        }
        const int LogMax = 6;

        struct Ereignis { public float t; public int id; }

        static readonly ConditionalWeakTable<ParallelSoundPropagationCalculator, WandWeg> Tabelle =
            new ConditionalWeakTable<ParallelSoundPropagationCalculator, WandWeg>();
        static readonly List<WandWeg> Alle = new List<WandWeg>();
        static readonly Dictionary<int, bool> Gelaende = new Dictionary<int, bool>();
        static readonly Ereignis[] ev = new Ereignis[2 * MaxTreffer];
        static readonly bool[] benutzt = new bool[2 * MaxTreffer];
        static readonly Vector3[] versatz = new Vector3[Linien];

        static int hauptThread, schmutz, budgetBild, budgetRest;
        static WandWeg logW; static int logLinie;
        // Diagnose
        static int gestartet, gemessen, vergleiche, wandGewann, gesperrt, voll, ohneBudget;
        static double summeRoute, summeWand;

        /// <summary>Live-Aenderung: alle Wandmessungen gelten als veraltet und werden neu gestartet.</summary>
        internal static void Neu() { schmutz++; }

        /// <summary>Klang-Log: Wertung der Wand und die Stuecke der mittleren Linie (false, wenn es keine Messung gibt).</summary>
        internal static bool LogDaten(ParallelSoundPropagationCalculator c, LogEintrag e, Func<int, string> name)
        {
            if (c == null || !Tabelle.TryGetValue(c, out var w) || w.Score < 0f) return false;
            e.WertWand = w.Score;
            e.WandAnzahl = 0;
            for (int i = 0; i < w.LogAnzahl && i < LogEintrag.MaxStuecke; i++)
            {
                var st = e.Wand[i];
                st.Gruppe = w.LogGruppe[i]; st.Dicke = w.LogDicke[i]; st.Db = w.LogDb[i]; st.Strahlen = 1; st.Name = name != null ? name(w.LogId[i]) : null;
                e.WandAnzahl++;
            }
            return true;
        }

        internal static void Binden(ConfigFile c)
        {
            const string s = "5. Walls Between Rooms";
            hauptThread = Thread.CurrentThread.ManagedThreadId;
            Aktiv = c.Bind(s, "Enabled", true,
                "Sound between different rooms can also pass through the wall (vanilla: only via doors/windows). Off = vanilla.");
            MaxDist = c.Bind(s, "Max. distance (m)", 40f,
                new ConfigDescription("Sources farther away are not measured through the wall (the vanilla value applies).", new AcceptableValueRange<float>(5f, 100f)));
            Zuschlag = c.Bind(s, "Surcharge", 0.05f,
                new ConfigDescription("Added to the measured wall attenuation: residual attenuation even with a completely free line. Ignored while Physics Mode is on.", new AcceptableValueRange<float>(0f, 0.5f)));
            Schale = c.Bind(s, "Shell thickness (m)", 0.15f,
                new ConfigDescription("Assumed thickness of a single surface without a back face; wall pieces over 1.5 m count as two such shells.", new AcceptableValueRange<float>(0.02f, 1f)));
        }

        internal static void Anwenden(Harmony h)
        {
            try
            {
                var t = typeof(ParallelSoundPropagationCalculator);
                var update = AccessTools.DeclaredMethod(t, "OnUpdate");
                var spaet = AccessTools.DeclaredMethod(t, "OnLateUpdate");
                var score = AccessTools.DeclaredMethod(t, "GetScore");
                var reset = AccessTools.DeclaredMethod(t, "OnReset");
                var weg = AccessTools.DeclaredMethod(t, "OnDispose");
                var swap = AccessTools.DeclaredMethod(typeof(SpatialAudioDataContainer), "SwapPortalBuffers");
                if (update == null || spaet == null || score == null || reset == null || weg == null || swap == null)
                {
                    Plugin.Log.LogWarning("Walls between rooms: target in ParallelSoundPropagationCalculator not found (game update?) -> vanilla.");
                    return;
                }
                h.Patch(update, postfix: new HarmonyMethod(typeof(WandZwischenRaeumen), nameof(UpdatePostfix)));
                h.Patch(spaet, postfix: new HarmonyMethod(typeof(WandZwischenRaeumen), nameof(SpaetPostfix)));
                h.Patch(score, postfix: new HarmonyMethod(typeof(WandZwischenRaeumen), nameof(ScorePostfix)));
                h.Patch(reset, postfix: new HarmonyMethod(typeof(WandZwischenRaeumen), nameof(ResetPostfix)));
                h.Patch(weg, postfix: new HarmonyMethod(typeof(WandZwischenRaeumen), nameof(DisposePostfix)));
                h.Patch(swap, postfix: new HarmonyMethod(typeof(WandZwischenRaeumen), nameof(SwapPostfix)));
                Plugin.Log.LogInfo("Walls between rooms: hooked.");
            }
            catch (Exception e) { Statistik.Fehler(e); }
        }

        // Szenenwechsel: alle Auftraege abschliessen, Speicher freigeben, Zustand vergessen.
        internal static void Leeren()
        {
            try
            {
                Gelaende.Clear();
                for (int i = Alle.Count - 1; i >= 0; i--)
                {
                    var w = Alle[i];
                    Freigeben(w);
                    Zuruecksetzen(w);
                    if (!w.Besitzer.TryGetTarget(out _)) Alle.RemoveAt(i);
                }
            }
            catch (Exception e) { Statistik.Fehler(e); }
        }

        internal static void DiagnoseLoggen()
        {
            if (gestartet == 0 && vergleiche == 0) return;
            string verg = vergleiche > 0
                ? $"route via doors on average {summeRoute / vergleiche:F2}, wall {summeWand / vergleiche:F2}, wall won {wandGewann}x of {vergleiche}"
                : "no comparison yet";
            Plugin.Log.LogInfo($"[Diagnostics] Walls: {gestartet} measurements started, {gemessen} finished, {verg}; " +
                               $"{voll} lines blocked (permanent hits/hill), {ohneBudget}x postponed due to the per-frame budget.");
            gestartet = gemessen = vergleiche = wandGewann = gesperrt = voll = ohneBudget = 0;
            summeRoute = summeWand = 0;
        }

        // ---------------------------------------------------------------- Zustand

        static WandWeg Hole(ParallelSoundPropagationCalculator c)
        {
            return Tabelle.GetValue(c, k =>
            {
                var w = new WandWeg { Besitzer = new WeakReference<ParallelSoundPropagationCalculator>(k) };
                Alle.Add(w);
                return w;
            });
        }

        static void Zuruecksetzen(WandWeg w) { w.Score = -1f; w.Hat = false; w.Laeuft = false; }

        // Auftrag zu Ende laufen lassen (nur fuer Reset/Dispose/Szenenwechsel erlaubt) und Puffer freigeben.
        static void Freigeben(WandWeg w)
        {
            if (w.Laeuft) { w.Handle.Complete(); w.Laeuft = false; }
            if (w.Befehle.IsCreated) w.Befehle.Dispose();
            if (w.Treffer.IsCreated) w.Treffer.Dispose();
        }

        // ---------------------------------------------------------------- Harmony

        static void SwapPostfix() { schmutz++; }   // Portaldaten wurden getauscht (Tuer auf/zu, Fenster kaputt)

        static void UpdatePostfix(ParallelSoundPropagationCalculator __instance, AudioSourceContainer sourceContainer,
            Vector3 listenerPosition, bool forceUpdate)
        {
            if (!Aktiv.Value) return;
            try
            {
                var w = Hole(__instance);
                if (w.Laeuft)
                {
                    if (w.Handle.IsCompleted) Beenden(__instance, w);
                    if (w.Laeuft) return;
                }

                var sys = __instance._audioSystem;
                var lr = sys?.ListenerCurrentRoom;
                var qr = sourceContainer.CurrentAudioRoom;
                if (lr == null || qr == null) { Zuruecksetzen(w); return; }

                // gleicher Raum / gemeinsames Aussengebiet: Vanilla-Hinderniswerte (Propagation gar nicht aktiv)
                if (sys.IsSourceInListenerRoom(sourceContainer) || sys.IsSourceAndListenerInCommonOutdoor(qr))
                { w.Score = -1f; w.Hat = false; return; }

                Vector3 a = sourceContainer.CurrentPosition, b = listenerPosition;
                float dist2 = (b - a).sqrMagnitude, max = MaxDist.Value;
                if (dist2 > max * max || dist2 < MinDist * MinDist || dist2 > sourceContainer.SqrMaxDistance)
                { w.Score = -1f; w.Hat = false; return; }

                bool neu = !w.Hat || forceUpdate || w.Schmutz != schmutz || w.RA != qr.ID || w.RB != lr.ID
                           || (a - w.PA).sqrMagnitude > NeuBewegung * NeuBewegung
                           || (b - w.PB).sqrMagnitude > NeuBewegung * NeuBewegung
                           || Time.time - w.Zeit > NeuAlter;
                if (!neu) return;

                if (Time.frameCount != budgetBild) { budgetBild = Time.frameCount; budgetRest = MaxStartsProBild; }
                if (budgetRest <= 0) { ohneBudget++; return; }
                budgetRest--;

                Starten(w, a, b, qr.ID, lr.ID);
            }
            catch (Exception e) { Statistik.Fehler(e); }
        }

        static void SpaetPostfix(ParallelSoundPropagationCalculator __instance)
        {
            if (!Aktiv.Value) return;
            try
            {
                if (Tabelle.TryGetValue(__instance, out var w) && w.Laeuft && w.Handle.IsCompleted) Beenden(__instance, w);
            }
            catch (Exception e) { Statistik.Fehler(e); }
        }

        static void ScorePostfix(ParallelSoundPropagationCalculator __instance, ref float __result)
        {
            if (__result <= 0f) return;                 // gleicher Raum -> nichts zu tun
            try
            {
                if (!Aktiv.Value || !Tabelle.TryGetValue(__instance, out var w) || w.Score < 0f) return;
                // Untergrenze, damit "Propagation aktiv" (Effekt > 0) erhalten bleibt
                __result = Mathf.Max(MinWert, Mathf.Min(__result, w.Score));
            }
            catch (Exception e) { Statistik.Fehler(e); }
        }

        static void ResetPostfix(ParallelSoundPropagationCalculator __instance)
        {
            try
            {
                if (Thread.CurrentThread.ManagedThreadId != hauptThread) return;
                if (Tabelle.TryGetValue(__instance, out var w))
                {
                    if (w.Laeuft) { w.Handle.Complete(); }
                    Zuruecksetzen(w);
                }
            }
            catch (Exception e) { Statistik.Fehler(e); }
        }

        static void DisposePostfix(ParallelSoundPropagationCalculator __instance)
        {
            try
            {
                // Der Finalizer ruft Dispose aus einem Fremd-Thread: dort nichts anfassen, der Szenenwechsel raeumt auf.
                if (Thread.CurrentThread.ManagedThreadId != hauptThread) return;
                if (Tabelle.TryGetValue(__instance, out var w))
                {
                    Freigeben(w);
                    Zuruecksetzen(w);
                    Alle.Remove(w);
                }
            }
            catch (Exception e) { Statistik.Fehler(e); }
        }

        // ---------------------------------------------------------------- Messen

        static void Starten(WandWeg w, Vector3 a, Vector3 b, short ra, short rb)
        {
            if (!w.Befehle.IsCreated)
            {
                w.Befehle = new NativeArray<RaycastCommand>(Strahlen, Allocator.Persistent);
                w.Treffer = new NativeArray<RaycastHit>(Strahlen * MaxTreffer, Allocator.Persistent);
            }
            Vector3 d = b - a;
            float len = d.magnitude;
            d /= len;
            Vector3 u = Vector3.Cross(d, Vector3.up);
            if (u.sqrMagnitude < 0.01f) u = Vector3.Cross(d, Vector3.right);
            u.Normalize();
            Vector3 v = Vector3.Cross(d, u);
            versatz[0] = Vector3.zero;
            versatz[1] = u * Kreis; versatz[2] = -u * Kreis;
            versatz[3] = v * Kreis; versatz[4] = -v * Kreis;

            int maske = (int)LayersMaskController.HighPolyWithTerrainMask | Statistik.AudioMaske;
            var qp = new QueryParameters(maske, true, QueryTriggerInteraction.Ignore, true);
            for (int k = 0; k < Linien; k++)
            {
                w.Befehle[2 * k] = new RaycastCommand(a + versatz[k], d, qp, len);
                w.Befehle[2 * k + 1] = new RaycastCommand(b + versatz[k], -d, qp, len);
            }
            w.Handle = RaycastCommand.ScheduleBatch(w.Befehle, w.Treffer, 1, MaxTreffer);
            JobHandle.ScheduleBatchedJobs();
            w.Laeuft = true; w.Hat = true;
            w.PA = a; w.PB = b; w.Laenge = len; w.Zeit = Time.time; w.RA = ra; w.RB = rb; w.Schmutz = schmutz;
            gestartet++;
        }

        static void Beenden(ParallelSoundPropagationCalculator c, WandWeg w)
        {
            w.Handle.Complete();      // fertig laut IsCompleted -> blockiert nicht
            w.Laeuft = false;
            float staerke = Plugin.Gesamtstaerke.Value, schale = Schale.Value, summe = 0f;
            w.LogAnzahl = 0;
            logW = KlangLog.An ? w : null;
            for (int k = 0; k < Linien; k++) { logLinie = k; summe += Gewicht[k] * Linie(w, k, staerke, schale); }
            logW = null;
            float wand = Physik.An ? Physik.WertungAusEnergie(summe) : Mathf.Clamp01(1f - summe + Zuschlag.Value);
            w.Score = wand;
            gemessen++;
            float route = c._propagationScore;
            if (route > 0f)
            {
                vergleiche++; summeRoute += route; summeWand += wand;
                if (wand < route) wandGewann++;
            }
        }

        // Restenergie entlang einer Linie (beide Richtungen zusammen), 0 = voellig dicht.
        static float Linie(WandWeg w, int k, float staerke, float schale)
        {
            float len = w.Laenge;
            int n = 0;
            for (int dir = 0; dir < 2; dir++)
            {
                int basis = (2 * k + dir) * MaxTreffer, anzahl = 0;
                for (int j = 0; j < MaxTreffer; j++)
                {
                    RaycastHit h = w.Treffer[basis + j];
                    int id = h.colliderInstanceID;
                    if (id == 0) break;
                    anzahl++;
                    ev[n].id = id;
                    ev[n].t = dir == 0 ? h.distance : len - h.distance;
                    n++;
                }
                if (anzahl >= MaxTreffer) { voll++; return 0f; }   // Strahl voll -> nicht verlaesslich, wie dicht
            }

            // nach Entfernung sortieren (n <= 16)
            for (int i = 1; i < n; i++)
            {
                var x = ev[i]; int j = i - 1;
                while (j >= 0 && ev[j].t > x.t) { ev[j + 1] = ev[j]; j--; }
                ev[j + 1] = x;
            }
            // dieselbe Flaeche aus beiden Richtungen gesehen -> einmal zaehlen
            int m = 0;
            for (int i = 0; i < n; i++)
            {
                bool doppelt = false;
                for (int j = m - 1; j >= 0 && ev[i].t - ev[j].t < 0.01f; j--)
                    if (ev[j].id == ev[i].id) { doppelt = true; break; }
                if (!doppelt) ev[m++] = ev[i];
            }

            // je Collider Ein-/Austritt paaren -> Wandstuecke
            float sum = 0f;
            Array.Clear(benutzt, 0, m);
            for (int i = 0; i < m; i++)
            {
                if (benutzt[i]) continue;
                benutzt[i] = true;
                int id = ev[i].id;
                float start = ev[i].t;
                bool offen = true;
                for (int j = i + 1; j < m; j++)
                {
                    if (benutzt[j] || ev[j].id != id) continue;
                    benutzt[j] = true;
                    if (offen)
                    {
                        if (!Beitrag(id, ev[j].t - start, schale, ref sum)) { gesperrt++; return 0f; }
                        offen = false;
                    }
                    else { start = ev[j].t; offen = true; }
                }
                if (offen && !Beitrag(id, schale, schale, ref sum)) { gesperrt++; return 0f; }   // einzelne Flaeche
            }
            if (Physik.An) return Physik.EnergieAusDb(Mathf.Min(sum, Physik.Dynamik.Value));
            return Mathf.Exp(-sum * staerke);
        }

        // false = Linie gilt als dicht (Huegel). Sonst wird die Daempfung des Stuecks auf x addiert.
        static bool Beitrag(int id, float dicke, float schale, ref float x)
        {
            bool gelaende = IstGelaende(id);
            if (gelaende && dicke > 3f) return false;
            if (!gelaende && dicke > 1.5f) dicke = 2f * schale;     // wohl zwei getrennte Flaechen eines grossen Meshes
            int g = MaterialCache.Get(id);
            if (g == (int)Gruppe.Body) return true;              // Spieler/Leichen zaehlen nicht als Wand
            if (Physik.An)
            {
                float db = Physik.Daemmung(g, Mathf.Max(dicke, 0.02f));
                x += db;
                LogStueck(id, g, dicke, db);
                return true;
            }
            float s = Plugin.Oberflaeche[g].Value;
            if (g == (int)Gruppe.Door)
            {
                float ts = TuerenFenster.TuerS(id);
                if (ts >= 0f) s = ts;
            }
            float dd = Mathf.Max(dicke, 0.02f);
            x += s + Plugin.Dicke[g].Value * dd;
            LogStueck(id, g, dicke, (s + Plugin.Dicke[g].Value * dd) * Plugin.Gesamtstaerke.Value * 4.3429f);
            return true;
        }

        // Klang-Log: nur die mittlere Linie (Gewicht 0.4) wird mitgeschrieben
        static void LogStueck(int id, int g, float dicke, float db)
        {
            var w = logW;
            if (w == null || logLinie != 0 || w.LogAnzahl >= LogMax) return;
            int i = w.LogAnzahl++;
            w.LogId[i] = id; w.LogGruppe[i] = g; w.LogDicke[i] = dicke; w.LogDb[i] = db;
        }

        static bool IstGelaende(int id)
        {
            if (Gelaende.TryGetValue(id, out bool b)) return b;
            b = Resources.InstanceIDToObject(id) is TerrainCollider;
            Gelaende[id] = b;
            return b;
        }
    }
}
