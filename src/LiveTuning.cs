// ---------------------------------------------------------------
//  Live-Einstellung: beobachtet BepInEx/config/kaan.materialklang.live.json und setzt die dort stehenden Werte in die
//  ConfigEntries (BepInEx speichert sie dann in die cfg). Diese Datei haengt nur an BepInEx.Configuration und System.IO -
//  sie laeuft auch im Offline-Test (Materialklang-KlangTest).
//
//  Dateiformat:  { "Sektion/Schluessel": Wert, ..., "reload": true }
//    - Namen genau wie in F12 / cfg (englisch). Schluessel mit "_" am Anfang sind Kommentare.
//    - Beim ersten Einlesen (Start) gilt alles; danach nur, was sich gegenueber dem letzten Lesen geaendert hat.
//    - "reload": true wendet alles erneut an; das Plugin schreibt die Datei dann ohne "reload" zurueck.
//    - Einstellungen, die nur beim Spiel-/Raidstart wirken (NurNeustart), werden spaeter ignoriert (Warnung);
//      beim allerersten Lesen vor dem Anwenden der Haken gelten sie noch.
// ---------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using BepInEx.Configuration;

namespace Materialklang
{
    /// <summary>0.8.3: Welche gemerkten Spielzustaende muss eine geaenderte Einstellung neu aufbauen lassen?</summary>
    [Flags]
    internal enum Auffrischung { Nichts = 0, Waende = 1, Portale = 2 }

    internal static class AuffrischRegel
    {
        /// <summary>
        /// Bis 0.8.2 liess JEDE Aenderung (z. B. Lautheit, Luft, Hall) alle Tuer-/Fensterwerte verwerfen und alle Portale neu setzen;
        /// das Spiel rechnet dann jede Verdeckung neu (ForceUpdate aller Quellen, sofortiges Setzen). Jetzt nur noch, wo es noetig ist:
        ///  - Portale (Tueren/Fenster-Zielwerte + UpdateInitialPortalsData): "4. Doors and Windows" und "1. General/Enabled"
        ///  - Waende zwischen Raeumen (Messungen veraltet): zusaetzlich "0. Physics Mode", "1. General/Overall strength",
        ///    die Materialgruppen "2. xx" und "5. Walls Between Rooms"
        /// </summary>
        public static Auffrischung Fuer(string sektion, string schluessel)
        {
            if (sektion == null) return Auffrischung.Nichts;
            if (sektion.StartsWith("4. ", StringComparison.Ordinal) || (sektion == "1. General" && schluessel == "Enabled"))
                return Auffrischung.Portale | Auffrischung.Waende;
            if (sektion.StartsWith("0. ", StringComparison.Ordinal) || sektion.StartsWith("2. ", StringComparison.Ordinal)
                || sektion.StartsWith("5. ", StringComparison.Ordinal) || (sektion == "1. General" && schluessel == "Overall strength"))
                return Auffrischung.Waende;
            return Auffrischung.Nichts;
        }
    }

    internal sealed class LiveTuning
    {
        /// <summary>Wirken nur beim Start (Haken werden einmal gesetzt bzw. Puffer beim Raidstart angelegt).</summary>
        public static readonly HashSet<string> NurNeustart = new HashSet<string>(StringComparer.Ordinal)
        {
            "3. Bot Hearing (SAIN)/Mode",
            "10. Advanced (rays)/Transmission rays x",
            "10. Advanced (rays)/Reflection rays x",
            "10. Advanced (rays)/Max. bounces",
            "10. Advanced (rays)/Energy kept per bounce",
        };

        public const string DateiName = "kaan.materialklang.live.json";

        public const string Vorlage =
            "{\n" +
            "  \"_example\": \"Put overrides here as \\\"Section/Key\\\": value, e.g. \\\"0. Physics Mode (real values)/Loudness factor\\\": 0.3 - applied within a second, no restart. \\\"reload\\\": true re-applies everything. Keys starting with _ are ignored.\"\n" +
            "}\n";

        readonly ConfigFile cfg;
        readonly string pfad;
        readonly Action<string> info, warn;
        readonly Dictionary<string, string> letzter = new Dictionary<string, string>(StringComparer.Ordinal);   // Schluessel -> "Art:Roh"
        readonly HashSet<string> gewarnt = new HashSet<string>(StringComparer.Ordinal);
        DateTime stempelZeit; long stempelLaenge = -1;
        bool erstes = true;

        /// <summary>Wird nach jedem Durchgang aufgerufen, in dem mindestens ein Wert geaendert wurde.</summary>
        public Action Angewendet = () => { };
        public int Gesamt;

        public LiveTuning(ConfigFile cfg, string pfad, Action<string> info, Action<string> warn)
        {
            this.cfg = cfg; this.pfad = pfad; this.info = info ?? (s => { }); this.warn = warn ?? (s => { });
        }

        public string Pfad => pfad;

        public void Anlegen()
        {
            try { if (!File.Exists(pfad)) File.WriteAllText(pfad, Vorlage); }
            catch (Exception e) { warn("[Live] cannot create " + pfad + ": " + e.Message); }
        }

        /// <summary>Alles aus der Datei anwenden (Spielstart: auch die Neustart-Werte).</summary>
        public int Start(bool beimSpielstart) => Lesen(true, beimSpielstart);

        /// <summary>Einmal je Sekunde aufrufen: liest nur, wenn sich die Datei geaendert hat.</summary>
        public int Tick()
        {
            FileInfo fi;
            try { fi = new FileInfo(pfad); } catch { return 0; }
            if (!fi.Exists) { Anlegen(); return 0; }
            if (fi.LastWriteTimeUtc == stempelZeit && fi.Length == stempelLaenge) return 0;
            return Lesen(false, false);
        }

        int Lesen(bool alle, bool beimSpielstart)
        {
            string text;
            FileInfo fi = new FileInfo(pfad);
            if (!fi.Exists) { Anlegen(); return 0; }
            try
            {
                using (var fs = new FileStream(pfad, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                using (var sr = new StreamReader(fs)) text = sr.ReadToEnd();
            }
            catch (Exception e) { warn("[Live] cannot read " + pfad + ": " + e.Message); return 0; }
            DateTime zeit = fi.LastWriteTimeUtc; long laenge = fi.Length;

            var liste = new List<JsonEintrag>();
            if (!MiniJson.Lesen(text, liste, out string fehler))
            {
                stempelZeit = zeit; stempelLaenge = laenge;      // nicht dieselbe Datei nochmal anmeckern
                warn("[Live] " + Path.GetFileName(pfad) + " is not valid (" + fehler + ") - ignored until it changes.");
                return 0;
            }
            stempelZeit = zeit; stempelLaenge = laenge;

            bool reload = false;
            foreach (var e in liste) if (e.Schluessel == "reload" && e.Art == JsonArt.Bool && e.Roh == "true") reload = true;
            if (reload) { alle = true; info("[Live] reload requested - re-applying all values"); }
            if (erstes) { alle = true; erstes = false; }

            var vorhandene = new List<KeyValuePair<string, string>>();
            foreach (var d in cfg.Keys) vorhandene.Add(new KeyValuePair<string, string>(d.Section, d.Key));

            int geaendert = 0;
            bool speichern = cfg.SaveOnConfigSet;
            cfg.SaveOnConfigSet = false;
            var neuerStand = new Dictionary<string, string>(StringComparer.Ordinal);
            try
            {
                foreach (var e in liste)
                {
                    if (e.Schluessel.StartsWith("_", StringComparison.Ordinal) || e.Schluessel == "reload") continue;
                    string stand = e.Art + ":" + e.Roh;
                    neuerStand[e.Schluessel] = stand;
                    if (!alle && letzter.TryGetValue(e.Schluessel, out var alt) && alt == stand) continue;
                    if (Anwenden(vorhandene, e, beimSpielstart)) geaendert++;
                }
            }
            finally
            {
                cfg.SaveOnConfigSet = speichern;
            }
            letzter.Clear();
            foreach (var kv in neuerStand) letzter[kv.Key] = kv.Value;
            if (geaendert > 0)
            {
                try { cfg.Save(); } catch (Exception e) { warn("[Live] saving the cfg failed: " + e.Message); }
                Gesamt += geaendert;
                Angewendet();
            }
            if (reload) ReloadEntfernen(liste, zeit, laenge);
            return geaendert;
        }

        bool Anwenden(List<KeyValuePair<string, string>> vorhandene, JsonEintrag e, bool beimSpielstart)
        {
            if (!LiveSchluessel.Aufloesen(vorhandene, e.Schluessel, out string sek, out string key))
            {
                WarnEinmal("unknown:" + e.Schluessel, "[Live] unknown setting '" + e.Schluessel + "' (use the exact F12 names: \"Section/Key\") - ignored.");
                return false;
            }
            string voll = sek + "/" + key;
            if (!beimSpielstart && NurNeustart.Contains(voll))
            {
                WarnEinmal("restart:" + voll + "=" + e.Roh, "[Live] '" + voll + "' only takes effect after a game/raid restart - ignored now (it is applied when the game starts).");
                return false;
            }
            var entry = cfg[new ConfigDefinition(sek, key)];
            if (!WertWandler.Wandeln(entry.SettingType, e, out string neu, out string fehler))
            {
                WarnEinmal("bad:" + voll + "=" + e.Roh, "[Live] '" + voll + "': " + fehler + " (got " + e.Art + " " + e.Roh + ") - ignored.");
                return false;
            }
            string vorher = entry.GetSerializedValue();
            try { entry.SetSerializedValue(neu); }
            catch (Exception ex)
            {
                WarnEinmal("bad:" + voll + "=" + e.Roh, "[Live] '" + voll + "': cannot set '" + neu + "' (" + ex.Message + ") - ignored.");
                return false;
            }
            string nachher = entry.GetSerializedValue();
            if (nachher == vorher) return false;
            string zusatz = "";
            if (!Gleich(nachher, neu)) zusatz = " (requested " + neu + ", limited to the allowed range)";
            info("[Live] " + voll + ": " + vorher + " -> " + nachher + zusatz + (beimSpielstart ? " (at start)" : ""));
            return true;
        }

        static bool Gleich(string a, string b)
        {
            if (a == b) return true;
            return double.TryParse(a, NumberStyles.Float, CultureInfo.InvariantCulture, out var x)
                && double.TryParse(b, NumberStyles.Float, CultureInfo.InvariantCulture, out var y) && Math.Abs(x - y) < 1e-9;
        }

        void WarnEinmal(string schluessel, string text)
        {
            if (gewarnt.Add(schluessel)) warn(text);
        }

        // "reload" aus der Datei nehmen, aber nur, wenn sie seit dem Lesen unveraendert ist (sonst gewinnt der andere Schreiber)
        void ReloadEntfernen(List<JsonEintrag> liste, DateTime zeit, long laenge)
        {
            try
            {
                var fi = new FileInfo(pfad);
                if (fi.LastWriteTimeUtc != zeit || fi.Length != laenge) return;
                var rest = new List<JsonEintrag>();
                foreach (var e in liste) if (e.Schluessel != "reload") rest.Add(e);
                string tmp = pfad + ".tmp";
                File.WriteAllText(tmp, rest.Count == 0 ? Vorlage : MiniJson.Schreiben(rest));
                if (File.Exists(pfad)) File.Delete(pfad);
                File.Move(tmp, pfad);
                var f2 = new FileInfo(pfad);
                stempelZeit = f2.LastWriteTimeUtc; stempelLaenge = f2.Length;
            }
            catch (Exception ex) { warn("[Live] could not clear 'reload': " + ex.Message); }
        }
    }
}
