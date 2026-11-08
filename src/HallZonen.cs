using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using Audio.SpatialSystem;
using BepInEx.Configuration;
using EFT.Ballistics;
using HarmonyLib;
using Meta.XR.Acoustics;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Materialklang
{
    // Feature 9: Hall je Material.
    // Das Spiel nutzt Meta-XR-Audio mit eingebackenen Raumdaten (.xrageo), in denen ALLE Materialien
    // gleich sind (Absorption 0.1). Der Hall kennt also keine Materialien. Wir messen darum beim
    // Raidstart je Innenraum, woraus die Waende bestehen (Strahlen + BallisticCollider), rechnen mit
    // Sabine die Abweichung zu 0.1 aus und legen pro Raum eine offizielle Meta-"Control Zone" an,
    // die RT60 (Sekunden, additiv) und Hallpegel (dB, additiv) im Raum verschiebt.
    internal static class HallZonen
    {
        // Baender der Control Zone (Hz).
        static readonly float[] Baender = { 125f, 250f, 500f, 1000f, 2000f, 4000f };
        const int Band1k = 3;
        const float GebackenAlpha = 0.1f;
        const int Strahlen = 64;
        const int MinTreffer = 12;          // darunter ist der Raum zu offen fuer eine Aussage
        const float MinVolumen = 6f;        // m3
        const float MaxVolumen = 40000f;

        internal static ConfigEntry<bool> Aktiv;
        internal static ConfigEntry<float> Staerke;
        internal static ConfigEntry<int> MaxZonen;
        internal static ConfigEntry<bool> NurGroessteBox;
        internal static ConfigEntry<bool> Diagnose;

        // Absorptionsgrad je Materialname und Frequenz, Paare (Hz, alpha). Quelle: BSGs ungenutzte Zuordnung
        // (Spieldaten, Herkunft von BSG nicht dokumentiert; mehrere Zeilen gleichen klassischen Lehrbuchtabellen)
        // (26 Materialien, Namen = EFT-MaterialType). Alles Unbekannte faellt auf Stone zurueck.
        static readonly Dictionary<string, float[]> Tabelle = new Dictionary<string, float[]>
        {
            ["Asphalt"] = new float[] { 125f, 0.02f, 250f, 0.03f, 500f, 0.05f, 1000f, 0.06f, 2000f, 0.07f, 4000f, 0.08f },
            ["Cardboard"] = new float[] { 400f, 0.41f, 500f, 0.607f, 630f, 0.773f, 800f, 0.669f, 1000f, 0.685f, 1250f, 0.806f, 1600f, 0.579f, 2000f, 0.792f },
            ["Concrete"] = new float[] { 125f, 0.01f, 250f, 0.01f, 500f, 0.02f, 1000f, 0.02f, 2000f, 0.02f, 4000f, 0.02f },
            ["Fabric"] = new float[] { 125f, 0.07f, 250f, 0.31f, 500f, 0.49f, 1000f, 0.75f, 2000f, 0.7f, 4000f, 0.6f },
            ["GarbageMetal"] = new float[] { 1000f, 1.0f },
            ["Glass"] = new float[] { 125f, 0.35f, 250f, 0.25f, 500f, 0.18f, 1000f, 0.12f, 2000f, 0.07f, 4000f, 0.05f },
            ["GrassHigh"] = new float[] { 125f, 0.11f, 250f, 0.26f, 500f, 0.6f, 1000f, 0.69f, 2000f, 0.92f, 4000f, 0.99f },
            ["GrassLow"] = new float[] { 125f, 0.11f, 250f, 0.26f, 500f, 0.6f, 1000f, 0.69f, 2000f, 0.92f, 4000f, 0.99f },
            ["Grate"] = new float[] { 1000f, 1.0f },
            ["Gravel"] = new float[] { 125f, 0.25f, 250f, 0.6f, 500f, 0.65f, 1000f, 0.7f, 2000f, 0.75f, 4000f, 0.8f },
            ["MetalNoDecal"] = new float[] { 125f, 0.05f, 250f, 0.1f, 500f, 0.1f, 1000f, 0.1f, 2000f, 0.07f, 4000f, 0.02f },
            ["MetalThick"] = new float[] { 125f, 0.05f, 250f, 0.1f, 500f, 0.1f, 1000f, 0.1f, 2000f, 0.07f, 4000f, 0.02f },
            ["MetalThin"] = new float[] { 125f, 0.05f, 250f, 0.1f, 500f, 0.1f, 1000f, 0.1f, 2000f, 0.07f, 4000f, 0.02f },
            ["Mud"] = new float[] { 125f, 0.15f, 250f, 0.25f, 500f, 0.3f, 1000f, 0.25f, 2000f, 0.2f, 4000f, 0.15f },
            ["Pebbles"] = new float[] { 125f, 0.25f, 250f, 0.6f, 500f, 0.65f, 1000f, 0.7f, 2000f, 0.75f, 4000f, 0.8f },
            ["Plastic"] = new float[] { 125f, 0.12f, 250f, 0.09f, 500f, 0.07f, 1000f, 0.05f, 2000f, 0.05f, 4000f, 0.04f },
            ["Rubber"] = new float[] { 125f, 0.05f, 250f, 0.05f, 500f, 0.1f, 1000f, 0.1f, 2000f, 0.05f, 4000f, 0.05f },
            ["Soil"] = new float[] { 125f, 0.15f, 250f, 0.25f, 500f, 0.4f, 1000f, 0.55f, 2000f, 0.6f, 4000f, 0.6f },
            ["SoilForest"] = new float[] { 125f, 0.15f, 250f, 0.25f, 500f, 0.4f, 1000f, 0.55f, 2000f, 0.6f, 4000f, 0.6f },
            ["Stone"] = new float[] { 125f, 0.02f, 500f, 0.02f, 2000f, 0.05f, 4000f, 0.05f },
            ["Swamp"] = new float[] { 125f, 0.01f, 250f, 0.01f, 500f, 0.01f, 1000f, 0.02f, 2000f, 0.02f, 4000f, 0.03f },
            ["Tile"] = new float[] { 125f, 0.01f, 250f, 0.01f, 500f, 0.01f, 1000f, 0.01f, 2000f, 0.02f, 4000f, 0.02f },
            ["Water"] = new float[] { 125f, 0.01f, 250f, 0.01f, 500f, 0.01f, 1000f, 0.02f, 2000f, 0.02f, 4000f, 0.03f },
            ["WaterPuddle"] = new float[] { 125f, 0.01f, 250f, 0.01f, 500f, 0.01f, 1000f, 0.02f, 2000f, 0.02f, 4000f, 0.03f },
            ["WoodThick"] = new float[] { 125f, 0.19f, 250f, 0.14f, 500f, 0.09f, 1000f, 0.06f, 2000f, 0.06f, 4000f, 0.05f },
            ["WoodThin"] = new float[] { 125f, 0.42f, 250f, 0.21f, 500f, 0.1f, 1000f, 0.08f, 2000f, 0.06f, 4000f, 0.06f },
        };

        // MaterialType-Namen ohne eigene Zeile -> naechstes Tabellenmaterial.
        static readonly Dictionary<string, string> Aliase = new Dictionary<string, string>
        {
            ["GlassVisor"] = "Glass", ["GlassShattered"] = "Glass", ["GarbagePaper"] = "Cardboard",
            ["Tyre"] = "Rubber", ["GenericSoft"] = "Fabric", ["GenericHard"] = "Concrete",
        };

        sealed class Box
        {
            public Vector3 Mitte, Halb;
            public Quaternion Drehung;
        }

        sealed class RaumDaten
        {
            public string Name, Dominant;
            public Box Box;
            public float Volumen, VzuS;
            public int Treffer;
            public bool Angelegt;                 // fuer das Klang-Log: es gibt eine Zone fuer diesen Raum
            public float DRt1k, DPegel1k;         // Verschiebung bei 1 kHz (s, dB) laut letzter Berechnung
            public Quaternion Inv;
            public float[] Alpha = new float[Baender.Length];
        }

        static Vector3[] richtungen;
        static List<SpatialAudioRoom> raeume;       // Raeume des laufenden Raids (aus dem Raumspeicher)
        static List<RaumDaten> daten;               // Messergebnis (einmal je Raid)
        static GameObject wurzel;
        static int zonenAktiv;
        static bool anstehend;
        static float fruehestens;

        public static int ZonenAnzahl => zonenAktiv;

        public static void Binden(ConfigFile cfg)
        {
            const string sek = "9. Reverb by Material";
            Aktiv = cfg.Bind(sek, "Enabled", true,
                "Reverb by material on/off. At raid start it measures the wall materials of every interior room and shifts reverb time and reverb level in the room (Meta Control Zones). Off = reverb as in the base game. Takes effect immediately.");
            Staerke = cfg.Bind(sek, "Strength", 1.0f,
                new ConfigDescription("Multiplies the calculated shift of reverb time and reverb level (0 = none, 1 = as calculated, 2 = double).", new AcceptableValueRange<float>(0f, 2f)));
            MaxZonen = cfg.Bind(sek, "Max zones", 400,
                new ConfigDescription("Maximum number of reverb zones per raid (large rooms first).", new AcceptableValueRange<int>(0, 2000)));
            NurGroessteBox = cfg.Bind(sek, "Only largest box per room", true,
                "A room often consists of several boxes. On: only the largest gets a zone (overlapping zones could add up). Off: one zone per box. Applies live (the rooms are measured again).");
            Diagnose = cfg.Bind(sek, "Diagnostics", true,
                "Writes to the BepInEx log at raid start: zone count, measuring time and the 5 most extreme rooms (name, main material, change of reverb time at 1 kHz).");

            Aktiv.SettingChanged += (_, __) =>
            {
                if (!Aktiv.Value) ZonenZerstoeren();
                else if (daten != null) Anstehend(0f);
            };
            Staerke.SettingChanged += (_, __) => { if (Aktiv.Value && daten != null) Anstehend(0f); };
            // live: andere Boxauswahl -> neu messen (die Raumliste bleibt bis Raidende erhalten)
            NurGroessteBox.SettingChanged += (_, __) => { if (Aktiv.Value && raeume != null) { daten = null; Anstehend(0f); } };
            MaxZonen.SettingChanged += (_, __) => { if (Aktiv.Value && daten != null) Anstehend(0f); };
        }

        /// <summary>Klang-Log: Hall-Verschiebung (1 kHz) der Zone, in der der Punkt liegt. Kleinste passende Box gewinnt.</summary>
        internal static bool Finde(Vector3 p, out string name, out float rt, out float db)
        {
            name = null; rt = db = 0f;
            if (zonenAktiv == 0 || daten == null) return false;
            RaumDaten best = null;
            for (int i = 0; i < daten.Count; i++)
            {
                var d = daten[i];
                if (!d.Angelegt) continue;
                Vector3 v = p - d.Box.Mitte;
                float r = d.Box.Halb.magnitude;
                if (v.sqrMagnitude > r * r) continue;
                Vector3 l = d.Inv * v;
                if (Mathf.Abs(l.x) > d.Box.Halb.x || Mathf.Abs(l.y) > d.Box.Halb.y || Mathf.Abs(l.z) > d.Box.Halb.z) continue;
                if (best == null || d.Volumen < best.Volumen) best = d;
            }
            if (best == null) return false;
            name = best.Name; rt = best.DRt1k; db = best.DPegel1k;
            return true;
        }

        static void Anstehend(float verzoegerung)
        {
            anstehend = true;
            fruehestens = Time.realtimeSinceStartup + verzoegerung;
        }

        // Raidstart: Raumspeicher steht, Raeume sind initialisiert.
        internal static void RaeumeMelden(List<SpatialAudioRoom> liste)
        {
            ZonenZerstoeren();
            daten = null;
            raeume = liste != null ? new List<SpatialAudioRoom>(liste) : null;
            if (raeume != null && raeume.Count > 0) Anstehend(1.5f);
        }

        // Raidende / Szenenwechsel.
        public static void Leeren()
        {
            ZonenZerstoeren();
            daten = null;
            raeume = null;
            anstehend = false;
        }

        // Szenenwechsel waehrend das Raumsystem noch lebt (Reihenfolge der Ereignisse unsicher): nichts tun.
        public static void SzeneGewechselt()
        {
            if (!SpatialAudioSystem.Initialized) Leeren();
        }

        // Aus Plugin.Update: fuehrt das Anlegen aus, sobald das Raumsystem fertig ist.
        public static void Tick()
        {
            if (!anstehend || !Aktiv.Value) return;
            if (!SpatialAudioSystem.Initialized || Time.realtimeSinceStartup < fruehestens) return;
            anstehend = false;
            var sw = Stopwatch.StartNew();
            try
            {
                bool gemessen = false;
                if (daten == null)
                {
                    daten = Messen();
                    gemessen = true;
                }
                double msMessen = sw.Elapsed.TotalMilliseconds;
                ZonenZerstoeren();
                Erzeugen(msMessen, gemessen, sw);
            }
            catch (Exception ex)
            {
                Statistik.Fehler(ex);
            }
        }

        // ---------------------------------------------------------------- Messen

        static List<RaumDaten> Messen()
        {
            var ergebnis = new List<RaumDaten>();
            if (raeume == null) return ergebnis;
            if (richtungen == null) richtungen = Fibonacci(Strahlen);
            var typCache = new Dictionary<int, string>(256);
            int maske = LayersMaskController.HighPolyWithTerrainMask;
            var zaehler = new Dictionary<string, int>();
            var summe = new float[Baender.Length];

            foreach (var raum in raeume)
            {
                if (raum == null || !Innenraum(raum) || raum.Colliders == null) continue;
                var boxen = new List<Box>();
                foreach (var c in raum.Colliders)
                    if (c != null && c.enabled) boxen.Add(AusCollider(c));
                if (boxen.Count == 0) continue;
                if (NurGroessteBox.Value)
                {
                    var b = boxen.OrderByDescending(x => x.Halb.x * x.Halb.y * x.Halb.z).First();
                    boxen.Clear();
                    boxen.Add(b);
                }
                string name = !string.IsNullOrEmpty(raum.Name) ? raum.Name : raum.gameObject.name;
                foreach (var box in boxen)
                {
                    var d = MessenBox(name, box, maske, typCache, zaehler, summe);
                    if (d != null) ergebnis.Add(d);
                }
            }
            return ergebnis;
        }

        static bool Innenraum(SpatialAudioRoom r)
        {
            if (r.Outdoor) return false;
            var t = r.Type;
            if ((t & EAudioRoomTypeMask.Indoor) == 0) return false;
            if ((t & (EAudioRoomTypeMask.Outdoor | EAudioRoomTypeMask.IndoorVehicle)) != 0) return false;
            return true;
        }

        static Box AusCollider(BoxCollider c)
        {
            var tr = c.transform;
            var s = tr.lossyScale;
            return new Box
            {
                Mitte = tr.TransformPoint(c.center),
                Halb = new Vector3(Mathf.Abs(c.size.x * s.x), Mathf.Abs(c.size.y * s.y), Mathf.Abs(c.size.z * s.z)) * 0.5f,
                Drehung = tr.rotation,
            };
        }

        static RaumDaten MessenBox(string name, Box box, int maske, Dictionary<int, string> typCache,
                                   Dictionary<string, int> zaehler, float[] summe)
        {
            float vx = box.Halb.x * 2f, vy = box.Halb.y * 2f, vz = box.Halb.z * 2f;
            float vol = vx * vy * vz;
            if (vol < MinVolumen || vol > MaxVolumen) return null;
            float flaeche = 2f * (vx * vy + vy * vz + vx * vz);

            zaehler.Clear();
            Array.Clear(summe, 0, summe.Length);
            int treffer = 0;
            var inv = Quaternion.Inverse(box.Drehung);
            float diag = Mathf.Sqrt(vx * vx + vy * vy + vz * vz);

            for (int i = 0; i < richtungen.Length; i++)
            {
                Vector3 dir = richtungen[i];
                // Strecke bis zum Boxrand + etwas Spiel (Waende liegen oft knapp ausserhalb der Box).
                Vector3 l = inv * dir;
                float t = diag;
                float tx = Mathf.Abs(l.x) > 1e-4f ? box.Halb.x / Mathf.Abs(l.x) : float.MaxValue;
                float ty = Mathf.Abs(l.y) > 1e-4f ? box.Halb.y / Mathf.Abs(l.y) : float.MaxValue;
                float tz = Mathf.Abs(l.z) > 1e-4f ? box.Halb.z / Mathf.Abs(l.z) : float.MaxValue;
                t = Mathf.Min(Mathf.Min(tx, ty), Mathf.Min(tz, diag)) + 0.6f;

                RaycastHit hit;
                if (!Physics.Raycast(box.Mitte, dir, out hit, t, maske, QueryTriggerInteraction.Ignore)) continue;
                string mat = Materialname(hit.collider, typCache);
                treffer++;
                zaehler[mat] = zaehler.TryGetValue(mat, out int c) ? c + 1 : 1;
                for (int b = 0; b < Baender.Length; b++) summe[b] += Alpha(mat, Baender[b]);
            }
            if (treffer < MinTreffer) return null;

            var d = new RaumDaten
            {
                Name = name, Box = box, Volumen = vol, VzuS = vol / flaeche, Treffer = treffer,
                Dominant = zaehler.OrderByDescending(p => p.Value).First().Key,
            };
            for (int b = 0; b < Baender.Length; b++) d.Alpha[b] = Mathf.Max(0.005f, summe[b] / treffer);
            return d;
        }

        // MaterialType-Name des getroffenen Colliders (wie MaterialCache, aber ohne Gruppenbildung).
        static string Materialname(Collider col, Dictionary<int, string> cache)
        {
            if (col == null) return "Stone";
            int id = col.GetInstanceID();
            if (cache.TryGetValue(id, out string s)) return s;
            string r = "Stone";
            if (col is TerrainCollider) r = "Soil";
            else
            {
                BaseBallistic bb;
                if (!col.TryGetComponent(out bb) && col.transform.parent != null)
                    col.transform.parent.TryGetComponent(out bb);
                if (bb is BallisticCollider bc) r = bc.TypeOfMaterial.ToString();
                else if (bb is TerrainBallistic) r = "Soil";
            }
            cache[id] = r;
            return r;
        }

        static float Alpha(string material, float hz)
        {
            float[] p;
            if (!Tabelle.TryGetValue(material, out p))
            {
                string a;
                if (!Aliase.TryGetValue(material, out a) || !Tabelle.TryGetValue(a, out p)) p = Tabelle["Stone"];
            }
            int n = p.Length / 2;
            if (hz <= p[0]) return p[1];
            if (hz >= p[(n - 1) * 2]) return p[(n - 1) * 2 + 1];
            for (int i = 0; i < n - 1; i++)
            {
                float f0 = p[i * 2], f1 = p[i * 2 + 2];
                if (hz > f1) continue;
                float u = Mathf.Log(hz / f0) / Mathf.Log(f1 / f0);
                return Mathf.Lerp(p[i * 2 + 1], p[i * 2 + 3], u);
            }
            return p[(n - 1) * 2 + 1];
        }

        static Vector3[] Fibonacci(int n)
        {
            var r = new Vector3[n];
            float gold = Mathf.PI * (3f - Mathf.Sqrt(5f));
            for (int i = 0; i < n; i++)
            {
                float y = 1f - 2f * (i + 0.5f) / n;
                float rad = Mathf.Sqrt(1f - y * y);
                float phi = gold * i;
                r[i] = new Vector3(Mathf.Cos(phi) * rad, y, Mathf.Sin(phi) * rad);
            }
            return r;
        }

        // ---------------------------------------------------------------- Rechnen + Zonen

        // Sabine-Korrektur gegenueber dem eingebackenen alpha 0.1.
        // Quelle: Sabine T60 = 0.161*V/(S*alpha) (SI-Form; W. C. Sabine, Collected Papers on Acoustics, 1922;
        // H. Kuttruff, Room Acoustics, 6. Aufl., CRC 2017). Fuer stark daempfende Raeume waere Eyring genauer
        // (C. F. Eyring, JASA 1, 217-241, 1930) - hier nicht verwendet.
        // Hallpegel: diffuses Feld ~ 10*log10(4/(S*alpha)) -> Differenz 10*log10(0.1/alpha) (Kuttruff; Bies & Hansen).
        // Grenzen -0.8..+1.5 s und +-6 dB sind Annahmen (nach Gehoer).
        static void Korrektur(RaumDaten d, float staerke, float[] dRt, float[] dPegel)
        {
            for (int b = 0; b < Baender.Length; b++)
            {
                float a = d.Alpha[b];
                float rt = 0.161f * d.VzuS * (1f / a - 1f / GebackenAlpha);
                dRt[b] = Mathf.Clamp(rt, -0.8f, 1.5f) * staerke;
                dPegel[b] = Mathf.Clamp(10f * Mathf.Log10(GebackenAlpha / a), -6f, 6f) * staerke;
            }
        }

        static void Erzeugen(double msMessen, bool gemessen, Stopwatch sw)
        {
            if (daten == null) return;
            float staerke = Staerke.Value;
            int max = MaxZonen.Value;
            var dRt = new float[Baender.Length];
            var dPegel = new float[Baender.Length];
            var kandidaten = daten.OrderByDescending(x => x.Volumen).Take(max).ToList();
            foreach (var x in daten) x.Angelegt = false;

            wurzel = new GameObject("Materialklang_Hallzonen");
            UnityEngine.Object.DontDestroyOnLoad(wurzel);
            int ok = 0, fehl = 0;
            var extrem = new List<(string name, string mat, float rt1k, float alpha1k, float vol)>();
            foreach (var d in kandidaten)
            {
                Korrektur(d, staerke, dRt, dPegel);
                extrem.Add((d.Name, d.Dominant, dRt[Band1k], d.Alpha[Band1k], d.Volumen));
                d.DRt1k = dRt[Band1k]; d.DPegel1k = dPegel[Band1k]; d.Inv = Quaternion.Inverse(d.Box.Drehung);
                d.Angelegt = ZoneAnlegen(d, dRt, dPegel);
                if (d.Angelegt) ok++; else fehl++;
            }
            zonenAktiv = ok;
            sw.Stop();

            if (Diagnose.Value)
            {
                string karte = SceneManager.GetActiveScene().name;
                var top = extrem.OrderByDescending(e => Mathf.Abs(e.rt1k)).Take(5)
                    .Select(e => $"{e.name} [{e.mat}, a(1k)={e.alpha1k:F2}, {e.vol:F0} m3] dRT60(1k)={e.rt1k:+0.00;-0.00} s");
                Plugin.Log.LogInfo($"[Reverb] Map {karte}: {ok} zones created ({fehl} failed) from {daten.Count} measured rooms" +
                                   $" ({(gemessen ? $"measuring {msMessen:F1} ms, " : "no new measurement, ")}total {sw.Elapsed.TotalMilliseconds:F1} ms), strength {staerke:F2}.");
                if (extrem.Count > 0) Plugin.Log.LogInfo("[Reverb] Most extreme rooms: " + string.Join(" | ", top));
            }
        }

        static bool ZoneAnlegen(RaumDaten d, float[] dRt, float[] dPegel)
        {
            try { return Zone.Anlegen(wurzel.transform, d.Name, d.Box.Mitte, d.Box.Drehung, d.Box.Halb, Baender, dRt, dPegel); }
            catch (Exception ex) { Statistik.Fehler(ex); return false; }
        }

        static void ZonenZerstoeren()
        {
            if (wurzel != null) UnityEngine.Object.Destroy(wurzel);   // Zone.OnDestroy gibt das native Objekt frei
            wurzel = null;
            zonenAktiv = 0;
        }

        // Alles, was Meta-Typen anfasst, liegt hier - so bricht ein fehlendes Meta.XR.Audio nichts anderes.
        static class Zone
        {
            [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
            public static bool Anlegen(Transform eltern, string name, Vector3 mitte, Quaternion drehung, Vector3 halb,
                                       float[] hz, float[] dRt, float[] dPegel)
            {
                var go = new GameObject("Hall_" + name);
                var tr = go.transform;
                tr.SetParent(eltern, false);
                tr.position = mitte;
                tr.rotation = drehung;
                // Die Zone ist 2 Einheiten gross (Native-Box = 2 + Fade/Scale), also Scale = halbe Raumgroesse.
                tr.localScale = new Vector3(Mathf.Max(halb.x, 0.1f), Mathf.Max(halb.y, 0.1f), Mathf.Max(halb.z, 0.1f));

                var z = go.AddComponent<MetaXRAcousticControlZone>();
                z.Rt60.points.Clear();
                z.ReverbLevel.points.Clear();
                for (int i = 0; i < hz.Length; i++)
                {
                    z.Rt60.points.Add(new Spectrum.Point(hz[i], dRt[i]));
                    z.ReverbLevel.points.Add(new Spectrum.Point(hz[i], dPegel[i]));
                }
                z.FadeDistance = 1f;
                z.StartInternal();                      // sofort anlegen statt erst im naechsten Frame
                if (z._controlHandle == IntPtr.Zero)
                {
                    UnityEngine.Object.Destroy(go);
                    return false;
                }
                return true;
            }
        }
    }

    // Raidstart: Raumspeicher fertig (Raeume initialisiert, gueltige Raeume in _validSpatialAudioRooms).
    [HarmonyPatch(typeof(AudioRoomStorage), MethodType.Constructor, new Type[0])]
    internal static class HallRaumspeicherPatch
    {
        static void Postfix(AudioRoomStorage __instance)
        {
            try { HallZonen.RaeumeMelden(__instance._validSpatialAudioRooms); KlangLog.RaidStart(); }
            catch (Exception ex) { Statistik.Fehler(ex); }
        }
    }

    // Raidende: Raumsystem wird abgebaut.
    [HarmonyPatch(typeof(SpatialAudioSystem), nameof(SpatialAudioSystem.Dispose))]
    internal static class HallDisposePatch
    {
        static void Prefix()
        {
            try { HallZonen.Leeren(); KlangLog.RaidEnde(); }
            catch (Exception ex) { Statistik.Fehler(ex); }
        }
    }
}
