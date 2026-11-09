using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using Audio.AmbientSubsystem;
using Audio.AuxiliaryAudioUtils;
using Audio.SpatialSystem;
using Audio.Vehicles;
using Audio.Vehicles.BTR;
using BepInEx.Configuration;
using Comfort.Common;
using EFT;
using EFT.Interactive;
using HarmonyLib;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Materialklang
{
    // ---------------------------------------------------------------------------------------------
    //  17. All fixed sources (0.8.5): eine Regel fuer alle ortsfesten 3D-Klangquellen der Karten (QuellRegelKern).
    //
    //  Beleg: Inventur aller Kartenszenen (level*-Dateien, 08.11.2026), Tabelle in klangquellen-inventur.md:
    //   - Ambient (BaseAmbientSoundPlayer, Abschnitt 15): 161 kleine Quellen + 14 Maschinen (Factory: Pumpen, Kessel) umstellbar.
    //   - Occluded (SourceOccluder, Abschnitt 16): 14 Signale + 41 Maschinen (Labs-Turbinen/Generatoren, Interchange-/Shoreline-
    //     Generatoren, Interchange-Feuer) + 16 kleine (Labs-Server/Trafos/Pumpen, Interchange-Spielautomaten).
    //   - Plain (rohe Szenen-AudioSource ohne Skript, NEU hier): 29 kleine (Signalfackeln an Ausgaengen, Factory-Tropfen,
    //     Lighthouse-Waerterhaus, Lagerfeuer Ground Zero) + 18 mittlere (brennende Autos Streets/Ground Zero/Shoreline,
    //     Generator Streets, SUV-Leerlauf). Diese haben im Spiel keinen Occluder (wie Abschnitt 15); seit 0.8.5 daempft sie
    //     WandDaempfung.cs ("Wall damping for non-occluded sources"), sobald ihre Reichweite verlaengert ist.
    //  Umsetzung hier: Einstellungen, Weg "Plain" (einmal je geladener Szene alle AudioSources durchsehen), Zusammenfassung je Karte
    //  ins BepInEx-Log, eine Klang-Log-Zeile je angepasster Quellart und der Okklusions-Takt ferner Quellen (Postfix
    //  SoundOcclusionProcessor.UpdatePriority): das Spiel rechnet je Bild genau EINEN Okklusions-Prozessor; mehr hoerbare Quellen
    //  verlaengern die Runde fuer alle (auch Schritte/Schuesse). Quellen jenseits ihrer alten Reichweite kommen deshalb nur jede
    //  9. Runde dran (Einstellung).
    // ---------------------------------------------------------------------------------------------
    internal static class FesteQuellen
    {
        internal const string Sek = "17. All fixed sources";
        internal static ConfigEntry<bool> Aktiv;
        internal static ConfigEntry<float> KleinGrenze, MittelFaktor, MittelGrenze, TrimDb;
        internal static ConfigEntry<string> MittelNamen, AusNamen, FahrzeugNamen;
        internal static ConfigEntry<int> FernTakt;
        internal static ConfigEntry<bool> FahrzeugAktiv;
        internal static ConfigEntry<float> FahrzeugFaktor, FahrzeugGrenze, BtrGrenze;

        internal static string[] Mittel = new string[0], Aus = new string[0], Fahrzeug = new string[0];

        internal static bool Wirksam => Aktiv != null && Aktiv.Value && Physik.An;

        internal static void Binden(ConfigFile c)
        {
            Aktiv = c.Bind(Sek, "Enabled", true,
                "Every fixed 3D sound source of the map follows one rule: up to a reference radius exactly the game's curve, beyond it -6 dB per doubling " +
                "of distance (point source) instead of a hard end, range = game range x factor, capped by how loud the source is (small hum/drips 30 m, " +
                "machines/generators/large fires 80 m, alarms 300 m - section 16). Extends sections 15 (ambient players) and 16 (occluded sources) " +
                "to all their sources and adds raw scene sources without any script (fires, extract flares, Streets generator ...). " +
                "Raw scene sources and ambient players have no occlusion in the game; 'Wall damping for non-occluded sources' below muffles them through walls. Works only while Physics Mode is on. Applies live.");
            KleinGrenze = c.Bind(Sek, "Small sources: max. range (m)", (float)QuellRegelKern.KleinGrenze,
                new ConfigDescription("Largest new range of small sources (hum, drips, flies, neon signs, server racks). Their factor is '15. Ambient emitters / Range factor'.",
                    new AcceptableValueRange<float>(10f, 60f)));
            MittelFaktor = c.Bind(Sek, "Medium sources: range factor", (float)QuellRegelKern.MittelFaktor,
                new ConfigDescription("New range of machines/generators/turbines/large fires = game range x this factor.", new AcceptableValueRange<float>(1f, 10f)));
            MittelGrenze = c.Bind(Sek, "Medium sources: max. range (m)", (float)QuellRegelKern.MittelGrenze,
                new ConfigDescription("Largest new range of medium sources.", new AcceptableValueRange<float>(20f, 200f)));
            MittelNamen = c.Bind(Sek, "Medium sources: name keywords", QuellRegelKern.MittelListe,
                "Comma-separated keywords. A source counts as medium (machine) when one of them starts a word in the name of the source, its parent or " +
                "grandparent or its clip; everything else (not loud, not excluded) is small.");
            AusNamen = c.Bind(Sek, "Exclude: name keywords", QuellRegelKern.AusListe,
                "Comma-separated keywords of sources that stay exactly as in the game: wide area sounds (sea, beach, river, cicadas, wind ...) and music. " +
                "Add the name of any source you want to keep unchanged (see the sound log lines '[Fixed]').");
            TrimDb = c.Bind(Sek, "Volume trim (dB)", 0f,
                new ConfigDescription("Level of the small/medium occluded and raw scene sources overall (ambient players: section 15, alarms: section 16).",
                    new AcceptableValueRange<float>(-20f, 10f)));
            FernTakt = c.Bind(Sek, "Distant sources: occlusion refresh (rounds)", QuellRegelKern.FernTakt,
                new ConfigDescription("The game updates the occlusion of one sound per frame in turn. Extended sources farther away than their game range " +
                    "are updated only every (n+1)-th turn so nearby sounds (steps, shots) stay responsive. 0 = like the game.",
                    new AcceptableValueRange<int>(0, 30)));

            FahrzeugAktiv = c.Bind(Sek, "Vehicles: extend range", true,
                "Running engines (V-Ex taxi: idle, engine start and driving off; BTR engine/tracks outdoors; idling vans on Streets) follow the same rule " +
                "with their own cap: the game stops the V-Ex taxi after 20 m (linear fade), a real car idles at ~75 dB(A) in 1 m and is heard 100-200 m " +
                "outdoors. They have no occlusion in the game ('Wall damping for non-occluded sources' muffles them through walls). Sound log: one '[Vehicle]' line per vehicle in range. Applies live " +
                "(BTR: from its next engine start).");
            FahrzeugFaktor = c.Bind(Sek, "Vehicles: range factor", (float)QuellRegelKern.FahrzeugFaktor,
                new ConfigDescription("New range of vehicle engines = game range x this factor (V-Ex taxi 20 m x 10 = 200 m).", new AcceptableValueRange<float>(1f, 20f)));
            FahrzeugGrenze = c.Bind(Sek, "Vehicles: max. range (m)", (float)QuellRegelKern.FahrzeugGrenze,
                new ConfigDescription("Largest new range of vehicle engines (V-Ex taxi, parked vans; the BTR has its own cap below).", new AcceptableValueRange<float>(20f, 400f)));
            BtrGrenze = c.Bind(Sek, "Vehicles: BTR engine max. range (m)", (float)QuellRegelKern.BtrGrenze,
                new ConfigDescription("Largest new range of the BTR engine/tracks outdoors (game 200 m; a BTR-80 diesel is ~107 dB at 1 m and heard 500-1000 m). " +
                    "Uses 'Vehicles: range factor' (200 m x 10, capped here). Turret and air horn: section 18. Applies live.", new AcceptableValueRange<float>(200f, 1500f)));
            FahrzeugNamen = c.Bind(Sek, "Vehicles: name keywords", QuellRegelKern.FahrzeugListe,
                "Comma-separated keywords (word start in the name of a raw scene source, its parent/grandparent or clip) of idling vehicles placed in the map. " +
                "The V-Ex taxi and the BTR are found by their scripts, not by name.");

            Mittel = LautSignalKern.ListeLesen(MittelNamen.Value);
            Fahrzeug = LautSignalKern.ListeLesen(FahrzeugNamen.Value);
            Aus = LautSignalKern.ListeLesen(AusNamen.Value);
            EventHandler neu = (_, __) => AllesNeu();
            FahrzeugAktiv.SettingChanged += neu; FahrzeugFaktor.SettingChanged += neu; FahrzeugGrenze.SettingChanged += neu; BtrGrenze.SettingChanged += neu;
            FahrzeugNamen.SettingChanged += (_, __) => { Fahrzeug = LautSignalKern.ListeLesen(FahrzeugNamen.Value); AllesNeu(); };
            Aktiv.SettingChanged += neu; KleinGrenze.SettingChanged += neu; MittelFaktor.SettingChanged += neu; MittelGrenze.SettingChanged += neu; TrimDb.SettingChanged += neu;
            MittelNamen.SettingChanged += (_, __) => { Mittel = LautSignalKern.ListeLesen(MittelNamen.Value); AllesNeu(); };
            AusNamen.SettingChanged += (_, __) => { Aus = LautSignalKern.ListeLesen(AusNamen.Value); AllesNeu(); };
            if (Physik.Aktiv != null) Physik.Aktiv.SettingChanged += neu;
            if (Plugin.Aktiv != null) Plugin.Aktiv.SettingChanged += neu;
        }

        /// <summary>Faktor und Grenze je Klasse (Laut: Abschnitt 16, Klein-Faktor: Abschnitt 15).</summary>
        internal static void Werte(QuellKlasse k, out double faktor, out double grenze)
        {
            switch (k)
            {
                case QuellKlasse.Laut:
                    faktor = LautSignal.Faktor != null ? LautSignal.Faktor.Value : QuellRegelKern.LautFaktor;
                    grenze = LautSignal.Reichweite != null ? LautSignal.Reichweite.Value : LautSignalKern.Standard;
                    break;
                case QuellKlasse.Fahrzeug:
                    faktor = FahrzeugFaktor != null ? FahrzeugFaktor.Value : QuellRegelKern.FahrzeugFaktor;
                    grenze = FahrzeugGrenze != null ? FahrzeugGrenze.Value : QuellRegelKern.FahrzeugGrenze;
                    break;
                case QuellKlasse.Mittel:
                    faktor = MittelFaktor != null ? MittelFaktor.Value : QuellRegelKern.MittelFaktor;
                    grenze = MittelGrenze != null ? MittelGrenze.Value : QuellRegelKern.MittelGrenze;
                    break;
                default:
                    faktor = Umgebung.Faktor != null ? Umgebung.Faktor.Value : 4.0;
                    grenze = KleinGrenze != null ? KleinGrenze.Value : QuellRegelKern.KleinGrenze;
                    break;
            }
        }

        internal static void Anwenden(Harmony h)
        {
            try
            {
                SceneManager.sceneLoaded += SzeneGeladen;
                SceneManager.activeSceneChanged += (_, __) => SzeneGewechselt();
                var prio = AccessTools.DeclaredMethod(typeof(SoundOcclusionProcessor), "UpdatePriority");
                if (prio == null) Plugin.Log.LogWarning("Fixed sources: SoundOcclusionProcessor.UpdatePriority not found (game update?) -> no slower refresh of distant sources.");
                else h.Patch(prio, postfix: new HarmonyMethod(typeof(FesteQuellen), nameof(NachPrio)));
                // Fahrzeuge: BTR-Motor (die Quellen stellt VehicleMovementSoundContext bei jedem Motorstart neu ein) und V-Ex-Anlassen (nur Log)
                var btr = AccessTools.DeclaredMethod(typeof(VehicleMovementSoundContext), "SetUpAudioSource");
                if (btr == null) Plugin.Log.LogWarning("Fixed sources: VehicleMovementSoundContext.SetUpAudioSource not found (game update?) -> BTR range unchanged.");
                else h.Patch(btr, postfix: new HarmonyMethod(typeof(FesteQuellen), nameof(NachBtrQuelle)));
                var vex = AccessTools.DeclaredMethod(typeof(CarExtraction), "Play");
                if (vex != null) h.Patch(vex, postfix: new HarmonyMethod(typeof(FesteQuellen), nameof(NachVexStart)));
            }
            catch (Exception e) { Statistik.Fehler(e); Plugin.Log.LogError($"Fixed sources: applying failed: {e.Message}"); }
        }

        // =========================================================================================
        //  Meldungen aller drei Wege (Ambient, Occluded, Plain): Zusammenfassung je Karte + eine Klang-Log-Zeile je Art
        // =========================================================================================
        internal sealed class Meldung
        {
            public UnityEngine.Object Quelle;
            public string Weg, Art, Klang, Grund;
            public bool An;
            public QuellKlasse Klasse;
            public float MaxAlt, MaxNeu, R0;
            public Func<double, double> AltF;   // x = d / MaxAlt
            public AnimationCurve Neu;          // x = d / MaxNeu
        }

        static readonly Dictionary<int, Meldung> meldungen = new Dictionary<int, Meldung>(256);
        static readonly HashSet<string> geschrieben = new HashSet<string>();
        static int stand, gemeldetStand = -1;
        static float ruheAb;
        static bool artenOffen;

        /// <summary>Stand einer Quelle melden (umgestellt oder mit Grund, warum nicht).</summary>
        internal static void Melden(int id, Meldung m)
        {
            meldungen[id] = m;
            stand++; ruheAb = Time.realtimeSinceStartup + 3f;
            if (m.An) artenOffen = true;
        }

        internal static void Vergessen(int id) { if (meldungen.Remove(id)) { stand++; ruheAb = Time.realtimeSinceStartup + 3f; } }

        static void SzeneGewechselt()
        {
            var weg = new List<int>();
            foreach (var kv in meldungen) if (kv.Value.Quelle == null) weg.Add(kv.Key);
            foreach (var k in weg) meldungen.Remove(k);
            weg.Clear();
            foreach (var kv in rohe) if (kv.Value.S == null) weg.Add(kv.Key);
            foreach (var k in weg) rohe.Remove(k);
            weg.Clear();
            foreach (var kv in fern) if (kv.Value.W == null) weg.Add(kv.Key);
            foreach (var k in weg) fern.Remove(k);
            weg.Clear();
            foreach (var kv in fahrzeuge) if (kv.Value.S == null) weg.Add(kv.Key);
            foreach (var k in weg) fahrzeuge.Remove(k);
            weg.Clear();
            foreach (var kv in btrQuellen) if (kv.Value.S == null) weg.Add(kv.Key);
            foreach (var k in weg) btrQuellen.Remove(k);
            geschrieben.Clear(); artenOffen = true; gemeldetStand = -1; stand++;
        }

        // ---- aus Plugin.Update ----
        internal static void Tick()
        {
            FahrzeugTick();
            if (meldungen.Count == 0) return;
            float jetzt = Time.realtimeSinceStartup;
            if (jetzt < ruheAb) return;
            try
            {
                if (stand != gemeldetStand) { gemeldetStand = stand; Zusammenfassung(); }
                if (artenOffen && KlangLog.An) { artenOffen = false; ArtenSchreiben(); }
            }
            catch (Exception ex) { Statistik.Fehler(ex); }
        }

        static string Karte()
        {
            try
            {
                if (Singleton<GameWorld>.Instantiated && !string.IsNullOrEmpty(Singleton<GameWorld>.Instance.LocationId)) return Singleton<GameWorld>.Instance.LocationId;
            }
            catch { }
            return SceneManager.GetActiveScene().name;
        }

        static void Zusammenfassung()
        {
            int[,] n = new int[4, 4];   // [Weg, Klasse]
            int an = 0, aus = 0;
            var gruende = new Dictionary<string, int>();
            foreach (var m in meldungen.Values)
            {
                if (m.Quelle == null) continue;
                if (m.An) { an++; n[WegIndex(m.Weg), (int)m.Klasse]++; }
                else { aus++; gruende.TryGetValue(m.Grund ?? "?", out int g); gruende[m.Grund ?? "?"] = g + 1; }
            }
            var sb = new StringBuilder(256);
            sb.Append("Fixed sources [").Append(Karte()).Append("]: ").Append(an).Append(" of ").Append(an + aus).Append(" adjusted - small ")
              .Append(Summe(n, -1, 0)).Append(", medium ").Append(Summe(n, -1, 1)).Append(", loud ").Append(Summe(n, -1, 2)).Append(", vehicle ").Append(Summe(n, -1, 3))
              .Append(" (ambient ").Append(Summe(n, 0, -1)).Append(", occluded ").Append(Summe(n, 1, -1))
              .Append(", raw scene ").Append(Summe(n, 2, -1)).Append(", vehicle ").Append(Summe(n, 3, -1)).Append(")");
            if (gruende.Count > 0)
            {
                var l = new List<KeyValuePair<string, int>>(gruende);
                l.Sort((a, b) => b.Value.CompareTo(a.Value));
                sb.Append("; unchanged: ");
                for (int i = 0; i < l.Count && i < 6; i++) sb.Append(i > 0 ? ", " : "").Append(l[i].Key).Append(' ').Append(l[i].Value);
            }
            if (!Physik.An) sb.Append(" (Physics Mode off)");
            Plugin.Log.LogInfo(sb.ToString());
        }

        static int WegIndex(string w) => w == "ambient" ? 0 : w == "occluded" ? 1 : w == "vehicle" ? 3 : 2;

        static int Summe(int[,] n, int weg, int klasse)
        {
            int s = 0;
            for (int w = 0; w < 4; w++) for (int k = 0; k < 4; k++) if ((weg < 0 || w == weg) && (klasse < 0 || k == klasse)) s += n[w, k];
            return s;
        }

        /// <summary>Eine Klang-Log-Zeile je angepasster Quellart (Weg, Art, Klasse, alte/neue Reichweite), je Szene einmal.</summary>
        static void ArtenSchreiben()
        {
            var arten = new Dictionary<string, KeyValuePair<Meldung, int>>();
            var inv = CultureInfo.InvariantCulture;
            foreach (var m in meldungen.Values)
            {
                if (!m.An || m.Quelle == null || m.Neu == null) continue;
                string k = m.Weg + "|" + m.Art + "|" + m.Klasse + "|" + m.MaxAlt.ToString("0.#", inv) + "|" + m.MaxNeu.ToString("0.#", inv);
                arten.TryGetValue(k, out var v);
                arten[k] = new KeyValuePair<Meldung, int>(v.Key ?? m, v.Value + 1);
            }
            foreach (var kv in arten)
            {
                if (!geschrieben.Add(kv.Key)) continue;
                var m = kv.Value.Key;
                var sb = new StringBuilder(200);
                sb.Append("[Fixed] ").Append(m.Art).Append(" x").Append(kv.Value.Value).Append(" (").Append(m.Weg).Append(", ").Append(QuellRegelKern.Name(m.Klasse))
                  .Append(", clip ").Append(m.Klang ?? "-").Append(") | range ").Append(m.MaxAlt.ToString("0.#", inv)).Append(" -> ").Append(m.MaxNeu.ToString("0.#", inv))
                  .Append(" m, r0 ").Append(m.R0.ToString("F1", inv)).Append(" m | game/new:");
                foreach (double f in new[] { 0.5, 1.0, 2.0, 4.0 })
                {
                    double d = m.MaxAlt * f;
                    if (d >= m.MaxNeu) break;
                    double alt = d >= m.MaxAlt ? 0 : m.AltF(d / m.MaxAlt);
                    double neu = m.Neu.Evaluate((float)(d / m.MaxNeu));
                    sb.Append(' ').Append(d.ToString("0.#", inv)).Append(" m ").Append(alt <= 1e-6 ? "silent" : Db(alt, inv)).Append('/').Append(Db(neu, inv));
                }
                if (m.Weg != "occluded") sb.Append(WandText());
                KlangLog.Text(sb.ToString());
            }
        }

        static string Db(double g, IFormatProvider inv) => FernschussKern.DbAusGain(g).ToString("F1", inv);

        /// <summary>Log-Zusatz fuer Quellen ohne Okklusion im Spiel (0.8.5: eigene Wanddaempfung, Zeilen '[Walls]').</summary>
        internal static string WandText() => WandDaempfung.Wirksam ? " | no game occlusion - wall damping (see [Walls])" : " | no occlusion (walls do not muffle)";

        // =========================================================================================
        //  Weg "Plain": rohe Szenen-AudioSources ohne steuerndes Skript
        // =========================================================================================
        sealed class Roh
        {
            public AudioSource S;
            public AnimationCurve Alt, Neu;
            public AudioRolloffMode ModusAlt;
            public float MaxAlt, MinAlt, MaxNeu, R0;
            public string Name, Art, Klang, Grund;
            public string[] Namen;
            public bool Umgestellt, Gesperrt, Vex, Fackel;
            public QuellKlasse Klasse;
        }

        static readonly Dictionary<int, Roh> rohe = new Dictionary<int, Roh>(128);
        static readonly List<MonoBehaviour> mbs = new List<MonoBehaviour>(16);
        static readonly List<AudioSource> quellenPuffer = new List<AudioSource>(256);
        static bool gestoert;

        static void SzeneGeladen(Scene szene, LoadSceneMode modus)
        {
            if (gestoert) return;
            try
            {
                var wurzeln = szene.GetRootGameObjects();
                for (int w = 0; w < wurzeln.Length; w++)
                {
                    quellenPuffer.Clear();
                    wurzeln[w].GetComponentsInChildren(true, quellenPuffer);
                    for (int i = 0; i < quellenPuffer.Count; i++) Aufnehmen(quellenPuffer[i]);
                }
                quellenPuffer.Clear();
            }
            catch (Exception ex) { Fehler(ex); }
        }

        static void Aufnehmen(AudioSource s)
        {
            if (s == null) return;
            int id = s.GetInstanceID();
            if (rohe.ContainsKey(id) || LautSignal.Kennt(id)) return;   // schon im Okklusionsweg (Abschnitt 16)
            var t = s.transform;
            var nahe = new List<string>(4);
            var kette = new List<string>(8);
            for (var p = t; p != null; p = p.parent)
            {
                mbs.Clear();
                p.GetComponents(mbs);
                bool nah = p == t || p == t.parent;
                for (int i = 0; i < mbs.Count; i++)
                {
                    var mb = mbs[i];
                    if (mb == null) continue;
                    string n = mb.GetType().Name;
                    kette.Add(n);
                    if (nah) nahe.Add(n);
                }
            }
            mbs.Clear();
            bool vex = QuellRegelKern.FahrzeugKette(kette) && IstVexMotor(s);
            // Ambient-Spieler/SourceOccluder: eigene Wege (Abschnitt 15/16) - hier gar nicht erst melden
            foreach (var n in kette)
                if (n == "SourceOccluder" || n.EndsWith("AmbientSoundPlayer", StringComparison.Ordinal) || n == "EventLoopPlayer" || n == "EventRandomPlayer") return;
            var namen = new List<string>(4) { s.gameObject.name };
            if (t.parent != null) { namen.Add(t.parent.name); if (t.parent.parent != null) namen.Add(t.parent.parent.name); }
            string klang = s.clip != null ? s.clip.name : null;
            if (klang != null) namen.Add(klang);
            var e = new Roh
            {
                S = s, Name = s.gameObject.name, Art = QuellRegelKern.Art(s.gameObject.name), Klang = klang, Namen = namen.ToArray(),
                ModusAlt = s.rolloffMode, Alt = s.GetCustomCurve(AudioSourceCurveType.CustomRolloff), MaxAlt = s.maxDistance, MinAlt = s.minDistance
            };
            e.Vex = vex;
            e.Fackel = !vex && QuellRegelKern.FackelKette(kette, namen);   // Signalfackel am Ausgang: Reichweite aus Abschnitt 18
            if (!vex && !QuellRegelKern.RoheQuelle(nahe, kette, out string grund)) { e.Gesperrt = true; e.Grund = grund.StartsWith("script ") ? "script-controlled" : grund; }
            rohe[id] = e;
            Stellen(e);
        }

        /// <summary>Motorquelle des V-Ex-Taxis? WheelDrive (auf der Quelle oder einem Vorfahren) fuehrt genau diese AudioSource
        /// (Leerlauf-Schleife + PlayOneShot(OnStartSound) in CarExtraction.Play). Andere Quellen unter CarExtraction bleiben gesperrt.</summary>
        static bool IstVexMotor(AudioSource s)
        {
            for (var p = s.transform; p != null; p = p.parent)
            {
                var wd = p.GetComponent<WheelDrive>();
                if (wd != null) return wd.AudioSource == s;
            }
            return false;
        }

        /// <summary>Abschnitt 16 uebernimmt eine Quelle (SourceOccluder mit explizitem Quellen-Array): Original zurueck, hier vergessen.</summary>
        internal static void Abgeben(AudioSource s)
        {
            if (s == null) return;
            int id = s.GetInstanceID();
            if (!rohe.TryGetValue(id, out var e)) return;
            try { if (e.Umgestellt) Zurueck(e); } catch (Exception ex) { Fehler(ex); }
            WandDaempfung.Melden(s, false, null, null);
            rohe.Remove(id);
            Vergessen(id);
        }

        internal static Func<double, double> AltFunktion(AudioRolloffMode modus, AnimationCurve kurve, float min, float max)
        {
            switch (modus)
            {
                case AudioRolloffMode.Linear: return x => LautSignalKern.GainLinear(x * max, min, max);
                case AudioRolloffMode.Logarithmic: return x => LautSignalKern.GainLog(x * max, min, max);
                default: return x => x >= 1.0 ? 0.0 : kurve.Evaluate((float)x);
            }
        }

        /// <summary>Wurzel des V-Ex-Taxis (CarExtraction bzw. WheelDrive) fuer die Wanddaempfung: seine Collider zaehlen nicht als Wand.</summary>
        static Transform VexWurzel(AudioSource s)
        {
            var ce = s.GetComponentInParent<CarExtraction>();
            if (ce != null) return ce.transform;
            var wd = s.GetComponentInParent<WheelDrive>();
            return wd != null ? wd.transform : null;
        }

        static void Zurueck(Roh e)
        {
            if (e.ModusAlt == AudioRolloffMode.Custom) e.S.SetCustomCurve(AudioSourceCurveType.CustomRolloff, e.Alt);
            else e.S.rolloffMode = e.ModusAlt;
            e.S.maxDistance = e.MaxAlt;
            e.Umgestellt = false;
        }

        static void Stellen(Roh e)
        {
            bool fackel = e.Fackel && SkriptQuellen.Wirksam(SkriptArt.Ausgangsfackel);
            var m = new Meldung { Quelle = e.S, Weg = e.Vex ? "vehicle" : "raw scene", Art = e.Vex ? "V-Ex taxi " + e.Art : fackel ? "exit flare " + e.Art : e.Art, Klang = e.Klang, MaxAlt = e.MaxAlt };
            string grund = e.Grund;
            QuellKlasse k = QuellKlasse.Klein;
            var altF = AltFunktion(e.ModusAlt, e.Alt, e.MinAlt, e.MaxAlt);
            if (grund == null)
            {
                if (e.S.spatialBlend < 0.5f) grund = "2D";
                else if (e.Vex) { string w = LautSignalKern.Treffer(e.Namen, Aus); if (w != null) grund = "excluded '" + w + "'"; else k = QuellKlasse.Fahrzeug; }
                else if (!QuellRegelKern.Einordnen(e.Namen, LautSignal.Liste, Mittel, Aus, out k, out string wort, Fahrzeug)) grund = "excluded '" + wort + "'";
                if (grund != null) { }
                else if (k == QuellKlasse.Fahrzeug && !FahrzeugAktiv.Value) grund = "vehicles off";
                else if (k == QuellKlasse.Laut && !LautSignal.Wirksam) grund = "section 16 off";
                else if (e.ModusAlt == AudioRolloffMode.Custom && (e.Alt == null || e.Alt.length < 2)) grund = "no curve";
                else if (!QuellRegelKern.Kandidat(e.S.spatialBlend, e.MaxAlt, (float)altF(0.0))) grund = "silent/invalid";
            }
            double maxNeu = 0;
            if (grund == null)
            {
                Werte(k, out double f, out double g);
                if (fackel && k == QuellKlasse.Klein)
                    maxNeu = SkriptQuellenKern.Reichweite(SkriptArt.Ausgangsfackel, e.MaxAlt, SkriptQuellen.Grenze(SkriptArt.Ausgangsfackel));
                else maxNeu = QuellRegelKern.Reichweite(e.MaxAlt, f, g);
                if (!QuellRegelKern.Lohnt(e.MaxAlt, maxNeu)) grund = "already long";
            }
            // Ausgangsfackel: schaltet Abschnitt 18 (Flares), sonst wie bisher die Regel "klein" von Abschnitt 17
            if (grund == null && !(fackel ? Physik.An : Wirksam)) grund = "section 17 off";
            if (grund != null)
            {
                if (e.Umgestellt) Zurueck(e);
                m.Grund = grund;
                Melden(e.S.GetInstanceID(), m);
                WandDaempfung.Melden(e.S, false, m.Weg, m.Art);
                if (e.Vex || k == QuellKlasse.Fahrzeug) FahrzeugMerken(e.S, m, e.Vex ? "V-Ex taxi" : "parked vehicle");
                return;
            }
            var kk = UmgebungKern.Bauen(altF, e.MaxAlt, maxNeu, (k == QuellKlasse.Laut ? LautSignal.TrimDb.Value : TrimDb.Value), out double r0);
            var keys = new Keyframe[kk.Length];
            for (int i = 0; i < kk.Length; i++) keys[i] = new Keyframe(kk[i].T, kk[i].V, kk[i].In, kk[i].Out);
            e.Neu = new AnimationCurve(keys);
            e.MaxNeu = (float)maxNeu; e.R0 = (float)r0; e.Klasse = k;
            e.S.rolloffMode = AudioRolloffMode.Custom;
            e.S.SetCustomCurve(AudioSourceCurveType.CustomRolloff, e.Neu);
            e.S.maxDistance = e.MaxNeu;
            e.Umgestellt = true;
            m.An = true; m.Klasse = k; m.MaxNeu = e.MaxNeu; m.R0 = e.R0; m.AltF = altF; m.Neu = e.Neu;
            Melden(e.S.GetInstanceID(), m);
            // 0.8.5: keine Okklusion im Spiel -> eigene Wanddaempfung (V-Ex: Collider des Taxis zaehlen nicht)
            WandDaempfung.Melden(e.S, true, m.Weg, m.Art, eigen: e.Vex ? VexWurzel(e.S) : null);
            if (k == QuellKlasse.Fahrzeug) FahrzeugMerken(e.S, m, e.Vex ? "V-Ex taxi" : "parked vehicle");
        }


        // =========================================================================================
        //  Fahrzeuge (0.8.5): V-Ex-Taxi, BTR, abgestellte Transporter - Reichweite nach QuellRegelKern (Klasse Fahrzeug) + [Vehicle]-Zeilen
        //
        //  Beleg (EFT 4.1.6, Assembly-CSharp; Szenen-Inventur 08.11.2026):
        //   - V-Ex-Taxi = EFT.Interactive.CarExtraction (ExfiltrationSubscriber) mit WheelDrive. Eine einzige AudioSource
        //     "Toyota_Land_Cruiser_close" (WheelDrive.AudioSource): Clip suv_idle, Schleife, playOnAwake, vol 0.536, LINEAR 2 -> 20 m,
        //     3D; WheelDrive.SetupAudioSource setzt nur die Mixergruppe (BetterAudio.EnvTechnicalSoundsGroup "Environment").
        //     CarExtraction.Play spielt OnStartSound per PlayOneShot auf DERSELBEN Quelle (also gleiche Kurve), dann faehrt das Auto
        //     den Spline ab (hoechstens 45 s) und Dispose loest die Quelle und schaltet sie ab. Keine Hupe. Kein SourceOccluder:
        //     Waende/Raeume/Luft/Wald des Okklusionssystems wirken auf das Taxi nicht (im Spiel wie hier).
        //     Customs: "Dorms V-Ex/Enabled/CarExtraction/Toyota_Land_Cruiser_close" bei (182.0, -1.6, 214.8). Messung Kaan 09.11.:
        //     MARK 11 neben dem Taxi (181.7, -0.7, 208.9) = 5.9 m, MARK 12 "nicht mehr hoerbar" (179.8, 0.2, 194.0) = 20.9 m von der
        //     Quelle = genau das lineare Ende bei 20 m (bei 15 m schon -11 dB).
        //   - BTR (Streets/Woods) = Audio.Vehicles.BTR.BtrSoundController mit zwei VehicleMovementSoundContext (innen/aussen). Jeder
        //     Motorstart ruft SetUpAudioSource fuer 6 Quellen (Motor tief/hoch, Fahrgeraeusch langsam/schnell, Beschleunigen, Bremsen):
        //     min/max/Kurve aus dem Prefab (_minRolloffDistance/_maxRolloffDistance/_rolloffCurve). Nur der Aussen-Kontext wird
        //     verlaengert (innen hoert man ihn nur im BTR). Hupe (_airHornSource) = Pool-BetterSource -> Abschnitt 18.
        //   - Abgestellte Transporter Streets ("Gazel_Next.../sound_suv", Clip suv_idle, linear 2 -> 20 m): rohe Szenenquellen,
        //     Klasse Fahrzeug ueber "Vehicles: name keywords".
        //   - Lok Reserve/Lighthouse (Locomotive): Abschnitt 18 (Scripted sources), ebenso BTR-Turm und -Lufthorn.
        //   - BTR-Motor: Faktor wie alle Fahrzeuge, eigene Grenze "Vehicles: BTR engine max. range (m)" (600 m; Spiel 200 m).
        //  Okklusion fuer das Taxi (SourceOccluder per AddComponent) waere nicht sicher: die Quelle faehrt weg, Dispose schaltet ihr
        //  GameObject ab (SourceOccluder.LateUpdate pausiert seinen Container dann nie) und Abschnitt 16 wuerde sie zusaetzlich greifen.
        //  Stattdessen (0.8.5): WandDaempfung.cs misst die Waende selbst (nur solange die Quelle aktiv ist und spielt; Collider des Taxis
        //  zaehlen nicht) und daempft ueber einen eigenen Filter im Signalweg - nichts wird beim Spiel an- oder abgemeldet.
        // =========================================================================================
        sealed class Fz
        {
            public AudioSource S;
            public Meldung M;
            public string Rolle;
            public double LetztD = -1;
            public float LetztT;
        }
        static readonly Dictionary<int, Fz> fahrzeuge = new Dictionary<int, Fz>(16);
        static float fzNaechste;

        static void FahrzeugMerken(AudioSource s, Meldung m, string rolle)
        {
            if (s == null || m == null) return;
            int id = s.GetInstanceID();
            if (fahrzeuge.TryGetValue(id, out var f)) { f.M = m; f.Rolle = rolle; f.LetztD = -1; }
            else fahrzeuge[id] = new Fz { S = s, M = m, Rolle = rolle };
        }

        internal static Vector3? Hoerer()
        {
            var ba = MonoBehaviourSingleton<BetterAudio>.Instantiated ? MonoBehaviourSingleton<BetterAudio>.Instance : null;
            var lt = ba != null ? ba.ListenerTransform : null;
            return lt != null ? lt.position : (Vector3?)null;
        }

        static void FahrzeugTick()
        {
            if (fahrzeuge.Count == 0 || !KlangLog.An) return;
            float jetzt = Time.realtimeSinceStartup;
            if (jetzt < fzNaechste) return;
            fzNaechste = jetzt + 0.5f;
            try
            {
                var h = Hoerer();
                if (h == null) return;
                foreach (var f in fahrzeuge.Values)
                {
                    var s = f.S;
                    if (s == null || f.M == null || !s.isActiveAndEnabled || !s.isPlaying) continue;
                    double d = (s.transform.position - h.Value).magnitude;
                    double reich = Math.Max(f.M.MaxAlt, f.M.An ? f.M.MaxNeu : 0f);
                    if (!QuellRegelKern.FahrzeugZeile(d, f.LetztD, jetzt - f.LetztT, reich)) continue;
                    f.LetztD = d; f.LetztT = jetzt;
                    KlangLog.Text(FahrzeugText(f, d, null));
                }
            }
            catch (Exception ex) { Statistik.Fehler(ex); }
        }

        /// <summary>[Vehicle] Name (Rolle, Clip, Mixer) d | range alt -> neu | Pegel Spiel/neu (Kurve) + Lautstaerke der Quelle.</summary>
        static string FahrzeugText(Fz f, double d, string ereignis)
        {
            var inv = CultureInfo.InvariantCulture;
            var m = f.M; var s = f.S;
            var sb = new StringBuilder(220);
            sb.Append("[Vehicle] ").Append(s.gameObject.name).Append(" (").Append(f.Rolle).Append(", clip ").Append(s.clip != null ? s.clip.name : "-")
              .Append(", mixer ").Append(s.outputAudioMixerGroup != null ? s.outputAudioMixerGroup.name : "-").Append(')');
            if (ereignis != null) sb.Append(' ').Append(ereignis);
            sb.Append(" | d=").Append(d.ToString("F1", inv)).Append(" m | range ").Append(m.MaxAlt.ToString("0.#", inv));
            if (m.An) sb.Append(" -> ").Append(m.MaxNeu.ToString("0.#", inv)).Append(" m (r0 ").Append(m.R0.ToString("F1", inv)).Append(" m)");
            else sb.Append(" m unchanged (").Append(m.Grund ?? "?").Append(')');
            double alt = d >= m.MaxAlt || m.AltF == null ? 0 : m.AltF(d / m.MaxAlt);
            sb.Append(" | level game ").Append(alt <= 1e-6 ? "silent" : Db(alt, inv) + " dB");
            if (m.An && m.Neu != null)
            {
                double neu = d >= m.MaxNeu ? 0 : m.Neu.Evaluate((float)(d / m.MaxNeu));
                sb.Append(", new ").Append(neu <= 1e-6 ? "silent" : Db(neu, inv) + " dB");
            }
            sb.Append(" (curve) x vol ").Append(s.volume.ToString("0.###", inv)).Append(WandText());
            return sb.ToString();
        }

        // ---- V-Ex: Anlassen + Wegfahren (nur Log; der Klang laeuft ueber dieselbe Quelle und Kurve) ----
        static void NachVexStart(CarExtraction __instance, bool force)
        {
            if (force || !KlangLog.An) return;
            try
            {
                var s = __instance != null && __instance.Driver != null ? __instance.Driver.AudioSource : null;
                if (s == null || !fahrzeuge.TryGetValue(s.GetInstanceID(), out var f)) return;
                var h = Hoerer();
                double d = h != null ? (s.transform.position - h.Value).magnitude : -1;
                string start = __instance.Driver.OnStartSound != null ? __instance.Driver.OnStartSound.name : "-";
                KlangLog.Text(FahrzeugText(f, d, "ENGINE START '" + start + "' (one-shot on the idle source), drives off (max. 45 s)"));
                f.LetztD = d; f.LetztT = Time.realtimeSinceStartup;
            }
            catch (Exception ex) { Statistik.Fehler(ex); }
        }

        // ---- BTR: Aussen-Kontext nach jedem SetUpAudioSource verlaengern ----
        sealed class BtrQ
        {
            public AudioSource S;
            public AnimationCurve AltKurve, Neu;
            public float MaxAlt, MinAlt, MaxNeu, R0, Faktor, Grenze, Trim;
            public bool Aktiv;
            public Meldung M;
            public VehicleMovementSoundContext Ctx;
        }
        static readonly Dictionary<int, BtrQ> btrQuellen = new Dictionary<int, BtrQ>(16);
        static bool btrGestoert;

        static void NachBtrQuelle(VehicleMovementSoundContext __instance, ref AudioSource source)
        {
            if (btrGestoert || source == null || __instance == null) return;
            BtrStellen(__instance, source);
        }

        static void BtrStellen(VehicleMovementSoundContext __instance, AudioSource source)
        {
            try
            {
                var bsc = __instance.GetComponentInParent<BtrSoundController>();
                if (bsc == null || !ReferenceEquals(bsc._outdoorSoundContext, __instance)) return;
                int id = source.GetInstanceID();
                var kurve = __instance._rolloffCurve;
                float maxAlt = __instance._maxRolloffDistance, minAlt = __instance._minRolloffDistance;
                Werte(QuellKlasse.Fahrzeug, out double fak, out double gr);
                gr = BtrGrenze != null ? BtrGrenze.Value : QuellRegelKern.BtrGrenze;   // BTR: eigene Grenze (600 m), Faktor wie alle Fahrzeuge
                bool aktiv = Wirksam && FahrzeugAktiv.Value;
                float trim = TrimDb.Value;
                if (!btrQuellen.TryGetValue(id, out var q) || !ReferenceEquals(q.AltKurve, kurve) || q.MaxAlt != maxAlt || q.MinAlt != minAlt
                    || q.Faktor != (float)fak || q.Grenze != (float)gr || q.Aktiv != aktiv || q.Trim != trim)
                {
                    q = new BtrQ { Ctx = __instance, S = source, AltKurve = kurve, MaxAlt = maxAlt, MinAlt = minAlt, Faktor = (float)fak, Grenze = (float)gr, Aktiv = aktiv, Trim = trim };
                    var m = new Meldung { Quelle = source, Weg = "vehicle", Art = "BTR " + source.gameObject.name, Klang = source.clip != null ? source.clip.name : null, MaxAlt = maxAlt, Klasse = QuellKlasse.Fahrzeug };
                    var altF = AltFunktion(AudioRolloffMode.Custom, kurve, minAlt, maxAlt);
                    m.AltF = altF;
                    double maxNeu = QuellRegelKern.Reichweite(maxAlt, fak, gr);
                    if (kurve == null || kurve.length < 2) m.Grund = "no curve";
                    else if (!QuellRegelKern.Kandidat(source.spatialBlend, maxAlt, (float)altF(0.0))) m.Grund = "silent/invalid";
                    else if (!QuellRegelKern.Lohnt(maxAlt, maxNeu)) m.Grund = "already long";
                    else if (!aktiv) m.Grund = !FahrzeugAktiv.Value ? "vehicles off" : "section 17 off";
                    else
                    {
                        var kk = UmgebungKern.Bauen(altF, maxAlt, maxNeu, trim, out double r0);
                        var keys = new Keyframe[kk.Length];
                        for (int i = 0; i < kk.Length; i++) keys[i] = new Keyframe(kk[i].T, kk[i].V, kk[i].In, kk[i].Out);
                        q.Neu = new AnimationCurve(keys); q.MaxNeu = (float)maxNeu; q.R0 = (float)r0;
                        m.An = true; m.MaxNeu = q.MaxNeu; m.R0 = q.R0; m.Neu = q.Neu;
                    }
                    q.M = m;
                    btrQuellen[id] = q;
                    Melden(id, m);
                    FahrzeugMerken(source, m, "BTR outdoor");
                }
                source.rolloffMode = AudioRolloffMode.Custom;
                // 0.8.5: Wanddaempfung (keine Okklusion im Spiel); der BTR selbst zaehlt nicht als Wand
                WandDaempfung.Melden(source, q.Neu != null, "vehicle", "BTR " + source.gameObject.name, eigen: bsc.transform.root);
                if (q.Neu == null)
                {
                    // wie im Spiel (SetUpAudioSource hat das gerade selbst gesetzt; bei einer Einstellungsaenderung zurueck)
                    if (kurve != null && kurve.length >= 2) source.SetCustomCurve(AudioSourceCurveType.CustomRolloff, kurve);
                    source.maxDistance = maxAlt;
                    return;
                }
                source.maxDistance = q.MaxNeu;
                source.SetCustomCurve(AudioSourceCurveType.CustomRolloff, q.Neu);
            }
            catch (Exception ex)
            {
                Statistik.Fehler(ex);
                if (ex is MissingFieldException || ex is MissingMethodException || ex is TypeLoadException)
                {
                    btrGestoert = true;
                    Plugin.Log.LogError($"Fixed sources: {ex.GetType().Name}: {ex.Message} - BTR range off for this session.");
                }
            }
        }

        /// <summary>Einstellung geaendert: laufende BTR-Quellen sofort neu stellen (sonst erst beim naechsten Motorstart).</summary>
        static void BtrNeu()
        {
            if (btrGestoert) return;
            var l = new List<BtrQ>(btrQuellen.Values);
            foreach (var q in l) if (q.S != null && q.Ctx != null) BtrStellen(q.Ctx, q.S);
        }

        internal static void AllesNeu()
        {
            var weg = new List<int>();
            foreach (var kv in rohe)
            {
                if (kv.Value.S == null) { weg.Add(kv.Key); continue; }
                try { Stellen(kv.Value); } catch (Exception ex) { Fehler(ex); }
            }
            foreach (var k in weg) { rohe.Remove(k); meldungen.Remove(k); }
            try { BtrNeu(); } catch (Exception ex) { Fehler(ex); }
            Umgebung.Neu();
            LautSignal.Neu();
        }

        static void Fehler(Exception ex)
        {
            Statistik.Fehler(ex);
            if (ex is MissingFieldException || ex is MissingMethodException || ex is TypeLoadException)
            {
                gestoert = true;
                Plugin.Log.LogError($"Fixed sources: {ex.GetType().Name}: {ex.Message} - game changed? Raw scene sources off for this session.");
            }
        }

        // =========================================================================================
        //  Okklusions-Takt ferner Quellen
        // =========================================================================================
        sealed class Fern { public AudioSourceContainer C; public BetterSource W; public Vector3 Pos; public float AltQ; }
        static readonly Dictionary<int, Fern> fern = new Dictionary<int, Fern>(64);
        static bool taktGestoert;

        /// <summary>Abschnitt 16/17: Container einer verlaengerten Quelle merken (alte Reichweite fuer den Takt).</summary>
        internal static void TaktMerken(int containerId, BetterSource w, Vector3 pos, float maxAlt)
        {
            if (containerId < 0 || w == null || !MonoBehaviourSingleton<SpatialAudioSystem>.Instantiated) return;
            if (!MonoBehaviourSingleton<SpatialAudioSystem>.Instance._allRegisteredSoundContainers.TryGetValue(containerId, out var c) || c == null) return;
            float q = maxAlt * maxAlt + 25f;   // wie das Spiel: sqrt(max^2 + 25)
            fern[containerId] = new Fern { C = c, W = w, Pos = pos, AltQ = q };
        }

        internal static void TaktVergessen(int containerId) { if (containerId >= 0) fern.Remove(containerId); }

        static void NachPrio(SoundOcclusionProcessor __instance)
        {
            if (fern.Count == 0 || taktGestoert) return;
            try
            {
                var c = __instance._audioSourceContainer;
                if (c == null || !fern.TryGetValue(c.ID, out var f)) return;
                if (!ReferenceEquals(f.C, c) || f.W == null || !c._sources.Contains(f.W)) return;   // Container aus dem Pool neu vergeben
                var p = __instance.Priority;
                if (p == EAudioSourcePriority.OutOfRange) return;
                var lt = __instance._listenerTransform;
                if (lt == null) return;
                int basis = QuellRegelKern.Grundtakt((int)p);
                int takt = Wirksam ? QuellRegelKern.Takt(basis, (lt.position - f.Pos).sqrMagnitude, f.AltQ, FernTakt.Value) : basis;
                if (__instance.FramesToSkip != takt) __instance.FramesToSkip = takt;
            }
            catch (Exception ex)
            {
                Statistik.Fehler(ex);
                if (ex is MissingFieldException || ex is MissingMethodException || ex is TypeLoadException)
                {
                    taktGestoert = true;
                    Plugin.Log.LogError($"Fixed sources: {ex.GetType().Name}: {ex.Message} - distant refresh off for this session.");
                }
            }
        }
    }
}
