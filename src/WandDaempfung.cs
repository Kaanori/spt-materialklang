using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using Audio.SpatialSystem;
using BepInEx.Configuration;
using Unity.Collections;
using Unity.Jobs;
using UnityEngine;

namespace Materialklang
{
    // ---------------------------------------------------------------------------------------------
    //  17. All fixed sources / Wall damping for non-occluded sources (0.8.5)
    //
    //  Problem: Ambient-Spieler (Abschnitt 15), rohe Szenenquellen und Fahrzeuge (17) sowie die Skriptquellen ohne Okklusion (18:
    //  Lok, Flugzeug, BTR-Turm/-Horn, Moerser-Pfeifen, Ausgangs-Tor) haben im Spiel keinen Occluder. Solange sie nach 3-20 m
    //  verstummten, fiel das kaum auf; mit den verlaengerten Reichweiten hoert man ein Lagerfeuer, das Taxi oder die Lok durch
    //  ganze Haeuser so laut wie im Freien.
    //  Weg (eigene, guenstige Rechnung - kein SourceOccluder/ProcessSourceOcclusion):
    //   - Messung wie "Wand zwischen Raeumen": 5 Linien Quelle -> Ohr (Mitte + 4 um 0,25 m versetzt), je Linie zwei Strahlen
    //     (RaycastCommand-Batch auf den Arbeitsfaeden, hoechstens 8 Treffer je Strahl), Schichten + Massengesetz je Band
    //     (250 Hz / 1 kHz / 4 kHz) mit Empfangsraum und Flanken (Wege.Korrigieren). Rechnung: WandDaempfungKern.Linie.
    //   - Umweg: Raumdaten des Spiels (AudioRoomStorage.FindActualCurrentRoom, RoomPair) -> gleicher Raum/draussen bzw. zwischen
    //     Raeumen, energetisch zur Wand summiert (WandDaempfungKern.Summe, Annahmen dort).
    //   - Wirkung: eigener Filter im Signalweg der Quelle (WandFilter: Lautstaerke x Faktor + 2-poliger Tiefpass, 300 ms
    //     Uebergang), angelegt erst, wenn eine Quelle wirklich gedaempft wird. AudioSource.volume bleibt dem Spiel.
    //   - Takt: hoechstens 'measurements per frame' neue Messungen je Bild (Standard 2), reihum, nur hoerbare Quellen
    //     (spielt, aktiv, innerhalb ihrer Reichweite); neu nach Bewegung > 0,5 m oder nach 'refresh (s)', ferne (> 60 m) 4x seltener.
    //     Nie wird auf einen unfertigen Auftrag gewartet (Ausnahme: Einmal-Klaenge, die sofort gemessen werden). Budget ms/Bild.
    //   - Lebensende: zerstoerte Quellen fallen heraus; Pool-Quellen (BetterSource.Release, Abschnitt 18) und abgeschaltete
    //     Abschnitte melden ab (Filter entfernt). Deaktivierte Objekte (V-Ex-Taxi nach Dispose) werden nicht gemessen.
    //   - Ausnahmen: 2D-Quellen, Objekte mit mehreren AudioSources (ein Skriptfilter waere fuer alle gemeinsam), bypassEffects.
    //     Flugzeug und Moerser-Pfeifen (in der Luft): nur das Stueck bis 40 m vor dem Ohr wird gemessen (Dach/Decke ueber dir).
    //   - Eigene Huelle: Flaechen naeher als 'ignore surfaces near the source' an der Quelle und die Collider des eigenen Fahrzeugs
    //     (V-Ex, BTR, Lok) bzw. einer kleinen Requisite (Elternobjekt < 8 m, <= 64 Collider) zaehlen nicht.
    // ---------------------------------------------------------------------------------------------
    internal static class WandDaempfung
    {
        internal static ConfigEntry<bool> Aktiv;
        internal static ConfigEntry<float> BudgetMs, Takt, UmwegDraussen, UmwegZwischen, NahQuelle;
        internal static ConfigEntry<int> ProBild;

        internal static bool Wirksam => Aktiv != null && Aktiv.Value && Physik.An && !gestoert;

        const int MaxProBild = 8, Strahlen = WandDaempfungKern.Linien * 2, MaxT = WandDaempfungKern.MaxTreffer;
        const float FernAb = 60f, FernFaktor = 4f, HimmelM = 40f, MinD = 0.5f;
        const float RequisiteMax = 8f; const int RequisiteCollider = 64, FahrzeugCollider = 2000;

        sealed class Eintrag
        {
            public AudioSource S; public int Id;
            public string Weg, Name;
            public bool Himmel, Tot, EigenGeprueft, Gesperrt;
            public Transform EigenWurzel;
            public HashSet<int> Eigen;
            public WandFilter F;
            public string Grund;                          // warum kein Filter (2D, mehrere Quellen, bypass)
            // Messung
            public bool Hat; public float Zeit = -999f; public Vector3 PosQ, PosH;
            public ISpatialAudioRoom Raum; public Vector3 RaumPos = new Vector3(float.NaN, 0, 0);
            // Ergebnis
            public double W0, W1, W2, U0, L0, L1, L2; public float Gain = 1f, Lp = 22000f; public int Linien;
            public WandSituation Lage; public int Portale; public bool Aussage;
            public int Nah, EigenTreffer;
            public readonly WandLogStueck[] Stuecke = new WandLogStueck[WandDaempfungKern.LogMax]; public int NStuecke;
            // Log
            public float LogZeit = -999f; public double LogL0 = -1; public float LogLp = -1f;
        }

        static readonly Dictionary<int, Eintrag> alle = new Dictionary<int, Eintrag>(256);
        static readonly List<Eintrag> liste = new List<Eintrag>(256);
        static int zeiger;
        static bool tote, gestoert, abbauOffen;
        static float abbauNaechste;

        // Strahlen-Auftrag (einer zur Zeit) + Sofort-Auftrag (Einmal-Klaenge)
        static NativeArray<RaycastCommand> cmds, cmdsS;
        static NativeArray<RaycastHit> hits, hitsS;
        static JobHandle handle;
        static bool laeuft;
        static readonly Eintrag[] flug = new Eintrag[MaxProBild];
        static readonly Vector3[] flugA = new Vector3[MaxProBild], flugB = new Vector3[MaxProBild];
        static readonly float[] flugLen = new float[MaxProBild];
        static int nFlug;

        // Rechenpuffer
        static readonly WandTreffer[] vorT = new WandTreffer[MaxT], rueckT = new WandTreffer[MaxT];
        static readonly float[] e0 = new float[WandDaempfungKern.Linien], e1 = new float[WandDaempfungKern.Linien], e2 = new float[WandDaempfungKern.Linien];
        static readonly float[] bandTmp = new float[WegeKern.N];
        static readonly double[] wand = new double[3], gesamt = new double[3];
        static readonly Vector3[] versatz = new Vector3[WandDaempfungKern.Linien];
        static readonly Dictionary<int, bool> gelaende = new Dictionary<int, bool>(256);
        static readonly List<AudioSource> quellenPuffer = new List<AudioSource>(4);
        static readonly List<Collider> colPuffer = new List<Collider>(64);
        static WandMaterial mat;

        // Diagnose
        internal static readonly Laufzeit Zeit = new Laufzeit("wall damping");
        static int dMess, dSofort, dGedaempft, dFrei, dOhne, dBudget, dFilter, dEntfernt;
        static double dSummeDb, dMaxDb;

        // =========================================================================================
        //  Einstellungen
        // =========================================================================================
        internal static void Binden(ConfigFile c)
        {
            string s = FesteQuellen.Sek;
            Aktiv = c.Bind(s, "Wall damping for non-occluded sources", true,
                "Ambient players (section 15), raw scene sources and vehicles (this section) and the scripted sources without occlusion (section 18: trains, " +
                "airdrop plane, BTR turret/horn, mortar whistle, extract gates) have no occlusion in the game - with their longer ranges you would hear a campfire, " +
                "the V-Ex taxi or the train through whole buildings as loud as outdoors. On = True Acoustics measures the walls between them and you itself " +
                "(5 lines through the walls, mass law per band like 'Walls between rooms', plus a detour path around the obstacle / through openings) and makes " +
                "them quieter and duller like occluded sources (volume x 'Loudness factor', low-pass from the band curve, 300 ms cross-fade). " +
                "Only extended sources, only while they play and are in range. Sound log: '[Walls]' lines. Works only while Physics Mode is on. Applies live.");
            BudgetMs = c.Bind(s, "Wall damping: max. ms per frame", 0.5f,
                new ConfigDescription("CPU budget on the main thread per frame (the rays themselves run on worker threads). When it is used up, the next sources wait " +
                    "for the next frame. 0 = no limit. The diagnostic line every 30 s shows the time used.", new AcceptableValueRange<float>(0f, 5f)));
            ProBild = c.Bind(s, "Wall damping: measurements per frame", 2,
                new ConfigDescription("New measurements started per frame, in turn over all audible sources (10 rays each). 2 at 60 fps = 120 per second.",
                    new AcceptableValueRange<int>(1, MaxProBild)));
            Takt = c.Bind(s, "Wall damping: refresh (s)", 0.5f,
                new ConfigDescription("A source is measured again after this time (sources farther than 60 m: 4x longer), at once when it or you moved more than 0.5 m.",
                    new AcceptableValueRange<float>(0.1f, 5f)));
            UmwegDraussen = c.Bind(s, "Wall damping: detour outdoors (dB)", 15f,
                new ConfigDescription("Sound also bends around an obstacle (house, car, hill). Loss of that detour at 250 Hz when you and the source are in the same room " +
                    "or both outdoors, +3 dB per octave above (Maekawa). Assumption: ISO 9613-2 caps the screening of one edge at 20 dB; typical 10-20 dB. " +
                    "The wall can only make it quieter down to this detour.", new AcceptableValueRange<float>(0f, 40f)));
            UmwegZwischen = c.Bind(s, "Wall damping: detour between rooms (dB)", 25f,
                new ConfigDescription("Detour loss at 250 Hz between different rooms of the game's room network (through doors/windows), +5 dB per further opening of the " +
                    "shortest route, +3 dB per octave. Assumption: ISO 9613-2 caps multiple diffraction at 25 dB. Rooms without any connection: walls only.",
                    new AcceptableValueRange<float>(0f, 60f)));
            NahQuelle = c.Bind(s, "Wall damping: ignore surfaces near the source (m)", 1.0f,
                new ConfigDescription("Surfaces closer than this to the source do not count (the barrel of a fire, the housing of a generator). Colliders of the source's " +
                    "own vehicle (V-Ex taxi, BTR, train) and of a small prop it sits in never count.", new AcceptableValueRange<float>(0f, 5f)));
            EventHandler neu = (_, __) => { foreach (var e in liste) e.Hat = false; abbauOffen = true; };
            Aktiv.SettingChanged += neu; UmwegDraussen.SettingChanged += neu; UmwegZwischen.SettingChanged += neu; NahQuelle.SettingChanged += neu;
            if (Physik.Aktiv != null) Physik.Aktiv.SettingChanged += neu;
            if (Plugin.Aktiv != null) Plugin.Aktiv.SettingChanged += neu;
        }

        // =========================================================================================
        //  An-/Abmelden (aus den Abschnitten 15/17/18)
        // =========================================================================================

        /// <summary>Stand einer verlaengerten Quelle ohne Okklusion: an = verlaengert (messen), sonst abmelden.
        /// himmel = in der Luft (nur das Stueck vor dem Ohr), sofort = Einmal-Klang (gleich messen), eigen = Wurzel des eigenen Fahrzeugs.</summary>
        internal static void Melden(AudioSource s, bool an, string weg, string name, bool himmel = false, bool sofort = false, Transform eigen = null)
        {
            if (s == null || gestoert) return;
            try
            {
                if (!an) { Abmelden(s); return; }
                int id = s.GetInstanceID();
                if (!alle.TryGetValue(id, out var e))
                {
                    e = new Eintrag { S = s, Id = id };
                    alle[id] = e; liste.Add(e);
                }
                e.Weg = weg; e.Name = name ?? s.gameObject.name; e.Himmel = himmel; e.Tot = false;
                if (eigen != null && e.EigenWurzel != eigen) { e.EigenWurzel = eigen; e.EigenGeprueft = false; }
                if (sofort && Wirksam) Sofort(e);
            }
            catch (Exception ex) { Fehler(ex); }
        }

        internal static void Abmelden(AudioSource s)
        {
            if (s == null) return;
            if (!alle.TryGetValue(s.GetInstanceID(), out var e)) return;
            alle.Remove(e.Id);
            e.Tot = true; tote = true;
            FilterWeg(e);
            dEntfernt++;
        }

        static void FilterWeg(Eintrag e)
        {
            try { if (e.F != null) UnityEngine.Object.Destroy(e.F); } catch (Exception ex) { Statistik.Fehler(ex); }
            e.F = null;
        }

        internal static void SzeneGewechselt()
        {
            try
            {
                if (laeuft) { handle.Complete(); laeuft = false; nFlug = 0; }
                foreach (var e in liste) if (e.S == null) { e.Tot = true; alle.Remove(e.Id); }
                tote = true; Aufraeumen();
                gelaende.Clear();
            }
            catch (Exception ex) { Statistik.Fehler(ex); }
        }

        internal static void Beenden()
        {
            try
            {
                if (laeuft) { handle.Complete(); laeuft = false; }
                if (cmds.IsCreated) cmds.Dispose(); if (hits.IsCreated) hits.Dispose();
                if (cmdsS.IsCreated) cmdsS.Dispose(); if (hitsS.IsCreated) hitsS.Dispose();
            }
            catch { }
        }

        static void Aufraeumen()
        {
            if (!tote) return;
            tote = false;
            liste.RemoveAll(e => e.Tot);
            if (zeiger >= liste.Count) zeiger = 0;
        }

        // =========================================================================================
        //  Takt (Plugin.Update)
        // =========================================================================================
        internal static void Tick()
        {
            if (gestoert || (liste.Count == 0 && !laeuft)) return;
            long t0 = Laufzeit.Jetzt; int bild = Time.frameCount;
            try
            {
                if (laeuft && handle.IsCompleted) Abschliessen();
                Aufraeumen();
                if (!Wirksam) { Abbauen(); return; }
                if (abbauOffen) abbauOffen = false;
                var h = FesteQuellen.Hoerer();
                if (h == null || laeuft) return;
                Planen(h.Value, t0);
            }
            catch (Exception ex) { Fehler(ex); }
            finally { Zeit.Dazu(t0, bild); }
        }

        static double Budget => BudgetMs != null ? BudgetMs.Value : 0.5;
        static bool UeberBudget(long t0) => Budget > 0 && Laufzeit.Ms(Laufzeit.Jetzt - t0) + Zeit.ImBild(Time.frameCount) > Budget;

        static void Planen(Vector3 hoerer, long t0)
        {
            int max = Math.Min(MaxProBild, ProBild != null ? ProBild.Value : 2);
            float jetzt = Time.time, takt = Takt != null ? Takt.Value : 0.5f;
            int n = liste.Count, gesehen = 0;
            nFlug = 0;
            while (gesehen < n && gesehen < 64 && nFlug < max)
            {
                if (zeiger >= liste.Count) zeiger = 0;
                var e = liste[zeiger++];
                gesehen++;
                if (e.Tot) continue;
                var s = e.S;
                if (s == null) { e.Tot = true; alle.Remove(e.Id); tote = true; continue; }
                if (!s.isActiveAndEnabled || !s.isPlaying) continue;
                if (e.Gesperrt && !KlangLog.An) continue;        // kein Filter moeglich: nur fuers Log messen
                Vector3 q = s.transform.position;
                float d = Vector3.Distance(q, hoerer);
                if (d > s.maxDistance) continue;                 // unhoerbar: Wert behalten, nicht messen
                if (s.spatialBlend < 0.5f) { Neutral(e, "2D"); continue; }
                if (d < MinD) { Neutral(e, null); e.Hat = true; e.Zeit = jetzt; continue; }
                if (!WandDaempfungKern.Faellig(e.Hat, jetzt - e.Zeit, (q - e.PosQ).magnitude, (hoerer - e.PosH).magnitude, d, takt, FernAb, FernFaktor)) continue;
                if (UeberBudget(t0)) { dBudget++; break; }
                Vorbereiten(e, q, hoerer, d);
                if (Strecke(e, q, hoerer, out var a, out var b, out float len)) { flug[nFlug] = e; flugA[nFlug] = a; flugB[nFlug] = b; flugLen[nFlug] = len; nFlug++; }
            }
            if (nFlug == 0) return;
            if (!cmds.IsCreated)
            {
                cmds = new NativeArray<RaycastCommand>(MaxProBild * Strahlen, Allocator.Persistent);
                hits = new NativeArray<RaycastHit>(MaxProBild * Strahlen * MaxT, Allocator.Persistent);
            }
            for (int i = 0; i < nFlug; i++) Befehle(cmds, i * Strahlen, flugA[i], flugB[i], flugLen[i]);
            handle = RaycastCommand.ScheduleBatch(cmds.GetSubArray(0, nFlug * Strahlen), hits.GetSubArray(0, nFlug * Strahlen * MaxT), 1, MaxT);
            JobHandle.ScheduleBatchedJobs();
            laeuft = true;
        }

        /// <summary>Messstrecke A (Quelle bzw. Punkt in der Luft) -> B (Ohr). false = nichts zu messen.</summary>
        static bool Strecke(Eintrag e, Vector3 q, Vector3 h, out Vector3 a, out Vector3 b, out float len)
        {
            b = h; a = q;
            Vector3 d = q - h;
            len = d.magnitude;
            if (len < MinD) return false;
            if (e.Himmel && len > HimmelM) { a = h + d * (HimmelM / len); len = HimmelM; }
            return true;
        }

        static void Befehle(NativeArray<RaycastCommand> c, int basis, Vector3 a, Vector3 b, float len)
        {
            Vector3 d = (b - a) / len;
            Vector3 u = Vector3.Cross(d, Vector3.up);
            if (u.sqrMagnitude < 0.01f) u = Vector3.Cross(d, Vector3.right);
            u.Normalize();
            Vector3 v = Vector3.Cross(d, u);
            float k = WandDaempfungKern.Kreis;
            versatz[0] = Vector3.zero; versatz[1] = u * k; versatz[2] = -u * k; versatz[3] = v * k; versatz[4] = -v * k;
            int maske = (int)LayersMaskController.HighPolyWithTerrainMask | Statistik.AudioMaske;
            var qp = new QueryParameters(maske, true, QueryTriggerInteraction.Ignore, true);
            for (int l = 0; l < WandDaempfungKern.Linien; l++)
            {
                c[basis + 2 * l] = new RaycastCommand(a + versatz[l], d, qp, len);
                c[basis + 2 * l + 1] = new RaycastCommand(b + versatz[l], -d, qp, len);
            }
        }

        /// <summary>Einmal-Klang (Ausgangs-Tor, Lok-Ereignis): sofort messen (kleiner Auftrag, gleich abgeschlossen) und ohne Uebergang setzen.</summary>
        static void Sofort(Eintrag e)
        {
            var s = e.S;
            var h = FesteQuellen.Hoerer();
            if (s == null || h == null || s.spatialBlend < 0.5f) return;
            Vector3 q = s.transform.position;
            float d = Vector3.Distance(q, h.Value);
            if (d > s.maxDistance || d < MinD) return;
            long t0 = Laufzeit.Jetzt; int bild = Time.frameCount;
            try
            {
                Vorbereiten(e, q, h.Value, d);
                if (!Strecke(e, q, h.Value, out var a, out var b, out float len)) return;
                if (!cmdsS.IsCreated)
                {
                    cmdsS = new NativeArray<RaycastCommand>(Strahlen, Allocator.Persistent);
                    hitsS = new NativeArray<RaycastHit>(Strahlen * MaxT, Allocator.Persistent);
                }
                Befehle(cmdsS, 0, a, b, len);
                RaycastCommand.ScheduleBatch(cmdsS, hitsS, 1, MaxT).Complete();
                e.Hat = false;   // erstes Ergebnis ohne Uebergang
                Auswerten(e, hitsS, 0, a, b, len);
                dSofort++;
            }
            finally { Zeit.Dazu(t0, bild); }
        }

        static void Vorbereiten(Eintrag e, Vector3 q, Vector3 h, float d)
        {
            if (!e.EigenGeprueft) { e.EigenGeprueft = true; e.Eigen = EigeneCollider(e); }
            e.PosQ = q; e.PosH = h;
        }

        static void Abschliessen()
        {
            handle.Complete();   // fertig laut IsCompleted -> blockiert nicht
            laeuft = false;
            for (int i = 0; i < nFlug; i++)
            {
                var e = flug[i]; flug[i] = null;
                if (e == null || e.Tot || e.S == null) continue;
                try { Auswerten(e, hits, i * Strahlen * MaxT, flugA[i], flugB[i], flugLen[i]); }
                catch (Exception ex) { Fehler(ex); }
            }
            nFlug = 0;
        }

        // =========================================================================================
        //  Auswerten
        // =========================================================================================
        static WandMaterial Material()
        {
            if (mat == null)
            {
                int n = Plugin.AnzahlGruppen;
                mat = new WandMaterial
                {
                    Gruppe = MaterialCache.Get, Gelaende = IstGelaende,
                    Daemmung = (g, d, f, deckel) => Physik.Daemmung(g, d, f, deckel),
                    Masse = Physik.Masse,
                    Dichte = new float[n], Einzel = new float[n], Massiv = new float[n], Schale = new bool[n],
                    Koerper = (int)Gruppe.Body
                };
            }
            float schale = WandZwischenRaeumen.Schale != null ? WandZwischenRaeumen.Schale.Value : 0.15f;
            for (int g = 0; g < Plugin.AnzahlGruppen; g++)
            {
                mat.Dichte[g] = Physik.Dichte[g].Value; mat.Massiv[g] = Physik.Massiv[g].Value;
                mat.Einzel[g] = Bauakustik.EinzelDicke(g, schale); mat.Schale[g] = Bauakustik.IstSchalenGruppe(g);
            }
            mat.SchaleM = schale; mat.Dyn = Physik.Dynamik.Value;
            var f = Wege.Frequenzen();
            mat.F[0] = f[0]; mat.F[1] = f[1]; mat.F[2] = f[2];
            return mat;
        }

        static void Auswerten(Eintrag e, NativeArray<RaycastHit> h, int basis, Vector3 a, Vector3 b, float len)
        {
            var m = Material();
            float dyn = m.Dyn;
            bool log = KlangLog.An;
            e.NStuecke = 0; e.Nah = e.EigenTreffer = 0;
            float nah = e.Himmel && len >= HimmelM - 0.01f ? 0f : (NahQuelle != null ? NahQuelle.Value : 1f);
            Wege.TBeginn(b, a);   // Empfangsraum/Flanken = Raum des Hoerers
            bool mitteNahA = false, mitteNahB = false;
            int linien = 0;
            for (int k = 0; k < WandDaempfungKern.Linien; k++)
            {
                int nVor = Lesen(e, h, basis + (2 * k) * MaxT, len, false, nah, vorT, out bool vorVoll);
                int nRueck = Lesen(e, h, basis + (2 * k + 1) * MaxT, len, true, nah, rueckT, out bool rueckVoll);
                if (k == 0) WandLinie.NahAnEnden(vorT, nVor, rueckT, nRueck, len, out mitteNahA, out mitteNahB);
                int nLog = e.NStuecke;
                var w = WandDaempfungKern.Linie(vorT, nVor, vorVoll, rueckT, nRueck, rueckVoll, len, k != 0, mitteNahA, mitteNahB, m,
                                                log && k == 0 ? e.Stuecke : null, ref nLog);
                if (k == 0) e.NStuecke = nLog;
                if (w.Befund == 1) { e0[k] = e1[k] = e2[k] = -1f; continue; }
                linien++;
                if (w.Befund == 2) { e0[k] = e1[k] = e2[k] = 0f; continue; }
                bandTmp[0] = w.Db0; bandTmp[1] = w.Db1; bandTmp[2] = w.Db2;
                if (w.Stuecke > 0 && w.Db0 > 0f && w.Masse > 0) Wege.Korrigieren(w.Masse, w.R1, w.R2, bandTmp);
                e0[k] = WandDaempfungKern.Energie(bandTmp[0]); e1[k] = WandDaempfungKern.Energie(bandTmp[1]); e2[k] = WandDaempfungKern.Energie(bandTmp[2]);
            }
            dMess++;
            e.Linien = linien;
            bool erst = !e.Hat;
            e.Hat = true; e.Zeit = Time.time;
            if (!WandDaempfungKern.Mitteln(e0, e1, e2, wand, dyn))
            {
                // keine Aussage (alle Linien mit Luecke): letzten Wert behalten
                e.Aussage = false; dOhne++;
                Loggen(e, Vector3.Distance(a, b));
                return;
            }
            e.Aussage = true;
            e.W0 = wand[0]; e.W1 = wand[1]; e.W2 = wand[2];
            Lage(e, a);
            double u = WandDaempfungKern.Umweg250(e.Lage, e.Portale, UmwegDraussen.Value, UmwegZwischen.Value, dyn);
            e.U0 = u;
            WandDaempfungKern.Summe(wand, u, m.F, dyn, gesamt);
            e.L0 = gesamt[0]; e.L1 = gesamt[1]; e.L2 = gesamt[2];
            e.Gain = (float)WandDaempfungKern.Gain(e.L0, Physik.Lautheit.Value);
            e.Lp = Wege.Baender ? Wege.FitLp(Math.Max(0, e.L1 - e.L0), Math.Max(0, e.L2 - e.L0)) : Physik.Abbildung((float)(e.L0 / dyn));
            if (e.L0 > 0.05) { dGedaempft++; dSummeDb += e.L0; dMaxDb = Math.Max(dMaxDb, e.L0); } else dFrei++;
            Setzen(e, erst);
            Loggen(e, Vector3.Distance(e.S.transform.position, b));
        }

        // Treffer eines Strahls (T ab A = Quelle), ohne eigene Huelle und Flaechen nahe der Quelle. voll = Trefferhoechstzahl erreicht.
        static int Lesen(Eintrag e, NativeArray<RaycastHit> h, int basis, float len, bool rueck, float nah, WandTreffer[] ziel, out bool voll)
        {
            int n = 0, roh = 0;
            for (int j = 0; j < MaxT; j++)
            {
                RaycastHit t = h[basis + j];
                int id = t.colliderInstanceID;
                if (id == 0) break;
                roh++;
                float T = rueck ? len - t.distance : t.distance;
                if (e.Eigen != null && e.Eigen.Contains(id)) { e.EigenTreffer++; continue; }
                if (T < nah) { e.Nah++; continue; }
                ziel[n].Id = id; ziel[n].T = T; n++;
            }
            voll = roh >= MaxT;
            return n;
        }

        static void Lage(Eintrag e, Vector3 q)
        {
            e.Lage = WandSituation.Unbekannt; e.Portale = 0;
            if (!MonoBehaviourSingleton<SpatialAudioSystem>.Instantiated) return;
            var sys = MonoBehaviourSingleton<SpatialAudioSystem>.Instance;
            var st = sys._audioRoomStorage;
            var lr = sys.ListenerCurrentRoom;
            if (st == null || lr == null || !lr.IsValid) return;
            if (e.Raum == null || !e.Raum.IsValid || float.IsNaN(e.RaumPos.x) || (q - e.RaumPos).sqrMagnitude > 1f)
            {
                e.Raum = st.FindActualCurrentRoom(e.Raum ?? st._emptyRoom, q);
                e.RaumPos = q;
            }
            var qr = e.Raum;
            bool gueltig = qr != null && qr.IsValid;
            bool paar = false;
            if (gueltig && qr.ID != lr.ID && !(qr.IsOutdoor && lr.IsOutdoor) && st.TryGetMatchingRoomPair(qr, lr, out var rp, out _) && rp != null)
            { paar = true; e.Portale = rp.ShortestRouteLength; }
            e.Lage = WandDaempfungKern.Lage(gueltig, gueltig ? qr.ID : -1, lr.ID, gueltig && qr.IsOutdoor, lr.IsOutdoor, paar);
        }

        // ---- Wirkung ----
        static void Setzen(Eintrag e, bool sofort)
        {
            bool neutral = WandFilterKern.IstNeutral(e.Gain, e.Lp);
            if (e.F == null)
            {
                if (neutral || e.Gesperrt) return;
                if (!FilterAnlegen(e)) return;
                sofort = true;
            }
            var k = e.F.K;
            k.Tau = (float)Math.Max(0.0, Wege.BlendSek / 3.0);
            k.ZielGain = e.Gain; k.ZielFc = e.Lp;
            if (sofort) k.Springen = true;
        }

        static void Neutral(Eintrag e, string grund)
        {
            if (grund != null) e.Grund = grund;
            e.Gain = 1f; e.Lp = 22000f; e.L0 = e.L1 = e.L2 = 0;
            if (e.F != null) { e.F.K.ZielGain = 1f; e.F.K.ZielFc = 22000f; }
        }

        static bool FilterAnlegen(Eintrag e)
        {
            var s = e.S;
            if (s.bypassEffects) { e.Gesperrt = true; e.Grund = "bypass effects"; return false; }
            quellenPuffer.Clear();
            s.GetComponents(quellenPuffer);
            int n = quellenPuffer.Count; quellenPuffer.Clear();
            if (n > 1) { e.Gesperrt = true; e.Grund = "several audio sources on one object"; return false; }
            var f = s.gameObject.GetComponent<WandFilter>();
            if (f == null) f = s.gameObject.AddComponent<WandFilter>();
            f.K.Fs = AudioSettings.outputSampleRate;
            e.F = f;
            dFilter++;
            return true;
        }

        /// <summary>Abschnitt aus: Filter auf neutral, nach dem Ausblenden entfernen.</summary>
        static void Abbauen()
        {
            float jetzt = Time.realtimeSinceStartup;
            if (jetzt < abbauNaechste) return;
            abbauNaechste = jetzt + 0.25f;
            foreach (var e in liste)
            {
                if (e.F == null) continue;
                var k = e.F.K;
                k.Tau = (float)Math.Max(0.0, Wege.BlendSek / 3.0); k.ZielGain = 1f; k.ZielFc = 22000f;
                e.Hat = false;
                if (k.Durchgang || e.S == null || !e.S.isPlaying) FilterWeg(e);
            }
        }

        // ---- eigene Huelle ----
        static HashSet<int> EigeneCollider(Eintrag e)
        {
            colPuffer.Clear();
            try
            {
                if (e.EigenWurzel != null)
                {
                    e.EigenWurzel.GetComponentsInChildren(true, colPuffer);
                    if (colPuffer.Count == 0 || colPuffer.Count > FahrzeugCollider) return null;
                    return Menge();
                }
                var p = e.S.transform.parent;
                if (p == null || p.childCount > 32) return null;
                p.GetComponentsInChildren(true, colPuffer);
                if (colPuffer.Count == 0 || colPuffer.Count > RequisiteCollider) return null;
                Bounds bb = default; bool erst = true;
                foreach (var c in colPuffer)
                {
                    if (c == null) continue;
                    if (c is TerrainCollider) return null;
                    if (erst) { bb = c.bounds; erst = false; } else bb.Encapsulate(c.bounds);
                }
                if (erst || bb.size.x > RequisiteMax || bb.size.y > RequisiteMax || bb.size.z > RequisiteMax) return null;
                return Menge();
            }
            finally { colPuffer.Clear(); }
        }

        static HashSet<int> Menge()
        {
            var m = new HashSet<int>();
            foreach (var c in colPuffer) if (c != null) m.Add(c.GetInstanceID());
            return m.Count > 0 ? m : null;
        }

        static bool IstGelaende(int id)
        {
            if (gelaende.TryGetValue(id, out bool b)) return b;
            b = Resources.InstanceIDToObject(id) is TerrainCollider;
            gelaende[id] = b;
            return b;
        }

        static void Fehler(Exception ex)
        {
            Statistik.Fehler(ex);
            if (ex is MissingFieldException || ex is MissingMethodException || ex is TypeLoadException)
            {
                gestoert = true;
                Plugin.Log.LogError($"Wall damping: {ex.GetType().Name}: {ex.Message} - game changed? Wall damping off for this session.");
                foreach (var e in liste) FilterWeg(e);
            }
        }

        // =========================================================================================
        //  Klang-Log und Diagnose
        // =========================================================================================
        static void Loggen(Eintrag e, float d)
        {
            if (!KlangLog.An || d > KlangLog.Radius.Value) return;
            float jetzt = Time.realtimeSinceStartup;
            if (jetzt - e.LogZeit < 1f) return;
            bool aenderung = Math.Abs(e.L0 - e.LogL0) > 1.0 || (e.LogLp > 0 && Math.Abs(Math.Log(e.Lp / e.LogLp)) > 0.15);
            if (!aenderung && jetzt - e.LogZeit < 5f) return;
            e.LogZeit = jetzt; e.LogL0 = e.L0; e.LogLp = e.Lp;
            KlangLog.Text(Text(e, d));
        }

        static string Text(Eintrag e, float d)
        {
            var inv = CultureInfo.InvariantCulture;
            var s = e.S;
            var sb = new StringBuilder(320);
            sb.Append("[Walls] ").Append(e.Name).Append(" (").Append(e.Weg).Append(", clip ").Append(s != null && s.clip != null ? s.clip.name : "-").Append(')');
            sb.Append(" | d=").Append(d.ToString("F1", inv)).Append(" m");
            if (e.Himmel) sb.Append(" (in the air: last ").Append(HimmelM.ToString("0", inv)).Append(" m measured)");
            sb.Append(" | ").Append(WandDaempfungKern.LageName(e.Lage));
            if (e.Lage == WandSituation.ZwischenRaeumen) sb.Append(" (route ").Append(e.Portale).Append(" openings)");
            if (!e.Aussage) { sb.Append(" | no usable line (gaps) - value kept: ").Append(e.L0.ToString("F1", inv)).Append(" dB"); return sb.ToString(); }
            sb.Append(" | lines ").Append(e.Linien).Append('/').Append(WandDaempfungKern.Linien);
            if (e.NStuecke > 0)
            {
                sb.Append(" | wall:");
                for (int i = 0; i < e.NStuecke; i++)
                {
                    var p = e.Stuecke[i];
                    sb.Append(i == 0 ? " " : ", ").Append(p.G >= 0 && p.G < Plugin.AnzahlGruppen ? ((Gruppe)p.G).ToString() : "?").Append(' ')
                      .Append(p.Dicke.ToString("0.00", inv)).Append(" m ").Append(p.Db.ToString("F1", inv)).Append(" dB");
                    if (p.Teile > 1) sb.Append(" (").Append(p.Teile).Append(" parts)");
                    string n = Name(p.Id);
                    if (n != null) sb.Append(" '").Append(n).Append('\'');
                }
            }
            else sb.Append(" | centre line clear");
            sb.Append(" | walls ").Append(e.W0.ToString("F1", inv)).Append('/').Append(e.W1.ToString("F1", inv)).Append('/').Append(e.W2.ToString("F1", inv)).Append(" dB");
            if (e.U0 >= 0) sb.Append(" + detour ").Append(e.U0.ToString("F1", inv)).Append(" dB @").Append(Wege.F[0].ToString("0", inv)).Append(" Hz");
            else sb.Append(" (no detour)");
            sb.Append(" -> ").Append(e.L0.ToString("F1", inv)).Append('/').Append(e.L1.ToString("F1", inv)).Append('/').Append(e.L2.ToString("F1", inv)).Append(" dB");
            double vdb = e.L0 * Physik.Lautheit.Value;
            sb.Append(" | volume ").Append((-vdb).ToString("F1", inv)).Append(" dB (x loudness ").Append(Physik.Lautheit.Value.ToString("0.##", inv)).Append(')')
              .Append(", low-pass ").Append(e.Lp >= 20000f ? "open" : (e.Lp / 1000f).ToString("0.00", inv) + " kHz");
            if (e.Gesperrt) sb.Append(" | NOT applied: ").Append(e.Grund);
            if (e.Nah > 0 || e.EigenTreffer > 0) sb.Append(" | ignored: ").Append(e.Nah).Append(" near-source, ").Append(e.EigenTreffer).Append(" own object");
            return sb.ToString();
        }

        static string Name(int id)
        {
            try { return Resources.InstanceIDToObject(id) is Collider c ? c.gameObject.name : null; } catch { return null; }
        }

        internal static void Loggen()
        {
            if (liste.Count == 0 && dMess == 0) return;
            int amb = 0, roh = 0, fz = 0, skr = 0, filter = 0, gesperrt = 0;
            var gruende = new Dictionary<string, int>();
            foreach (var e in liste)
            {
                if (e.Tot) continue;
                switch (e.Weg) { case "ambient": amb++; break; case "raw scene": roh++; break; case "vehicle": fz++; break; default: skr++; break; }
                if (e.F != null) filter++;
                if (e.Grund != null) { gesperrt++; gruende.TryGetValue(e.Grund, out int g); gruende[e.Grund] = g + 1; }
            }
            var inv = CultureInfo.InvariantCulture;
            var sb = new StringBuilder(300);
            sb.Append("[Diagnostics] Wall damping: ").Append(liste.Count).Append(" sources (ambient ").Append(amb).Append(", raw scene ").Append(roh)
              .Append(", vehicle ").Append(fz).Append(", scripted ").Append(skr).Append("), ").Append(filter).Append(" with filter (").Append(dFilter).Append(" added, ")
              .Append(dEntfernt).Append(" released); ").Append(dMess).Append(" measurements (").Append(dSofort).Append(" one-shot): muffled ").Append(dGedaempft);
            if (dGedaempft > 0) sb.Append(" (avg ").Append((dSummeDb / dGedaempft).ToString("F1", inv)).Append(" dB, max ").Append(dMaxDb.ToString("F1", inv)).Append(" dB)");
            sb.Append(", clear ").Append(dFrei).Append(", no usable line ").Append(dOhne).Append(", ").Append(dBudget).Append("x budget");
            if (gruende.Count > 0) { sb.Append("; not filtered:"); foreach (var kv in gruende) sb.Append(' ').Append(kv.Key).Append(' ').Append(kv.Value); }
            if (!Zeit.Leer) sb.Append("; ").Append(Zeit.Text());
            if (!Wirksam) sb.Append(" (off)");
            Plugin.Log.LogInfo(sb.ToString());
            dMess = dSofort = dGedaempft = dFrei = dOhne = dBudget = dFilter = dEntfernt = 0; dSummeDb = dMaxDb = 0;
        }
    }

    /// <summary>Lautstaerke + Tiefpass im Signalweg einer Quelle (Audiofaden). Parameter setzt WandDaempfung (Hauptfaden).</summary>
    internal sealed class WandFilter : MonoBehaviour
    {
        internal readonly WandFilterKern K = new WandFilterKern();
        void OnAudioFilterRead(float[] data, int channels)
        {
            try { K.Verarbeiten(data, channels); } catch { }
        }
    }
}
