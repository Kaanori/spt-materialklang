using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text;
using Audio.SpatialSystem;
using Audio.SpatialSystem.SpatialAudioCalculator;
using BepInEx;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Materialklang
{
    // ------------------------------------------------------------------------------------------
    //  Klang-Log (Abschnitt 11): je verarbeitetem Klang eine Zeile in BepInEx/Materialklang-Logs/<Datum>-<Karte>.log.
    //   - Standard AUS. Aus = ein Boolesches Feld (An) wird gelesen, sonst nichts.
    //   - An = der Hauptfaden fuellt einen wiederverwendeten LogEintrag und reiht ihn ein (kein Dateizugriff, keine Neuanlage);
    //     Formatieren und Schreiben macht der Faden "Materialklang-Log" (KlangSchreiber in KlangLogKern.cs).
    //   - Namen von Collidern/Quellen/Besitzern werden einmal je Objekt aufgeloest und gemerkt (danach keine Zeichenketten mehr).
    //   - Grenzen: Radius um den Hoerer, hoechstens N Zeilen je Quelle und Sekunde, Gesamtgrenze je Sekunde.
    // ------------------------------------------------------------------------------------------
    internal static class KlangLog
    {
        internal static ConfigEntry<bool> Aktiv, LiveAktiv;
        internal static ConfigEntry<float> Radius;
        internal static ConfigEntry<int> MaxProQuelle, MaxMB, Behalte;
        internal static ConfigEntry<KeyboardShortcut> MarkTaste;

        /// <summary>Zwischengespeicherter Zustand von "Sound log" - die einzige Pruefung auf den heissen Pfaden.</summary>
        internal static bool An;

        internal const string Sek = "11. Sound log & live tuning";

        static readonly KlangSchreiber schreiber = new KlangSchreiber();
        static readonly RateLimiter limiter = new RateLimiter();
        static bool offen;          // Datei fuer den laufenden Raid ist (zum Oeffnen) eingereiht
        static int markNr;
        static ConfigFile cfgRef;

        // ---- Zwischenspeicher fuer Namen (einmal je Objekt) ----
        static readonly Dictionary<int, string> colNamen = new Dictionary<int, string>(256);
        static readonly Dictionary<int, string> quellNamen = new Dictionary<int, string>(128);
        static readonly Dictionary<int, string> besitzerNamen = new Dictionary<int, string>(128);
        static readonly Dictionary<object, string> raumNamen = new Dictionary<object, string>(128);
        static readonly Dictionary<string, string> klangTypen = new Dictionary<string, string>(256);
        static readonly Dictionary<Type, string> typNamen = new Dictionary<Type, string>();
        static readonly string[] gruppenTypen = Enum.GetNames(typeof(BetterAudio.AudioSourceGroupType));
        static readonly Func<int, string> colNameF = ColName;

        // ---- von den Haken gesetzt ----
        internal static float LpEnde = float.NaN, HpEnde = float.NaN, LuftHz = float.NaN, FarbeH = float.NaN, FarbeL = float.NaN;
        internal static float WaldM = float.NaN, WaldDb = float.NaN, WaldHz = float.NaN;   // Wald (Laubdaempfung) des Geraeusches, das gerade verarbeitet wird

        struct SebWert { public double Zeit; public float W, Db, DirLp, LuftHz, Lp, WaldM, WaldHz; }
        static readonly Dictionary<int, SebWert> sebWerte = new Dictionary<int, SebWert>(64);

        static readonly ConditionalWeakTable<TransmissionCalculator, TransInfo> transTab =
            new ConditionalWeakTable<TransmissionCalculator, TransInfo>();

        internal static void Binden(ConfigFile c)
        {
            cfgRef = c;
            NamenEinrichten();
            Aktiv = c.Bind(Sek, "Sound log", false,
                "Writes one line per processed sound near you to BepInEx/Materialklang-Logs/<date>-<map>.log (path, obstacles with material/thickness/dB, volume, low-pass, reverb, SEB values). For tuning and bug reports. Off = no cost. Applies live.");
            Radius = c.Bind(Sek, "Log radius (m)", 60f,
                new ConfigDescription("Only sounds closer than this to you are logged.", new AcceptableValueRange<float>(5f, 300f)));
            MaxProQuelle = c.Bind(Sek, "Max. lines per source per second", 4,
                new ConfigDescription("Rate limit so footsteps and continuous sounds do not flood the file.", new AcceptableValueRange<int>(1, 60)));
            MaxMB = c.Bind(Sek, "Max. file size (MB)", 50,
                new ConfigDescription("When a raid's file reaches this size, further sound lines are dropped (MARK lines and [Live] lines are still written).", new AcceptableValueRange<int>(1, 500)));
            Behalte = c.Bind(Sek, "Keep last raids", 10,
                new ConfigDescription("Older log files are deleted when a new raid's file is created.", new AcceptableValueRange<int>(1, 100)));
            MarkTaste = c.Bind(Sek, "Mark odd sound key", KeyboardShortcut.Empty,
                "Press to write a '=== MARK n ===' line (with time and your position) into the sound log, so the odd moment is easy to find. Unbound by default. Works only while the sound log is on.");
            LiveAktiv = c.Bind(Sek, "Live tuning", false,
                "Watches BepInEx/config/kaan.materialklang.live.json (once per second) and applies the overrides in it to the settings without a restart. See README 'Sound log & live tuning'. Applies live.");

            An = Aktiv.Value;
            Aktiv.SettingChanged += (_, __) => { An = Aktiv.Value; if (!An) limiter.Leeren(); };
        }

        internal static void Anwenden(Harmony h)
        {
            try
            {
                var apply = AccessTools.DeclaredMethod(typeof(SpatialAudioOccluder), "ApplyOcclusionToSource", new[] { typeof(BetterSource), typeof(bool) });
                var compute = AccessTools.DeclaredMethod(typeof(Audio.SpatialSystem.Utils.FilterCutoffCalculator), "Compute");
                if (apply == null || compute == null)
                {
                    Plugin.Log.LogWarning("Sound log: targets not found (game update?) -> sound log unavailable.");
                    return;
                }
                h.Patch(apply, postfix: new HarmonyMethod(typeof(KlangLog), nameof(NachApply)) { priority = Priority.Last });
                h.Patch(compute, postfix: new HarmonyMethod(typeof(KlangLog), nameof(FilterEnde)) { priority = Priority.Last });
            }
            catch (Exception e) { Statistik.Fehler(e); }
        }

        // ------------------------------------------------------------------ Datei / Raid

        internal static void RaidStart()
        {
            Wetter.RaidStart();      // Wetter zuerst lesen: steht dann im Kopf der Log-Datei
            Wald.RaidStart();
            Schliessen();
            if (An) DateiOeffnen();
        }

        internal static void RaidEnde() { Schliessen(); Wetter.RaidEnde(); Wald.RaidEnde(); }

        internal static void SzeneGewechselt()
        {
            colNamen.Clear(); quellNamen.Clear(); besitzerNamen.Clear(); raumNamen.Clear(); sebWerte.Clear(); limiter.Leeren();
        }

        static void Schliessen()
        {
            if (offen) schreiber.Schliessen();
            offen = false;
        }

        internal static void Beenden() { schreiber.Beenden(); offen = false; }

        static void DateiOeffnen()
        {
            try
            {
                string karte = SceneManager.GetActiveScene().name;
                string dir = Path.Combine(BepInEx.Paths.BepInExRootPath, "Materialklang-Logs");
                string name = LogRotation.Dateiname(DateTime.Now, karte);
                schreiber.Oeffnen(dir, name, Behalte.Value, (long)MaxMB.Value * 1024 * 1024, Kopf(karte));
                offen = true;
                Plugin.Log.LogInfo("Sound log: " + Path.Combine(dir, name));
            }
            catch (Exception e) { Statistik.Fehler(e); }
        }

        static string Kopf(string karte)
        {
            var sb = new StringBuilder();
            sb.Append("=== Materialklang ").Append(Plugin.Version).Append(" sound log === map=").Append(karte)
              .Append(" date=").Append(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss")).Append('\n');
            sb.Append("columns: clock | game time t, frame f | kind(preset) clip src owner | d=distance | path + scores (T transmission, D diffraction, R reflection; 1 = fully blocked) | hits: group thickness dB xrays \"collider\" | vol, low-pass, high-pass | mode | rooms | reverb | SEB\n");
            sb.Append(Wetter.Kopfzeile()).Append('\n');
            if (cfgRef != null)
            {
                sb.Append("--- settings at the start of this file (section 2.xx only where changed from default) ---\n");
                foreach (var def in cfgRef.Keys)
                {
                    var e = cfgRef[def];
                    if (def.Section.StartsWith("2. ") && Equals(e.BoxedValue, e.DefaultValue)) continue;
                    sb.Append('[').Append(def.Section).Append("] ").Append(def.Key).Append(" = ").Append(e.GetSerializedValue()).Append('\n');
                }
                sb.Append("--- end of settings ---");
            }
            return sb.ToString();
        }

        /// <summary>Eine Textzeile (z.B. "[Live] ...") in das Log, sobald eine Datei offen ist.</summary>
        internal static void Text(string zeile)
        {
            if (!offen) return;
            var e = schreiber.Holen();
            e.Art = LogArt.Text; e.Uhr = DateTime.Now.Ticks; e.Spielzeit = Time.time; e.Bild = Time.frameCount; e.Text = zeile;
            schreiber.Einreihen(e);
        }

        /// <summary>Taste "Mark odd sound".</summary>
        internal static void Markieren()
        {
            if (!An) { Plugin.Log.LogInfo("Sound log: MARK ignored - the sound log is off (F12 > 11. Sound log & live tuning)."); Ton(EFT.UI.EUISoundType.ErrorMessage); return; }
            if (!offen) DateiOeffnen();
            var e = schreiber.Holen();
            e.Art = LogArt.Marke; e.MarkNr = ++markNr; e.Uhr = DateTime.Now.Ticks; e.Spielzeit = Time.time; e.Bild = Time.frameCount;
            var ba = MonoBehaviourSingleton<BetterAudio>.Instance;
            var lt = ba != null ? ba.ListenerTransform : null;
            if (lt != null) { var p = lt.position; e.PosX = p.x; e.PosY = p.y; e.PosZ = p.z; }
            schreiber.Einreihen(e);
            Plugin.Log.LogInfo($"Sound log: === MARK {markNr} ===");
            Ton(EFT.UI.EUISoundType.QuestSubTrackComplete);
        }

        /// <summary>Kurzer Bestaetigungston der Spieloberflaeche (Marke gesetzt / Protokoll aus).</summary>
        static void Ton(EFT.UI.EUISoundType art)
        {
            try { Comfort.Common.Singleton<EFT.UI.GUISounds>.Instance?.PlayUISound(art); }
            catch (Exception e) { Statistik.Fehler(e); }
        }

        /// <summary>Aus dem Plugin-Update (einmal je Sekunde): Meldungen des Schreibers ausgeben.</summary>
        internal static void MeldungenAbholen()
        {
            string m;
            while ((m = schreiber.MeldungHolen()) != null) Plugin.Log.LogWarning(m);
        }

        // ------------------------------------------------------------------ Haken

        internal static TransInfo TransHole(TransmissionCalculator c) => transTab.GetValue(c, _ => new TransInfo());

        internal static void SebMerken(BetterSource q, float w, float db, float dirLp, float luftHz, float lp, float waldM, float waldHz)
        {
            if (q == null) return;
            sebWerte[q.GetInstanceID()] = new SebWert { Zeit = KlangSchreiber.Jetzt(), W = w, Db = db, DirLp = dirLp, LuftHz = luftHz, Lp = lp, WaldM = waldM, WaldHz = waldHz };
        }

        // Postfix FilterCutoffCalculator.Compute (zuletzt): endgueltige Grenzfrequenzen merken
        static void FilterEnde(ref float lowPassFreq, ref float highPassFreq)
        {
            if (!An) return;
            LpEnde = lowPassFreq; HpEnde = highPassFreq;
            FarbeH = Kontext.HatFarbe ? Kontext.dH : float.NaN;
            FarbeL = Kontext.HatFarbe ? Kontext.dL : float.NaN;
        }

        // Postfix SpatialAudioOccluder.ApplyOcclusionToSource (zuletzt): die Zeile
        static void NachApply(SpatialAudioOccluder __instance, BetterSource source)
        {
            if (!An) { return; }
            try { Aufnehmen(__instance, source); }
            catch (Exception ex) { Statistik.Fehler(ex); }
            finally { LpEnde = HpEnde = LuftHz = FarbeH = FarbeL = WaldM = WaldDb = WaldHz = float.NaN; }
        }

        static void Aufnehmen(SpatialAudioOccluder occ, BetterSource source)
        {
            if (source == null) return;
            float entf = Farbe.Entfernung(source);
            if (entf > Radius.Value) return;
            limiter.ProQuelle = MaxProQuelle.Value;
            if (!limiter.Erlaubt(source.GetInstanceID(), KlangSchreiber.Jetzt())) return;
            if (!offen) DateiOeffnen();
            if (!offen) return;

            var e = schreiber.Holen();
            e.Art = LogArt.Klang;
            e.Uhr = DateTime.Now.Ticks; e.Spielzeit = Time.time; e.Bild = Time.frameCount;
            e.Entf = entf;

            // ---- was fuer ein Klang, von wem ----
            string klang = source._lastPlayingClipName;
            var preset = source.Preset;
            e.Gruppentyp = preset != null ? TypName(preset.Type) : "?";
            e.Klang = klang;
            e.Typ = KlangArt(klang, e.Gruppentyp);
            e.Quelle = QuellName(source);
            var cont = occ.SourceContainer;
            e.Besitzer = cont != null ? BesitzerName(cont._parentTransform) : null;
            e.QuellRaum = RaumName(cont?.CurrentAudioRoom);
            e.HoererRaum = RaumName(occ.AudioSystem?.ListenerCurrentRoom);

            // ---- Weg und Hindernisse ----
            e.Propagation = occ.IsPropagationActive;
            e.Wirkung = occ.OcclusionEffect;
            TransmissionCalculator tc = null;
            ParallelSoundPropagationCalculator pc = null;
            var comb = occ as CombinedSpatialAudioOccluder;
            if (comb != null)
            {
                pc = comb._propagationCalculator as ParallelSoundPropagationCalculator;
                var oc = comb._obstructionCalculator as SpatialAudioOcclusionCombinedCalculator;
                if (oc != null)
                {
                    var r = oc.LastAggregateResult;
                    if (oc._ranTransmission) e.WertT = r.transmissionScore;
                    if (oc._ranDiffraction) e.WertD = r.diffractionScore;
                    if (oc._ranReflection) e.WertR = r.reflectionScore;
                    tc = oc._transmissionCalculator;
                }
            }
            else if (occ is MultiSourceSpatialAudioOccluder)
            {
                tc = Kontext.MultiCalc;
            }
            e.Strahlen = e.FreieStrahlen = e.StueckAnzahl = 0;
            if (!e.Propagation && tc != null && transTab.TryGetValue(tc, out var ti))
            {
                ti.Fuellen(e, colNameF);
                e.WertT = float.IsNaN(e.WertT) && comb == null ? e.Wirkung : e.WertT;
            }
            if (pc != null)
            {
                e.WertRoute = pc._propagationScore;
                if (e.Propagation) WandZwischenRaeumen.LogDaten(pc, e, colNameF);
            }
            e.Weg = ZeilenFormat.WegWahl(e.Propagation, e.Wirkung, e.WertT, e.WertD, e.WertR, e.WertRoute, e.WertWand);

            // ---- Ergebnis ----
            e.Lautstaerke = occ.CurrentParameters.OcclusionVolumeFactor;
            e.Tiefpass = LpEnde; e.Hochpass = HpEnde;
            e.Physik = Physik.An;
            e.RohDb = Physik.Dynamik.Value * e.Wirkung;
            e.Lautheit = Physik.Lautheit.Value;
            e.FarbeH = FarbeH; e.FarbeL = FarbeL; e.LuftHz = LuftHz;
            e.WaldM = WaldM; e.WaldDb = WaldDb; e.WaldHz = WaldHz;

            // ---- SEB (nur Schuesse, nur wenn frisch) ----
            if (sebWerte.TryGetValue(source.GetInstanceID(), out var sw) && KlangSchreiber.Jetzt() - sw.Zeit < 1.5)
            {
                e.HatSeb = true; e.SebW = sw.W; e.SebDb = sw.Db; e.SebDirLp = sw.DirLp; e.SebLuftHz = sw.LuftHz; e.SebLp = sw.Lp; e.SebWaldM = sw.WaldM; e.SebWaldHz = sw.WaldHz;
            }

            // ---- Hall (Zone, in der Quelle und Hoerer stehen) ----
            if (HallZonen.ZonenAnzahl > 0)
            {
                if (HallZonen.Finde(source.Position, out var rn, out var rrt, out var rdb))
                { e.HallQuelle = rn; e.HallQuelleRt = rrt; e.HallQuelleDb = rdb; }
                var ba = MonoBehaviourSingleton<BetterAudio>.Instance;
                var lt = ba != null ? ba.ListenerTransform : null;
                if (lt != null && HallZonen.Finde(lt.position, out var hn, out var hrt, out var hdb))
                { e.HallHoerer = hn; e.HallHoererRt = hrt; e.HallHoererDb = hdb; }
            }
            schreiber.Einreihen(e);
        }

        // ------------------------------------------------------------------ Namen (einmal je Objekt)

        static string TypName(BetterAudio.AudioSourceGroupType t)
        {
            int i = (int)t;
            return i >= 0 && i < gruppenTypen.Length ? gruppenTypen[i] : "?";
        }

        // Grobe Einordnung nach Klangname; Standard = Gruppentyp des Spiels. Ergebnis wird je Klangname gemerkt.
        static string KlangArt(string klang, string gruppe)
        {
            if (string.IsNullOrEmpty(klang)) return gruppe;
            if (klangTypen.TryGetValue(klang, out var t)) return t;
            string k = klang.ToLowerInvariant();
            if (k.Contains("step") || k.Contains("foot") || k.Contains("sprint") || k.Contains("walk")) t = "Footstep";
            else if (k.Contains("door") || k.Contains("hatch") || k.Contains("gate")) t = "Door";
            else if (k.Contains("shot") || k.Contains("fire") || k.Contains("silenc") || k.Contains("suppress")) t = "Gunshot";
            else if (k.Contains("vo_") || k.Contains("voice") || k.Contains("speech") || k.Contains("breath") || k.Contains("cough") || k.Contains("scream")) t = "Voice";
            else if (k.Contains("reload") || k.Contains("mag") || k.Contains("bolt") || k.Contains("charge") || k.Contains("rack") || k.Contains("click")) t = "WeaponHandling";
            else if (k.Contains("impact") || k.Contains("ricoch") || k.Contains("hit") || k.Contains("bullet")) t = "Impact";
            else if (k.Contains("grenade") || k.Contains("explo")) t = "Explosion";
            else t = gruppe;
            klangTypen[klang] = t;
            return t;
        }

        static string QuellName(BetterSource s)
        {
            int id = s.GetInstanceID();
            if (quellNamen.TryGetValue(id, out var n)) return n;
            var ty = s.GetType();
            if (!typNamen.TryGetValue(ty, out var tn)) typNamen[ty] = tn = ty.Name;
            n = tn + "/" + s.name;
            quellNamen[id] = n;
            return n;
        }

        // 0.8.3: ISpatialAudioRoom.Name ist in den Karten leer (wird nur im Editor gesetzt) -> Name des GameObjects, einmal je Raum.
        static string RaumName(ISpatialAudioRoom r)
        {
            if (r == null) return null;
            if (raumNamen.TryGetValue(r, out var n)) return n;
            n = r.Name;
            if (string.IsNullOrEmpty(n) && r is Component c && c != null) n = c.gameObject.name;
            if (string.IsNullOrEmpty(n)) n = r.IsOutdoor ? "outdoor" : "?";
            raumNamen[r] = n;
            return n;
        }

        static string BesitzerName(Transform t)
        {
            if (t == null) return "-";
            int id = t.GetInstanceID();
            if (besitzerNamen.TryGetValue(id, out var n)) return n;
            n = t.name;
            var wurzel = t.root;
            if (wurzel != null && wurzel != t) n += " @" + wurzel.name;
            besitzerNamen[id] = n;
            return n;
        }

        static string ColName(int id)
        {
            if (colNamen.TryGetValue(id, out var n)) return n;
            n = "?";
            try
            {
                var c = Resources.InstanceIDToObject(id) as Collider;
                if (c != null)
                {
                    n = c.name;
                    var p = c.transform.parent;
                    if (p != null) n = p.name + "/" + n;
                    if (n.Length > 48) n = n.Substring(n.Length - 48);
                }
            }
            catch { }
            colNamen[id] = n;
            return n;
        }

        /// <summary>Gruppenname fuer die Zeile (einmal einrichten).</summary>
        internal static void NamenEinrichten()
        {
            ZeilenFormat.GruppenName = i => i >= 0 && i < Plugin.AnzahlGruppen ? ((Gruppe)i).ToString() : "?";
        }
    }
}
