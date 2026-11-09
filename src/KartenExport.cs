using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using BepInEx.Configuration;
using Comfort.Common;
using EFT;
using EFT.Interactive;
using Unity.Collections;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Materialklang
{
    // ------------------------------------------------------------------------------------------
    //  0.8.4 - Kartenexport fuer den Sandkasten der Demo-Seite (F12 > 11. Sound log & live tuning > "Export map for sandbox").
    //  Waagerechter Schnitt durch die Kollisionsgeometrie, gegen die auch die Schallstrahlen laufen, in Ohrhoehe:
    //   - Collider: duenne Scheiben-Abfragen (OverlapBox, 2 cm hoch) in Kacheln ueber die Karte, Trigger aus, Terrain aus,
    //     Spieler/Bots/lose Gegenstaende (nicht kinematischer Rigidbody) aus, Tueren und Fenster getrennt.
    //   - Schnitt: Quader -> gedrehtes Schnittpolygon, Mesh -> Dreiecke (nur lesbare; sonst Bounds-Rechteck als Notloesung),
    //     Kugel/Kapsel -> Kreis-Polygone. Rechnung in KartenSchnitt.cs (ohne Unity, Offline-Test).
    //   - Material ueber dieselbe Zuordnung wie die Strahlen (MaterialCache.Bestimmen), Dicke: Quader = kleinere Breite des
    //     Schnitts, Mesh-Flaeche = Gruppendicke (Bauakustik.EinzelDicke) bzw. Abstand eines Flaechenpaares.
    //  Verteilt ueber mehrere Bilder (Coroutine, ~6 ms je Bild), Fortschritt im Log.
    //  0.8.5 - Standard ist jetzt der Scan ("Export method" = Scan): auf Customs waren 5247 von 5266 MeshCollidern nicht lesbar
    //  (isReadable = false) und kamen nur als Bounds-Rechteck heraus. Die PhysX-Kollisionsform ist aber immer abfragbar: waagerechte
    //  Strahlen entlang Gitterlinien in x- und z-Richtung (RaycastCommand, auf den Arbeitsfaeden, im selben Bild fertig), vorwaerts
    //  = Eintritte, rueckwaerts = Austritte, schrittweise von Treffer zu Treffer; Rechnung (Raster, Umriss, Mittellinien, JSON) in
    //  KartenScan (KartenSchnitt.cs) im Hintergrundfaden. Der alte Schnitt bleibt als "Mesh".
    // ------------------------------------------------------------------------------------------
    internal static class KartenExport
    {
        internal enum Methode { Scan, Mesh }
        internal static ConfigEntry<bool> Ausloeser, AbFuessen;
        internal static ConfigEntry<float> Hoehe, Radius, ScanRadius, ScanRaster;
        internal static ConfigEntry<Methode> Art;

        static bool angefordert, laeuft;
        const double BudgetMs = 6.0;
        const float KachelStart = 128f, KachelMin = 4f, GanzeKarte = 1500f;
        static readonly Collider[] puffer = new Collider[8192];

        internal static void Binden(ConfigFile c)
        {
            string s = KlangLog.Sek;
            Ausloeser = c.Bind(s, "Export map for sandbox", false,
                "Tick during a raid: exports a horizontal cut through the level's collision geometry (walls, doors, windows) at ear height to BepInEx/Materialklang-Logs/sandbox-<map>-<height>.json, for the 'Load map' button of the demo page. Spread over frames (scan: roughly 10-60 s for 300 m, progress in the BepInEx log), confirmed with a sound. Resets itself.");
            Hoehe = c.Bind(s, "Export slice height above ground (m)", 1.6f,
                new ConfigDescription("Height of the cut above the ground below you (1.6 = ear height).", new AcceptableValueRange<float>(0.2f, 10f)));
            Art = c.Bind(s, "Export method", Methode.Scan,
                "Scan = probe the collision shapes with physics rays on a grid (works for every collider; most of the game's mesh colliders cannot be read). " +
                "Mesh = cut the collider data directly (exact, but unreadable meshes only come out as their bounds rectangle).");
            ScanRadius = c.Bind(s, "Export scan radius (m)", 300f,
                new ConfigDescription("Scan: square of +- this many metres around you. 0 = the map's bounds (limited to 3200 grid cells per side around you).", new AcceptableValueRange<float>(0f, 1000f)));
            ScanRaster = c.Bind(s, "Export scan resolution (m)", 0.25f,
                new ConfigDescription("Scan: grid spacing. Smaller = finer, but the time grows with 1/spacing^2 (0.25 m and 300 m: 2400 lines per direction).", new AcceptableValueRange<float>(0.05f, 2f)));
            Radius = c.Bind(s, "Export radius (m)", 0f,
                new ConfigDescription("Mesh method only: export a square of +- this many metres around you. 0 = whole map.", new AcceptableValueRange<float>(0f, 2000f)));
            AbFuessen = c.Bind(s, "Export slice from your feet", false,
                "Off = the cut is at (ground below you + height). On = at (your feet + height), e.g. on a roof or a stair landing.");
            Ausloeser.SettingChanged += (_, __) =>
            {
                if (!Ausloeser.Value) return;
                angefordert = true;
                Ausloeser.Value = false;      // Knopf: springt sofort zurueck (loest SettingChanged noch einmal aus -> oben return)
            };
        }

        /// <summary>Aus Plugin.Update: angeforderten Export starten (nicht aus OnGUI des F12-Menues heraus).</summary>
        internal static void Tick()
        {
            if (!angefordert) return;
            angefordert = false;
            if (laeuft) { Plugin.Log.LogInfo("Map export: already running."); return; }
            if (!Singleton<GameWorld>.Instantiated || Singleton<GameWorld>.Instance.MainPlayer == null || Plugin.Instanz == null)
            {
                Plugin.Log.LogInfo("Map export: only works during a raid.");
                Ton(EFT.UI.EUISoundType.ErrorMessage);
                return;
            }
            Plugin.Instanz.StartCoroutine(Ablauf());
        }

        static void Ton(EFT.UI.EUISoundType art)
        {
            try { Singleton<EFT.UI.GUISounds>.Instance?.PlayUISound(art); }
            catch (Exception e) { Statistik.Fehler(e); }
        }

        // ------------------------------------------------------------------ Zaehler

        sealed class Zahlen
        {
            public int Kacheln, Collider, Quader, Mesh, MeshNichtLesbar, MeshOhne, Kugel, Kapsel, AndereArt;
            public int Terrain, Dynamisch, Spieler, TuerTeile, FensterTeile, Dreiecke, RohSegmente, Paare, Waende;
            public int Tueren, Fenster, TuerenAusserhalb;
            public double Ms;
            // Scan
            public long Strahlen, Treffer, Abbrueche;
            public int Linien, ZellenW, ZellenH, Slots;
            public double MsStrahlen, Bilder;
            public KartenScan.Zahlen Scan;
        }

        // ------------------------------------------------------------------ Ablauf

        static IEnumerator Ablauf()
        {
            laeuft = true;
            var uhr = Stopwatch.StartNew();
            var bild = Stopwatch.StartNew();
            var z = new Zahlen();
            var roh = new List<Seg>(65536);
            var oeff = new List<string>(512);
            string karte = "map", datei = null;
            float sliceY = 0f, boden = 0f;
            Vector3 pos = Vector3.zero;
            bool ok = false;
            IEnumerator innen = null;
            ScanAuftrag scan = null;
            try
            {
                var gw = Singleton<GameWorld>.Instance;
                pos = gw.MainPlayer.Position;
                karte = !string.IsNullOrEmpty(gw.LocationId) ? gw.LocationId : SceneManager.GetActiveScene().name;
                boden = pos.y;
                if (!AbFuessen.Value && Physics.Raycast(pos + Vector3.up * 0.5f, Vector3.down, out var hit, 4f,
                        (int)LayersMaskController.HighPolyWithTerrainMask, QueryTriggerInteraction.Ignore))
                    boden = hit.point.y;
                sliceY = boden + Hoehe.Value;
                if (Art.Value == Methode.Scan)
                {
                    scan = new ScanAuftrag();
                    innen = ScanSammeln(pos, sliceY, z, scan, oeff, bild);
                    Plugin.Log.LogInfo($"Map export (scan): {karte}, cut at y = {sliceY:F2} m ({Hoehe.Value:F2} m above {(AbFuessen.Value ? "your feet" : "the ground")}), " +
                                       (ScanRadius.Value > 0f ? $"+-{ScanRadius.Value:F0} m around you" : "map bounds") + $", grid {ScanRaster.Value:F2} m ...");
                }
                else
                {
                    innen = Sammeln(pos, sliceY, z, roh, oeff, bild);
                    Plugin.Log.LogInfo($"Map export (mesh): {karte}, cut at y = {sliceY:F2} m ({Hoehe.Value:F2} m above {(AbFuessen.Value ? "your feet" : "the ground")}), " +
                                       (Radius.Value > 0f ? $"+-{Radius.Value:F0} m around you" : "whole map") + " ...");
                }
            }
            catch (Exception e) { Plugin.Log.LogError("Map export failed: " + e); laeuft = false; Ton(EFT.UI.EUISoundType.ErrorMessage); yield break; }

            // Coroutine im Inneren schrittweise; Fehler darin brechen den Export ab (yield geht nicht in try/catch)
            while (true)
            {
                bool weiter;
                try { weiter = innen.MoveNext(); }
                catch (Exception e) { Plugin.Log.LogError("Map export failed: " + e); weiter = false; z = null; }
                if (!weiter) break;
                yield return null;
                bild.Restart();
            }

            if (z != null)
            {
                // Zusammenfassen + JSON im Hintergrundfaden (reine Rechnung, bei vielen Mesh-Stuecken > 1 s)
                string json = null; List<Seg> waende = null; Exception fehler = null; long msMerge = 0;
                z.Ms = uhr.Elapsed.TotalMilliseconds;
                var zz = z; string kk = karte; float sy = sliceY, bo = boden; Vector3 pp = pos; float hh = Hoehe.Value; var sc = scan;
                string[] namen = Enum.GetNames(typeof(Gruppe));
                var task = System.Threading.Tasks.Task.Run(() =>
                {
                    try
                    {
                        var t0 = Stopwatch.StartNew();
                        if (sc != null)
                        {
                            zz.Scan = sc.Zahlen;
                            waende = KartenScan.Auswerten(sc.Raster, sc.SlotMat, sc.SlotDicke, sc.Oeffnungen, sc.Zahlen, KartenSchnitt.DuennGrenze, (int)Gruppe.Unknown);
                            sc.Raster = null;
                        }
                        else waende = KartenSchnitt.Zusammenfassen(roh, out zz.Paare);
                        zz.Waende = waende.Count;
                        msMerge = t0.ElapsedMilliseconds;
                        json = Json(kk, sy, hh, bo, pp, waende, oeff, namen, zz);
                    }
                    catch (Exception e) { fehler = e; }
                });
                while (!task.IsCompleted) yield return null;
                try
                {
                    if (fehler != null || json == null) throw fehler ?? new Exception("no result");
                    string dir = Path.Combine(BepInEx.Paths.BepInExRootPath, "Materialklang-Logs");
                    Directory.CreateDirectory(dir);
                    datei = Path.Combine(dir, KartenSchnitt.Dateiname(karte, Hoehe.Value));
                    File.WriteAllText(datei, json, new UTF8Encoding(false));
                    ok = true;
                    if (scan != null)
                    {
                        var q = z.Scan;
                        Plugin.Log.LogInfo($"Map export (scan): {z.Waende} walls ({q.Kanten} outline pieces of thick parts in {q.Umrisse} outlines, {q.Mitten} centre-line pieces of thin walls in {q.Linien} lines), " +
                                           $"{z.Tueren} doors, {z.Fenster} windows; grid {z.ZellenW} x {z.ZellenH} cells, {q.FesteZellen} solid ({q.DickeZellen} thick, {q.DuenneZellen} thin, skeleton {q.Skelett}, " +
                                           $"{q.AesteWeg} spurs removed, {q.OeffnungZellen} cleared in openings); {z.Linien} lines, {z.Strahlen} casts, {z.Treffer} hits ({q.Paare} entry/exit pairs, {q.Einzel} single surfaces), " +
                                           $"{z.Slots} colliders (left out: moving {z.Dynamisch}, players/bots {z.Spieler}, door parts {z.TuerTeile}, window parts {z.FensterTeile}), " +
                                           $"{(z.Abbrueche > 0 ? z.Abbrueche + " lines stopped at the hit limit, " : "")}casting {z.MsStrahlen / 1000.0:F1} s main thread over {z.Bilder:F0} frames, " +
                                           $"{uhr.Elapsed.TotalSeconds:F1} s total (contours {msMerge} ms, off the main thread) -> {datei} ({new FileInfo(datei).Length / 1024} KB)");
                    }
                    else
                    Plugin.Log.LogInfo($"Map export: {z.Waende} walls (from {z.RohSegmente} raw pieces, {z.Paare} face pairs joined), {z.Tueren} doors, {z.Fenster} windows; " +
                                       $"{z.Collider} colliders (box {z.Quader}, mesh {z.Mesh}, unreadable mesh {z.MeshNichtLesbar} = bounds rectangle, sphere {z.Kugel}, capsule {z.Kapsel}; " +
                                       $"left out: terrain {z.Terrain}, moving {z.Dynamisch}, players/bots {z.Spieler}, door parts {z.TuerTeile}, window parts {z.FensterTeile}), " +
                                       $"{z.Dreiecke} triangles cut, {uhr.Elapsed.TotalSeconds:F1} s (merge {msMerge} ms, off the main thread) -> {datei}");
                    if (scan == null && z.MeshNichtLesbar > 0)
                        Plugin.Log.LogWarning($"Map export: {z.MeshNichtLesbar} mesh colliders are not readable; they are exported as their bounds rectangle (\"approx\":1).");
                }
                catch (Exception e) { Plugin.Log.LogError("Map export failed: " + e); }
            }
            laeuft = false;
            Ton(ok ? EFT.UI.EUISoundType.QuestSubTrackComplete : EFT.UI.EUISoundType.ErrorMessage);
        }

        // ------------------------------------------------------------------ Scan (0.8.5)

        sealed class ScanAuftrag
        {
            public ScanRaster Raster;
            public readonly List<int> SlotMat = new List<int>(4096);
            public readonly List<float> SlotDicke = new List<float>(4096);
            public readonly List<(P2 a, P2 b)> Oeffnungen = new List<(P2 a, P2 b)>(512);
            public readonly KartenScan.Zahlen Zahlen = new KartenScan.Zahlen();
        }

        struct Strahl { public int Linie; public bool Rueck; public float T; public int Treffer; }

        const int MaxZellen = 3200;          // je Seite (Speicher: 5 Byte je Zelle + ~6 Byte waehrend der Auswertung)
        const int MaxBatch = 16384;
        const float Schritt = 0.005f;        // hinter einem Treffer weiter (m)
        const int MaxTrefferJeStrahl = 20000;

        static IEnumerator ScanSammeln(Vector3 pos, float y, Zahlen z, ScanAuftrag auf, List<string> oeff, Stopwatch bild)
        {
            int maske = Maske();
            float res = ScanRaster.Value, r = ScanRadius.Value;
            float x0, x1, z0, z1;
            if (r > 0f) { x0 = pos.x - r; x1 = pos.x + r; z0 = pos.z - r; z1 = pos.z + r; }
            else
            {
                x0 = z0 = float.MaxValue; x1 = z1 = float.MinValue;
                foreach (var t in UnityEngine.Object.FindObjectsOfType<TerrainCollider>())
                {
                    if (t == null) continue;
                    var tb = t.bounds;
                    x0 = Mathf.Min(x0, tb.min.x); z0 = Mathf.Min(z0, tb.min.z); x1 = Mathf.Max(x1, tb.max.x); z1 = Mathf.Max(z1, tb.max.z);
                }
                if (x0 > x1) { x0 = pos.x - GanzeKarte; x1 = pos.x + GanzeKarte; z0 = pos.z - GanzeKarte; z1 = pos.z + GanzeKarte; }
            }
            // auf MaxZellen je Seite um den Spieler begrenzen
            float halb = MaxZellen * res * 0.5f;
            bool gekappt = false;
            if (x1 - x0 > 2 * halb) { x0 = Mathf.Max(x0, Mathf.Min(pos.x, x1 - halb) - halb); x1 = x0 + 2 * halb; gekappt = true; }
            if (z1 - z0 > 2 * halb) { z0 = Mathf.Max(z0, Mathf.Min(pos.z, z1 - halb) - halb); z1 = z0 + 2 * halb; gekappt = true; }
            int W = Mathf.Max(1, Mathf.CeilToInt((x1 - x0) / res)), H = Mathf.Max(1, Mathf.CeilToInt((z1 - z0) / res));
            if (gekappt) Plugin.Log.LogWarning($"Map export (scan): area limited to {MaxZellen} cells per side around you ({2 * halb:F0} m at {res:F2} m).");
            auf.Raster = new ScanRaster(W, H, x0, z0, res);
            z.ZellenW = W; z.ZellenH = H; z.Linien = W + H;
            float lx = W * res, lz = H * res;
            Plugin.Log.LogInfo($"Map export (scan): x {x0:F0}..{x0 + lx:F0}, z {z0:F0}..{z0 + lz:F0} m, {W} x {H} cells, {H} + {W} lines, each cast forwards and backwards ...");

            // Strahlen: Linie 0..H-1 = x-Linien (Zeile k), H..H+W-1 = z-Linien (Spalte k-H)
            int linien = W + H;
            var offen = new int[linien];          // offene Richtungen je Linie (2 -> 0: fertig)
            var ev = new List<ScanTreffer>[linien];
            var aktiv = new Strahl[2 * linien];
            int nAktiv = 0;
            for (int k = 0; k < linien; k++)
            {
                offen[k] = 2;
                aktiv[nAktiv++] = new Strahl { Linie = k, Rueck = false };
                aktiv[nAktiv++] = new Strahl { Linie = k, Rueck = true };
            }
            var klasse = new Dictionary<int, int>(8192);   // Collider-Id -> Slot (-1 = weglassen)
            float schale = WandZwischenRaeumen.Schale != null ? WandZwischenRaeumen.Schale.Value : 0.15f;
            var qp = new QueryParameters(maske, false, QueryTriggerInteraction.Ignore, false);
            var cmds = new NativeArray<RaycastCommand>(MaxBatch, Allocator.Persistent);
            var hits = new NativeArray<RaycastHit>(MaxBatch, Allocator.Persistent);
            var batch = new Strahl[MaxBatch];
            int groesse = 1024, fertigeLinien = 0;
            var guss = new Stopwatch();
            float naechsteMeldung = Time.realtimeSinceStartup + 2f;
            try
            {
                while (nAktiv > 0)
                {
                    if (bild.Elapsed.TotalMilliseconds > BudgetMs)
                    {
                        z.Bilder++;
                        if (Time.realtimeSinceStartup >= naechsteMeldung)
                        {
                            naechsteMeldung = Time.realtimeSinceStartup + 2f;
                            Plugin.Log.LogInfo($"Map export (scan): {fertigeLinien}/{linien} lines, {z.Strahlen} casts, {z.Treffer} hits, {nAktiv} rays running ...");
                        }
                        yield return null;
                        continue;
                    }
                    int n = Math.Min(groesse, nAktiv);
                    for (int i = 0; i < n; i++)
                    {
                        var st = aktiv[--nAktiv];
                        batch[i] = st;
                        bool xl = st.Linie < H;
                        int k = xl ? st.Linie : st.Linie - H;
                        float len = xl ? lx : lz;
                        Vector3 dir = xl ? Vector3.right : Vector3.forward;
                        Vector3 a = xl ? new Vector3(x0, y, z0 + (k + 0.5f) * res) : new Vector3(x0 + (k + 0.5f) * res, y, z0);
                        Vector3 von = st.Rueck ? a + dir * (len - st.T) : a + dir * st.T;
                        cmds[i] = new RaycastCommand(von, st.Rueck ? -dir : dir, qp, Mathf.Max(0.001f, len - st.T));
                    }
                    guss.Restart();
                    var job = RaycastCommand.ScheduleBatch(cmds.GetSubArray(0, n), hits.GetSubArray(0, n), 64, 1);
                    job.Complete();    // im selben Bild fertig: keine Abfrage laeuft in die naechste Physik-Rechnung hinein
                    z.Strahlen += n;
                    for (int i = 0; i < n; i++)
                    {
                        var st = batch[i];
                        var h = hits[i];
                        int id = h.colliderInstanceID;
                        bool xl = st.Linie < H;
                        float len = xl ? lx : lz;
                        if (id == 0) { LinieFertig(auf, offen, ev, st.Linie, H, z, ref fertigeLinien); continue; }
                        z.Treffer++;
                        if (!klasse.TryGetValue(id, out int slot)) { slot = Klassifizieren(h.collider, schale, z, auf); klasse[id] = slot; }
                        float t = st.T + h.distance;
                        if (slot >= 0)
                        {
                            Vector3 dir = xl ? Vector3.right : Vector3.forward;
                            float cos = Mathf.Abs(Vector3.Dot(h.normal, dir));
                            (ev[st.Linie] ??= new List<ScanTreffer>(64)).Add(new ScanTreffer(st.Rueck ? len - t : t, slot, !st.Rueck, cos));
                        }
                        st.T = t + Schritt;
                        st.Treffer++;
                        if (st.T >= len || st.Treffer >= MaxTrefferJeStrahl)
                        {
                            if (st.Treffer >= MaxTrefferJeStrahl) z.Abbrueche++;
                            LinieFertig(auf, offen, ev, st.Linie, H, z, ref fertigeLinien);
                            continue;
                        }
                        aktiv[nAktiv++] = st;
                    }
                    guss.Stop();
                    z.MsStrahlen += guss.Elapsed.TotalMilliseconds;
                    // Stapelgroesse an das Bildbudget anpassen (Arbeitsfaeden + Auswertung im Hauptfaden)
                    double proStrahl = guss.Elapsed.TotalMilliseconds / n;
                    int ziel = (int)Math.Max(256, Math.Min(MaxBatch, BudgetMs * 0.5 / Math.Max(proStrahl, 1e-5)));
                    groesse = ziel;
                }
            }
            finally
            {
                cmds.Dispose(); hits.Dispose();
            }
            z.Slots = auf.SlotMat.Count;
            Plugin.Log.LogInfo($"Map export (scan): {z.Strahlen} casts, {z.Treffer} hits, {z.Slots} colliders, {z.MsStrahlen / 1000.0:F1} s casting; outlines next ...");
            yield return null;
            try { Oeffnungen(pos, y, x0, x0 + lx, z0, z0 + lz, z, oeff, auf.Oeffnungen); }
            catch (Exception e) { Statistik.Fehler(e); }
        }

        static void LinieFertig(ScanAuftrag auf, int[] offen, List<ScanTreffer>[] ev, int linie, int H, Zahlen z, ref int fertig)
        {
            if (--offen[linie] > 0) return;
            bool xl = linie < H;
            KartenScan.LinieEintragen(auf.Raster, xl, xl ? linie : linie - H, ev[linie], auf.SlotDicke, auf.Zahlen);
            ev[linie] = null;
            fertig++;
        }

        /// <summary>Wie beim Mesh-Schnitt: Trigger, bewegliche Teile, Spieler/Bots, lose Gegenstaende, Tueren und Fenster weglassen
        /// (Strahl laeuft hindurch); sonst neuer Slot mit Material und Gruppendicke.</summary>
        static int Klassifizieren(Collider c, float schale, Zahlen z, ScanAuftrag auf)
        {
            if (c == null || c is TerrainCollider || c.isTrigger) return -1;
            var rb = c.attachedRigidbody;
            if (rb != null && !rb.isKinematic) { z.Dynamisch++; return -1; }
            if (c.GetComponentInParent<Player>() != null) { z.Spieler++; return -1; }
            if (c.GetComponentInParent<LootItem>() != null) { z.Dynamisch++; return -1; }
            if (c.GetComponentInParent<Door>() != null) { z.TuerTeile++; return -1; }
            if (c.GetComponentInParent<WindowBreaker>() != null) { z.FensterTeile++; return -1; }
            if (auf.SlotMat.Count >= 65534) return auf.SlotMat.Count - 1;
            int mat = MaterialCache.Bestimmen(c, false);
            auf.SlotMat.Add(mat);
            auf.SlotDicke.Add(Bauakustik.EinzelDicke(mat, schale));
            return auf.SlotMat.Count - 1;
        }

        static int Maske()
        {
            // Ebenen wie die Schallstrahlen (Plugin.cs: Statistik.AudioMaske; vor dem ersten Klang: HighPolyCollider + Terrain), ohne
            // Terrain, Spieler, Leichen und Trefferzonen.
            int maske = Statistik.AudioMaske != 0 ? Statistik.AudioMaske : (int)LayersMaskController.HighPolyWithTerrainMask;
            maske |= (int)LayersMaskController.HighPolyCollider;
            maske &= ~(1 << LayersMaskController.TerrainLayer);
            maske &= ~(int)LayersMaskController.PlayerMask;
            if (LayersMaskController.DeadbodyLayer >= 0) maske &= ~(1 << LayersMaskController.DeadbodyLayer);
            if (LayersMaskController.HitColliderLayer >= 0) maske &= ~(1 << LayersMaskController.HitColliderLayer);
            return maske;
        }

        // ------------------------------------------------------------------ Sammeln

        static IEnumerator Sammeln(Vector3 pos, float y, Zahlen z, List<Seg> roh, List<string> oeff, Stopwatch bild)
        {
            int maske = Maske();

            // Gebiet
            float r = Radius.Value;
            float x0, x1, z0, z1;
            if (r > 0f) { x0 = pos.x - r; x1 = pos.x + r; z0 = pos.z - r; z1 = pos.z + r; }
            else
            {
                x0 = pos.x - GanzeKarte; x1 = pos.x + GanzeKarte; z0 = pos.z - GanzeKarte; z1 = pos.z + GanzeKarte;
                // Terrain-Flaechen (falls groesser) - ueber ihre Collider, TerrainModule ist nicht referenziert
                foreach (var t in UnityEngine.Object.FindObjectsOfType<TerrainCollider>())
                {
                    if (t == null) continue;
                    var tb = t.bounds;
                    x0 = Mathf.Min(x0, tb.min.x); z0 = Mathf.Min(z0, tb.min.z); x1 = Mathf.Max(x1, tb.max.x); z1 = Mathf.Max(z1, tb.max.z);
                }
            }

            // Kacheln (Viertelung, wenn eine Abfrage den Puffer fuellt)
            var gesehen = new HashSet<int>();
            var stapel = new Stack<(float x, float z, float s)>();
            for (float kx = x0; kx < x1; kx += KachelStart)
                for (float kz = z0; kz < z1; kz += KachelStart)
                    stapel.Push((kx, kz, KachelStart));
            var liste = new List<Collider>(16384);
            int kachelnGesamt = stapel.Count;
            float naechsteMeldung = Time.realtimeSinceStartup + 1f;
            while (stapel.Count > 0)
            {
                var k = stapel.Pop();
                float sx = Mathf.Min(k.s, x1 - k.x), sz = Mathf.Min(k.s, z1 - k.z);
                if (sx <= 0f || sz <= 0f) continue;
                var mitte = new Vector3(k.x + sx * 0.5f, y, k.z + sz * 0.5f);
                int n = Physics.OverlapBoxNonAlloc(mitte, new Vector3(sx * 0.5f, 0.01f, sz * 0.5f), puffer, Quaternion.identity, maske, QueryTriggerInteraction.Ignore);
                z.Kacheln++;
                if (n >= puffer.Length && k.s > KachelMin)
                {
                    float h = k.s * 0.5f;
                    stapel.Push((k.x, k.z, h)); stapel.Push((k.x + h, k.z, h)); stapel.Push((k.x, k.z + h, h)); stapel.Push((k.x + h, k.z + h, h));
                    continue;
                }
                for (int i = 0; i < n; i++)
                {
                    var c = puffer[i];
                    if (c != null && gesehen.Add(c.GetInstanceID())) liste.Add(c);
                    puffer[i] = null;
                }
                if (bild.Elapsed.TotalMilliseconds > BudgetMs) yield return null;
            }
            Plugin.Log.LogInfo($"Map export: {liste.Count} colliders in {z.Kacheln} queries, cutting ...");

            // Schneiden
            float schale = WandZwischenRaeumen.Schale != null ? WandZwischenRaeumen.Schale.Value : 0.15f;
            var meshCache = new Dictionary<int, (Vector3[] v, int[] t)>();
            var poly = new List<P2>();
            for (int i = 0; i < liste.Count; i++)
            {
                if (bild.Elapsed.TotalMilliseconds > BudgetMs)
                {
                    if (Time.realtimeSinceStartup >= naechsteMeldung)
                    {
                        naechsteMeldung = Time.realtimeSinceStartup + 1f;
                        Plugin.Log.LogInfo($"Map export: {i}/{liste.Count} colliders, {roh.Count} pieces ...");
                    }
                    yield return null;
                }
                var c = liste[i];
                if (c == null) continue;
                try { Schneiden(c, y, schale, z, roh, meshCache); }
                catch (Exception e) { Statistik.Fehler(e); }
            }
            z.RohSegmente = roh.Count;
            yield return null;

            // Tueren und Fenster
            try { Oeffnungen(pos, y, x0, x1, z0, z1, z, oeff); }
            catch (Exception e) { Statistik.Fehler(e); }
        }

        static void Schneiden(Collider c, float y, float schale, Zahlen z, List<Seg> roh, Dictionary<int, (Vector3[] v, int[] t)> meshCache)
        {
            if (c is TerrainCollider) { z.Terrain++; return; }
            if (c.isTrigger || !c.enabled) return;
            var rb = c.attachedRigidbody;
            if (rb != null && !rb.isKinematic) { z.Dynamisch++; return; }
            if (c.GetComponentInParent<Player>() != null) { z.Spieler++; return; }
            if (c.GetComponentInParent<LootItem>() != null) { z.Dynamisch++; return; }
            if (c.GetComponentInParent<Door>() != null) { z.TuerTeile++; return; }
            if (c.GetComponentInParent<WindowBreaker>() != null) { z.FensterTeile++; return; }
            z.Collider++;
            int mat = MaterialCache.Bestimmen(c, false);
            var t = c.transform;
            switch (c)
            {
                case BoxCollider b:
                {
                    z.Quader++;
                    Vector3 m = t.TransformPoint(b.center), h = b.size * 0.5f;
                    var poly = KartenSchnitt.Quader(P(m), P(t.TransformVector(new Vector3(h.x, 0, 0))), P(t.TransformVector(new Vector3(0, h.y, 0))),
                        P(t.TransformVector(new Vector3(0, 0, h.z))), y);
                    KartenSchnitt.PolygonZuWand(poly, mat, false, roh);
                    break;
                }
                case SphereCollider s:
                {
                    z.Kugel++;
                    Vector3 sc = t.lossyScale;
                    float rr = s.radius * Mathf.Max(Mathf.Abs(sc.x), Mathf.Abs(sc.y), Mathf.Abs(sc.z));
                    KartenSchnitt.PolygonZuWand(KartenSchnitt.Kugel(P(t.TransformPoint(s.center)), rr, y), mat, false, roh);
                    break;
                }
                case CapsuleCollider k:
                {
                    z.Kapsel++;
                    Vector3 sc = t.lossyScale, achse = k.direction == 0 ? Vector3.right : k.direction == 1 ? Vector3.up : Vector3.forward;
                    float sA = k.direction == 0 ? Mathf.Abs(sc.x) : k.direction == 1 ? Mathf.Abs(sc.y) : Mathf.Abs(sc.z);
                    float sR = k.direction == 0 ? Mathf.Max(Mathf.Abs(sc.y), Mathf.Abs(sc.z)) : k.direction == 1 ? Mathf.Max(Mathf.Abs(sc.x), Mathf.Abs(sc.z)) : Mathf.Max(Mathf.Abs(sc.x), Mathf.Abs(sc.y));
                    float rr = k.radius * sR, halb = Mathf.Max(0f, k.height * sA * 0.5f - rr);
                    Vector3 m = t.TransformPoint(k.center), d = t.TransformDirection(achse).normalized * halb;
                    KartenSchnitt.PolygonZuWand(KartenSchnitt.Kapsel(P(m - d), P(m + d), rr, y), mat, false, roh);
                    break;
                }
                case MeshCollider mc:
                {
                    var mesh = mc.sharedMesh;
                    if (mesh == null) { z.MeshOhne++; return; }
                    if (!mesh.isReadable)
                    {
                        z.MeshNichtLesbar++;
                        Bounds bb = c.bounds;
                        var rect = new List<P2> { new P2(bb.min.x, bb.min.z), new P2(bb.max.x, bb.min.z), new P2(bb.max.x, bb.max.z), new P2(bb.min.x, bb.max.z) };
                        KartenSchnitt.PolygonZuWand(rect, mat, true, roh);
                        return;
                    }
                    z.Mesh++;
                    int id = mesh.GetInstanceID();
                    if (!meshCache.TryGetValue(id, out var md)) { md = (mesh.vertices, mesh.triangles); meshCache[id] = md; }
                    var mtx = t.localToWorldMatrix;
                    var v = md.v; var tri = md.t;
                    // nur die y-Koordinate vorab: die meisten Dreiecke liegen ganz ueber oder unter der Ebene
                    var wy = new float[v.Length];
                    for (int i = 0; i < v.Length; i++) wy[i] = mtx.m10 * v[i].x + mtx.m11 * v[i].y + mtx.m12 * v[i].z + mtx.m13;
                    float th = Bauakustik.EinzelDicke(mat, schale);
                    for (int i = 0; i + 2 < tri.Length; i += 3)
                    {
                        int a = tri[i], b = tri[i + 1], cc = tri[i + 2];
                        float ya = wy[a], yb = wy[b], yc = wy[cc];
                        if ((ya > y && yb > y && yc > y) || (ya < y && yb < y && yc < y)) continue;
                        z.Dreiecke++;
                        if (KartenSchnitt.Dreieck(W(mtx, v[a], ya), W(mtx, v[b], yb), W(mtx, v[cc], yc), y, out var s0, out var s1))
                            roh.Add(new Seg { AX = s0.X, AZ = s0.Z, BX = s1.X, BZ = s1.Z, Mat = mat, Dicke = Math.Max(th, 0.005f), Art = SegArt.Flaeche });
                    }
                    break;
                }
                default: z.AndereArt++; break;
            }
        }

        static P3 P(Vector3 v) => new P3(v.x, v.y, v.z);
        static P3 W(Matrix4x4 m, Vector3 v, float wy) =>
            new P3(m.m00 * v.x + m.m01 * v.y + m.m02 * v.z + m.m03, wy, m.m20 * v.x + m.m21 * v.y + m.m22 * v.z + m.m23);

        // ------------------------------------------------------------------ Tueren / Fenster

        static void Oeffnungen(Vector3 pos, float y, float x0, float x1, float z0, float z1, Zahlen z, List<string> aus, List<(P2 a, P2 b)> linien = null)
        {
            var gw = Singleton<GameWorld>.Instance;
            // Tueren: wie TuerenFenster.FindeTuer aus dem Verzeichnis der interaktiven Objekte; fehlt es, alle Door-Objekte der Szene
            var tueren = new List<Door>();
            var dict = gw.World != null ? gw.World._interactiveObjectsDictionary : null;
            if (dict != null) { foreach (var o in dict.Values) if (o is Door d) tueren.Add(d); }
            if (tueren.Count == 0) tueren.AddRange(UnityEngine.Object.FindObjectsOfType<Door>());
            var doppelt = new HashSet<int>();
            foreach (var d in tueren)
            {
                if (d == null || !doppelt.Add(d.GetInstanceID())) continue;
                var col = GroessterCollider(d.gameObject);
                if (col == null) continue;
                if (!Box(col, out Vector3 c, out Vector3 ex, out Vector3 ey, out Vector3 ez)) continue;
                // in die geschlossene Lage zurueckdrehen: rotation = GetDoorRotation(angle) * parent.rotation (WorldInteractiveObject)
                var par = d.transform.parent;
                if (par != null && d.DoorState != EDoorState.Shut && d.DoorState != EDoorState.Locked)
                {
                    Quaternion zu = d.GetDoorRotation(d.GetAngle(EDoorState.Shut)) * par.rotation;
                    Quaternion q = zu * Quaternion.Inverse(d.transform.rotation);
                    Vector3 piv = d.transform.position;
                    c = piv + q * (c - piv); ex = q * ex; ey = q * ey; ez = q * ez;
                }
                if (!ImSchnitt(col, c, ex, ey, ez, y, x0, x1, z0, z1)) { z.TuerenAusserhalb++; continue; }
                if (!KartenSchnitt.Oeffnung(P(c), P(ex), P(ey), P(ez), out var a, out var b, out _)) continue;
                string zustand = d.IsBroken ? "broken" : d.DoorState == EDoorState.Open ? "open" : "closed";
                aus.Add(Eintrag("door", a, b, zustand, TuerBlatt(d)));
                linien?.Add((a, b));
                z.Tueren++;
            }

            // Fenster: GameWorld.Windows (wie SpatialAudioPortal.SyncState); kaputt = getroffen oder von Anfang an zerbrochen
            var fenster = gw.Windows != null ? gw.Windows._iteration : null;
            if (fenster != null)
                foreach (var w in fenster)
                {
                    if (w == null) continue;
                    var col = w.GetComponent<Collider>() ?? w.ObstructiveCollider ?? GroessterCollider(w.gameObject);
                    if (col == null || !Box(col, out Vector3 c, out Vector3 ex, out Vector3 ey, out Vector3 ez)) continue;
                    if (!ImSchnitt(col, c, ex, ey, ez, y, x0, x1, z0, z1)) continue;
                    if (!KartenSchnitt.Oeffnung(P(c), P(ex), P(ey), P(ez), out var a, out var b, out _)) continue;
                    aus.Add(Eintrag("window", a, b, (w.IsDamaged || w.BrakeByDefault) ? "broken" : "closed", nameof(Gruppe.Glass)));
                    linien?.Add((a, b));
                    z.Fenster++;
                }
        }

        static bool ImSchnitt(Collider col, Vector3 c, Vector3 ex, Vector3 ey, Vector3 ez, float y, float x0, float x1, float z0, float z1)
        {
            if (c.x < x0 || c.x > x1 || c.z < z0 || c.z > z1) return false;
            float hy = Mathf.Abs(ex.y) + Mathf.Abs(ey.y) + Mathf.Abs(ez.y);   // halbe senkrechte Ausdehnung
            return y >= c.y - hy - 0.3f && y <= c.y + hy + 0.3f;
        }

        static Collider GroessterCollider(GameObject g)
        {
            Collider best = null; float gr = -1f;
            foreach (var c in g.GetComponentsInChildren<Collider>(true))
            {
                if (c == null || c.isTrigger) continue;
                float s = c.bounds.size.sqrMagnitude;
                if (s > gr) { gr = s; best = c; }
            }
            return best;
        }

        /// <summary>Gedrehter Quader eines Colliders in Weltkoordinaten (Mitte + halbe Kantenvektoren).</summary>
        static bool Box(Collider col, out Vector3 c, out Vector3 ex, out Vector3 ey, out Vector3 ez)
        {
            var t = col.transform;
            Vector3 lc, lh;
            if (col is BoxCollider b) { lc = b.center; lh = b.size * 0.5f; }
            else if (col is MeshCollider m && m.sharedMesh != null) { lc = m.sharedMesh.bounds.center; lh = m.sharedMesh.bounds.extents; }
            else
            {
                var bb = col.bounds;
                c = bb.center; ex = new Vector3(bb.extents.x, 0, 0); ey = new Vector3(0, bb.extents.y, 0); ez = new Vector3(0, 0, bb.extents.z);
                return bb.size.sqrMagnitude > 0f;
            }
            c = t.TransformPoint(lc);
            ex = t.TransformVector(new Vector3(lh.x, 0, 0)); ey = t.TransformVector(new Vector3(0, lh.y, 0)); ez = t.TransformVector(new Vector3(0, 0, lh.z));
            return true;
        }

        /// <summary>Material des Tuerblatts: groesster BallisticCollider (wie TuerenFenster.TuerWert), sonst nach Namen, sonst "Door".</summary>
        static string TuerBlatt(Door d)
        {
            EFT.Ballistics.BallisticCollider best = null; float gr = -1f;
            foreach (var bc in d.GetComponentsInChildren<EFT.Ballistics.BallisticCollider>(true))
            {
                var col = bc.GetComponent<Collider>();
                if (col == null) continue;
                float s = col.bounds.size.sqrMagnitude;
                if (s > gr) { gr = s; best = bc; }
            }
            if (best != null)
            {
                var g = MaterialCache.Zuordnen(best.TypeOfMaterial);
                if (g != Gruppe.Unknown) return g.ToString();
            }
            string n = d.name.ToLowerInvariant();
            if (n.Contains("steel")) return nameof(Gruppe.MetalThick);
            if (n.Contains("metal")) return nameof(Gruppe.MetalThin);
            return nameof(Gruppe.Door);
        }

        static string Eintrag(string art, P2 a, P2 b, string zustand, string blatt)
        {
            var sb = new StringBuilder(96);
            sb.Append("{\"kind\":\"").Append(art).Append("\",\"a\":");
            KartenSchnitt.Punkt(sb, a.X, a.Z);
            sb.Append(",\"b\":");
            KartenSchnitt.Punkt(sb, b.X, b.Z);
            sb.Append(",\"state\":\"").Append(zustand).Append("\",\"leaf\":\"").Append(blatt).Append("\"}");
            return sb.ToString();
        }

        // ------------------------------------------------------------------ JSON

        static string Json(string karte, float sliceY, float hoehe, float boden, Vector3 pos, List<Seg> waende, List<string> oeff, string[] namen, Zahlen z)
        {
            double minX = pos.x, maxX = pos.x, minZ = pos.z, maxZ = pos.z;
            foreach (var s in waende)
            {
                minX = Math.Min(minX, Math.Min(s.AX, s.BX)); maxX = Math.Max(maxX, Math.Max(s.AX, s.BX));
                minZ = Math.Min(minZ, Math.Min(s.AZ, s.BZ)); maxZ = Math.Max(maxZ, Math.Max(s.AZ, s.BZ));
            }
            if (z.Scan != null)
            {
                var q = z.Scan;
                return KartenJson.Schreiben(Plugin.Version, karte, sliceY, hoehe, boden, pos.x, pos.y, pos.z, minX, minZ, maxX, maxZ, waende, oeff, namen,
                    new[]
                    {
                        ("walls", (double)z.Waende), ("outlinePieces", q.Kanten), ("centreLinePieces", q.Mitten), ("doors", z.Tueren), ("windows", z.Fenster),
                        ("gridW", z.ZellenW), ("gridH", z.ZellenH), ("resolution", ScanRaster.Value), ("solidCells", q.FesteZellen), ("thickCells", q.DickeZellen),
                        ("thinCells", q.DuenneZellen), ("lines", z.Linien), ("casts", z.Strahlen), ("hits", z.Treffer), ("pairs", q.Paare), ("singleSurfaces", q.Einzel),
                        ("colliders", z.Slots), ("movingSkipped", z.Dynamisch), ("playersSkipped", z.Spieler), ("doorParts", z.TuerTeile), ("windowParts", z.FensterTeile),
                        ("spursRemoved", q.AesteWeg), ("openingCellsCleared", q.OeffnungZellen), ("msCasting", Math.Round(z.MsStrahlen)), ("ms", Math.Round(z.Ms))
                    });
            }
            return KartenJson.Schreiben(Plugin.Version, karte, sliceY, hoehe, boden, pos.x, pos.y, pos.z, minX, minZ, maxX, maxZ, waende, oeff, namen,
                new[]
                {
                    ("walls", (double)z.Waende), ("rawPieces", z.RohSegmente), ("facePairsJoined", z.Paare), ("doors", z.Tueren), ("windows", z.Fenster),
                    ("colliders", z.Collider), ("box", z.Quader), ("mesh", z.Mesh), ("meshUnreadable", z.MeshNichtLesbar), ("meshMissing", z.MeshOhne),
                    ("sphere", z.Kugel), ("capsule", z.Kapsel), ("otherType", z.AndereArt), ("terrainSkipped", z.Terrain), ("movingSkipped", z.Dynamisch),
                    ("playersSkipped", z.Spieler), ("doorParts", z.TuerTeile), ("windowParts", z.FensterTeile), ("trianglesCut", z.Dreiecke),
                    ("queries", z.Kacheln), ("ms", Math.Round(z.Ms))
                });
        }
    }
}
