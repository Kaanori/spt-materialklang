using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using BepInEx.Configuration;
using UnityEngine;

namespace Materialklang
{
    // ---------------------------------------------------------------------------------------------
    //  Wald (F12 Abschnitt 12): Laubdaempfung nach ISO 9613-2:1996, Anhang A (Rechnung: WaldRechnung.cs).
    //
    //  Wie die Baeume im Spiel stehen (nachgezaehlt in den Karten-Szenen, siehe README): als einzelne GameObjects mit LODGroup
    //  (Namen pine, pine02_big, pine04_big, birch, tree, ash, filbert_big/-small ...) - NICHT als Terrain-Baeume
    //  (alle TerrainData der Karten haben 0 treeInstances). Billboards (Fern-Attrappen), Stuempfe, Farn und Gras zaehlen nicht.
    //  Beim Raidstart wird einmal Resources.FindObjectsOfTypeAll(LODGroup) gerufen (ein kurzer Haenger im Ladevorgang), die Treffer
    //  werden ueber mehrere Bilder verteilt (2 ms je Bild) nach Namen eingestuft und in ein 10-x-10-m-Raster gezaehlt.
    //  Je Geraeusch: Strecke Quelle-Hoerer durch das Raster (DDA) -> Meter "dichtes Laub" -> Pegel + Tiefpass.
    // ---------------------------------------------------------------------------------------------
    internal static class Wald
    {
        internal static ConfigEntry<bool> Aktiv, ImLog;
        internal static ConfigEntry<float> Staerke, Schwelle;

        internal const string Sek = "12. Forest";
        internal const float MaxEntf = 400f;          // weiter entfernte Geraeusche bekommen keine Laubdaempfung (Rechenaufwand, ISO-Tabelle endet bei 200 m Laub)
        const float Zelle = 10f;
        const float SucheNach = 1f;                   // s nach Raidstart (die Szenen mit den Baeumen sind dann geladen; falls nicht: ein zweiter Versuch)
        const float Wiederholung = 20f;               // s: einmaliger zweiter Versuch, falls nichts gefunden wurde

        static volatile WaldRaster raster;

        enum Phase { Aus, Warten, Sammeln, Fertig }
        static Phase phase = Phase.Aus;
        static UnityEngine.Object[] funde;
        static int pos, versuche;
        static float[] px = new float[0], pz = new float[0], pw = new float[0];
        static int pn;
        static float naechster;
        static double msSuche, msSammeln;
        static int bilder;
        static readonly Dictionary<string, float> gewichte = new Dictionary<string, float>(64);

        // Zwischenspeicher je Quelle: gilt, solange sich Quelle und Hoerer kaum bewegt haben und nichts umgestellt wurde
        struct Eintrag { public float Qx, Qy, Qz, Hx, Hy, Hz, Luft; public int Stand; public WaldErgebnis R; }
        static readonly Dictionary<int, Eintrag> zwischen = new Dictionary<int, Eintrag>(256);
        static int stand = 1;                         // wird bei jeder Aenderung (Einstellung, Raster, Wetter) erhoeht

        // Diagnose (alle 30 s)
        static int rechnungen, treffer, zwischenTreffer;
        static double summeM, summeDb, summeHz;
        static long ticks;

        internal static bool Bereit => raster != null;
        internal static int Baeume => raster != null ? raster.Eintraege : 0;

        internal static void Binden(ConfigFile c)
        {
            Aktiv = c.Bind(Sek, "Enabled", true,
                "Foliage attenuation (ISO 9613-2, annex A): sound that travels through dense forest loses level and highs. Needs trees on the map (Woods, Shoreline, Lighthouse, Customs, Reserve ...); nothing happens in cities and buildings. Applies live; switching it on again also rescans the map.");
            Staerke = c.Bind(Sek, "Strength", 1f,
                new ConfigDescription("Factor on the ISO foliage values (1 = as in the standard's table, 0 = off). Pine trunks are bare at ear height, so a real pine forest can attenuate less than the table; lower this if forests sound too dull. Applies live.",
                    new AcceptableValueRange<float>(0f, 3f)));
            Schwelle = c.Bind(Sek, "Density threshold (trees per 100 m2)", 3f,
                new ConfigDescription("From this many (weighted) trees per 10 x 10 m cell the foliage counts as fully dense; below it a cell counts in proportion (1.5 trees = half). 3 per 100 m2 means a tree about every 6 m, enough to block the view. Bushes count half. Applies live.",
                    new AcceptableValueRange<float>(0.5f, 20f)));
            ImLog = c.Bind(Sek, "Show forest in sound log", true,
                "Adds 'forest=48 m -2.1 dB lp=5.2 kHz' (metres through dense foliage, level change, resulting low-pass) to the sound log lines, and the forest scan result to the log. Applies live.");

            Aktiv.SettingChanged += (_, __) =>
            {
                stand++;
                if (Aktiv.Value && raster == null && (phase == Phase.Fertig || phase == Phase.Aus) && RaidLaeuft) { phase = Phase.Warten; naechster = 0f; versuche = 1; }
            };
            Staerke.SettingChanged += (_, __) => stand++;
            Schwelle.SettingChanged += (_, __) => stand++;
            SebAkustik.Feuchte.SettingChanged += (_, __) => stand++;      // Klarheitsfaktor steckt im Tiefpass
            SebAkustik.LuftAktiv.SettingChanged += (_, __) => stand++;    // Luft aus -> die Luft zaehlt im Tiefpass nicht mit
        }

        static bool RaidLaeuft;

        // ---------------------------------------------------------------- Aufbau

        internal static void RaidStart()
        {
            Leeren();
            RaidLaeuft = true;
            phase = Phase.Warten; versuche = 0;
            naechster = Time.realtimeSinceStartup + SucheNach;
        }

        internal static void RaidEnde() { Leeren(); RaidLaeuft = false; phase = Phase.Aus; }

        internal static void Leeren()
        {
            raster = null; funde = null; pos = 0; pn = 0; bilder = 0;
            if (px.Length > 200000) { px = new float[0]; pz = new float[0]; pw = new float[0]; }
            zwischen.Clear(); gewichte.Clear(); stand++;
        }

        /// <summary>Jeden Frame aus Plugin.Update.</summary>
        internal static void Tick()
        {
            try
            {
                if (phase == Phase.Aus || phase == Phase.Fertig || Aktiv == null || !Aktiv.Value) return;
                if (phase == Phase.Warten)
                {
                    if (Time.realtimeSinceStartup < naechster) return;
                    Suchen();
                    return;
                }
                SammelnSchritt();
            }
            catch (Exception e) { Statistik.Fehler(e); phase = Phase.Fertig; }
        }

        static void Suchen()
        {
            long t0 = Stopwatch.GetTimestamp();
            funde = Resources.FindObjectsOfTypeAll(typeof(LODGroup));
            msSuche = (Stopwatch.GetTimestamp() - t0) * 1000.0 / Stopwatch.Frequency;
            pos = 0; pn = 0; bilder = 0; msSammeln = 0;
            phase = Phase.Sammeln;
        }

        static void Add(float x, float z, float w)
        {
            if (pn == px.Length)
            {
                int n = Math.Max(4096, px.Length * 2);
                Array.Resize(ref px, n); Array.Resize(ref pz, n); Array.Resize(ref pw, n);
            }
            px[pn] = x; pz[pn] = z; pw[pn] = w; pn++;
        }

        static void SammelnSchritt()
        {
            long t0 = Stopwatch.GetTimestamp();
            long ende = t0 + (long)(Stopwatch.Frequency * 0.002);
            bilder++;
            int n = funde.Length;
            while (pos < n)
            {
                var g = funde[pos++] as LODGroup;
                if (g != null)
                {
                    string name = g.name;
                    float w;
                    if (!gewichte.TryGetValue(name, out w))
                    {
                        w = WaldKlassen.Gewicht(name);
                        if (gewichte.Count > 4000) gewichte.Clear();
                        gewichte[name] = w;
                    }
                    if (w > 0f)
                    {
                        var go = g.gameObject;
                        if (go.scene.IsValid())            // nicht die Vorlagen (Prefabs) im Speicher
                        {
                            var p = go.transform.position;
                            Add(p.x, p.z, w);
                        }
                    }
                }
                if ((pos & 63) == 0 && Stopwatch.GetTimestamp() > ende) break;
            }
            msSammeln += (Stopwatch.GetTimestamp() - t0) * 1000.0 / Stopwatch.Frequency;
            if (pos < n) return;
            Fertigstellen(n);
        }

        static void Fertigstellen(int lodGruppen)
        {
            long t0 = Stopwatch.GetTimestamp();
            funde = null;
            if (pn == 0)
            {
                if (versuche < 1)
                {
                    versuche++; phase = Phase.Warten; naechster = Time.realtimeSinceStartup + Wiederholung;
                    Melden($"[Forest] no trees found among {lodGruppen} LOD groups - trying once more in {Wiederholung:F0} s.");
                }
                else
                {
                    phase = Phase.Fertig;
                    Melden($"[Forest] no trees on this map ({lodGruppen} LOD groups checked in {msSuche + msSammeln:F0} ms) - forest attenuation stays off.");
                }
                return;
            }
            float minX = float.MaxValue, minZ = float.MaxValue, maxX = float.MinValue, maxZ = float.MinValue;
            for (int i = 0; i < pn; i++)
            {
                float x = px[i], z = pz[i];
                if (Math.Abs(x) > 5000f || Math.Abs(z) > 5000f) continue;
                if (x < minX) minX = x; if (x > maxX) maxX = x;
                if (z < minZ) minZ = z; if (z > maxZ) maxZ = z;
            }
            if (minX > maxX) { phase = Phase.Fertig; return; }
            var r = new WaldRaster(minX - Zelle, minZ - Zelle, maxX + Zelle, maxZ + Zelle, Zelle);
            for (int i = 0; i < pn; i++) r.Add(px[i], pz[i], pw[i]);
            double ms = msSuche + msSammeln + (Stopwatch.GetTimestamp() - t0) * 1000.0 / Stopwatch.Frequency;
            raster = r; stand++; phase = Phase.Fertig;
            Melden(string.Format(CultureInfo.InvariantCulture,
                "[Forest] {0} trees/bushes counted ({1:F0} weighted, {2} LOD groups checked) in a {3} x {4} grid of {5:F0} m cells; search {6:F0} ms (one call), counting {7:F0} ms over {8} frames, total {9:F0} ms.",
                r.Eintraege, r.Summe, lodGruppen, r.Nx, r.Nz, r.Zelle, msSuche, msSammeln + (Stopwatch.GetTimestamp() - t0) * 1000.0 / Stopwatch.Frequency, bilder, ms));
        }

        static void Melden(string z)
        {
            if (ImLog != null && !ImLog.Value) return;
            Plugin.Log.LogInfo(z);
            KlangLog.Text(z);
        }

        // ---------------------------------------------------------------- Je Geraeusch

        /// <summary>
        /// Laubdaempfung fuer ein Geraeusch. 'id' = Kennung der Quelle (fuer den Zwischenspeicher), 'dLuft' = Entfernung in m, die die Luftdaempfung zaehlt.
        /// Keine Speicherzuweisung auf dem normalen Pfad.
        /// </summary>
        internal static bool Berechnen(int id, Vector3 q, Vector3 h, float dLuft, out WaldErgebnis r)
        {
            r = default(WaldErgebnis);
            var ras = raster;
            if (ras == null || Aktiv == null || !Aktiv.Value) return false;
            float st = Staerke.Value;
            if (st <= 0f) return false;

            int wetter = Wetter.Berechnungen;
            Eintrag e;
            if (zwischen.TryGetValue(id, out e) && e.Stand == stand + wetter * 1000003)
            {
                float a = (q.x - e.Qx) * (q.x - e.Qx) + (q.y - e.Qy) * (q.y - e.Qy) + (q.z - e.Qz) * (q.z - e.Qz);
                float b = (h.x - e.Hx) * (h.x - e.Hx) + (h.y - e.Hy) * (h.y - e.Hy) + (h.z - e.Hz) * (h.z - e.Hz);
                if (a < 1f && b < 1f && Math.Abs(dLuft - e.Luft) < 1f)
                {
                    zwischenTreffer++;
                    r = e.R;
                    return r.Gueltig;
                }
            }

            long t0 = Stopwatch.GetTimestamp();
            float dx = q.x - h.x, dy = q.y - h.y, dz = q.z - h.z;
            float dist = (float)Math.Sqrt(dx * dx + dy * dy + dz * dz);
            var res = default(WaldErgebnis);
            if (dist <= MaxEntf && dist >= 1f)
            {
                double eff = ras.Laenge(h.x, h.z, q.x, q.z, Schwelle.Value);
                if (eff >= 0.5)
                {
                    double df = Math.Min(eff, Laub.MaxLaenge);
                    double lp = Laub.Grenzfrequenz(dLuft, df, st, Wetter.Baender);
                    res.Gueltig = true;
                    res.Meter = (float)df;
                    res.Db = (float)Laub.PegelDb(df, st);
                    res.LpHz = lp >= 22000.0 ? 22000f : (float)Math.Min(22000.0, lp * SebAkustik.Feuchte.Value);   // Klarheitsfaktor wie bei der Luft
                }
            }
            ticks += Stopwatch.GetTimestamp() - t0;
            rechnungen++;
            if (res.Gueltig) { treffer++; summeM += res.Meter; summeDb += res.Db; summeHz += res.LpHz; }

            if (zwischen.Count > 600) zwischen.Clear();
            zwischen[id] = new Eintrag { Qx = q.x, Qy = q.y, Qz = q.z, Hx = h.x, Hy = h.y, Hz = h.z, Luft = dLuft, Stand = stand + wetter * 1000003, R = res };
            r = res;
            return res.Gueltig;
        }

        internal static float Verstaerkung(float db) => Mathf.Pow(10f, -db / 20f);

        internal static void Loggen()
        {
            if (rechnungen + zwischenTreffer == 0) return;
            if (raster != null)
                Plugin.Log.LogInfo(string.Format(CultureInfo.InvariantCulture,
                    "[Diagnostics] Forest: {0} calculations ({1} from the per-source cache), {2} with foliage (mean {3:F0} m, -{4:F1} dB, low-pass {5:F0} Hz); mean cost {6:F1} us per calculation.",
                    rechnungen, zwischenTreffer, treffer, treffer > 0 ? summeM / treffer : 0, treffer > 0 ? summeDb / treffer : 0, treffer > 0 ? summeHz / treffer : 0,
                    rechnungen > 0 ? ticks * 1e6 / Stopwatch.Frequency / rechnungen : 0));
            rechnungen = treffer = zwischenTreffer = 0; summeM = summeDb = summeHz = 0; ticks = 0;
        }
    }
}
