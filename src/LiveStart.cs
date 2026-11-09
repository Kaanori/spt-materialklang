using System;
using System.IO;
using Audio.SpatialSystem;
using BepInEx;
using BepInEx.Configuration;
using EFT.DataProviding;
using UnityEngine;

namespace Materialklang
{
    // Verbindet Klang-Log und Live-Einstellung mit dem Plugin: Beobachter fuer die Datei, "Mark"-Taste,
    // und das Auffrischen der Spielzustaende nach jeder Aenderung (ob aus F12 oder aus der Datei).
    internal static class LiveStart
    {
        static ConfigFile cfg;
        static LiveTuning live;
        static float naechsterLauf, auffrischenAb = -1f;
        static Auffrischung offen;

        internal static string DateiPfad => Path.Combine(BepInEx.Paths.ConfigPath, LiveTuning.DateiName);

        internal static void Awake(ConfigFile c)
        {
            cfg = c;

            // Jede Aenderung eines Wertes (F12 oder Datei) frischt die abgeleiteten Zustaende auf.
            c.SettingChanged += (_, a) =>
            {
                var e = a.ChangedSetting;
                if (e.Definition.Section == KlangLog.Sek || e.Definition.Key == "Diagnostic log" || e.Definition.Key == "Diagnostics") return;
                Anfordern(AuffrischRegel.Fuer(e.Definition.Section, e.Definition.Key));
                // Schwelle fuer "freier Weg" gilt live fuer die schon angelegten Rechner
                if (ReferenceEquals(e, Plugin.FreiwegSchwelle) || ReferenceEquals(e, Physik.Freiweg)
                    || ReferenceEquals(e, Physik.Aktiv) || ReferenceEquals(e, Plugin.Aktiv)) FreiwegPatch.AlleNeu();
            };

            if (KlangLog.LiveAktiv.Value) Starten(true);
        }

        static void Starten(bool beimSpielstart)
        {
            live = new LiveTuning(cfg, DateiPfad, Info, Warn);
            // jede gesetzte Einstellung meldet sich ueber SettingChanged selbst (Anfordern dort, je nach Schluessel)
            live.Anlegen();
            int n = live.Start(beimSpielstart);
            Plugin.Log.LogInfo($"Live tuning: watching {live.Pfad} ({n} values applied).");
        }

        static void Info(string s) { Plugin.Log.LogInfo(s); KlangLog.Text(s); }
        static void Warn(string s) { Plugin.Log.LogWarning(s); KlangLog.Text(s); }

        static void Anfordern(Auffrischung was)
        {
            if (was == Auffrischung.Nichts) return;
            offen |= was;
            auffrischenAb = Time.realtimeSinceStartup + 0.3f;
        }

        /// <summary>Ist gerade ein Texteingabefeld aktiv (Chat, Suche, Benennen)? Dann zaehlt die Marker-Taste nicht -
        /// sonst setzt z. B. "?" (Umschalt+ß = KeyCode.Minus auf deutscher Tastatur) beim Tippen ungewollt Marken.
        /// Typpruefung ueber den Namen, damit kein Verweis auf TextMeshPro/UnityEngine.UI noetig ist.</summary>
        static Type esTyp;
        static bool TipptGerade()
        {
            try
            {
                if (esTyp == null) esTyp = Type.GetType("UnityEngine.EventSystems.EventSystem, UnityEngine.UI");
                if (esTyp == null) return false;
                var es = esTyp.GetProperty("current")?.GetValue(null);
                var go = es != null ? esTyp.GetProperty("currentSelectedGameObject")?.GetValue(es) as GameObject : null;
                if (go == null) return false;
                foreach (var c in go.GetComponents<Component>())
                {
                    if (c == null) continue;
                    var n = c.GetType().Name;
                    if (n.IndexOf("InputField", StringComparison.Ordinal) >= 0) return true;
                }
            }
            catch { }
            return false;
        }

        // Jeden Frame aus Plugin.Update
        internal static void Tick()
        {
            float t = Time.realtimeSinceStartup;
            try
            {
                if (KlangLog.MarkTaste != null && KlangLog.MarkTaste.Value.IsDown() && !TipptGerade()) KlangLog.Markieren();
                if (auffrischenAb >= 0f && t >= auffrischenAb) { auffrischenAb = -1f; Auffrischen(); }
                if (t < naechsterLauf) return;
                naechsterLauf = t + 1f;
                KlangLog.MeldungenAbholen();
                if (KlangLog.LiveAktiv.Value)
                {
                    if (live == null) Starten(false);
                    else live.Tick();
                }
                else live = null;      // wieder einschalten liest alles neu
            }
            catch (Exception e) { Statistik.Fehler(e); }
        }

        // Spielzustaende, die einen Wert zwischenspeichern, neu aufbauen lassen (0.8.3: nur, was die Einstellung betrifft, AuffrischRegel):
        //  - Tueren/Fenster: Zielwerte verwerfen und alle Portale neu setzen (das loest im Spiel auch eine Neuberechnung
        //    aller Verdeckungen aus - dasselbe, was jede Tuer beim Oeffnen tut); Materialzuordnung "Tuer" haengt am Schalter
        //  - Waende zwischen Raeumen: Messungen gelten als veraltet
        static void Auffrischen()
        {
            var was = offen; offen = Auffrischung.Nichts;
            if ((was & Auffrischung.Waende) != 0) WandZwischenRaeumen.Neu();
            if ((was & Auffrischung.Portale) == 0) return;
            TuerenFenster.Leeren();
            MaterialCache.Leeren();
            if (SpatialAudioSystem.Initialized && DataProvider.TryGetData<SpatialAudioDataContainer>(out var d) && d != null)
                d.UpdateInitialPortalsData();
        }
    }
}
