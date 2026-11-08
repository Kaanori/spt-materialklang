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

        internal static string DateiPfad => Path.Combine(BepInEx.Paths.ConfigPath, LiveTuning.DateiName);

        internal static void Awake(ConfigFile c)
        {
            cfg = c;

            // Jede Aenderung eines Wertes (F12 oder Datei) frischt die abgeleiteten Zustaende auf.
            c.SettingChanged += (_, a) =>
            {
                var e = a.ChangedSetting;
                if (e.Definition.Section == KlangLog.Sek || e.Definition.Key == "Diagnostic log" || e.Definition.Key == "Diagnostics") return;
                Anfordern();
                // Schwelle fuer "freier Weg" gilt live fuer die schon angelegten Rechner
                if (ReferenceEquals(e, Plugin.FreiwegSchwelle) || ReferenceEquals(e, Physik.Freiweg)
                    || ReferenceEquals(e, Physik.Aktiv) || ReferenceEquals(e, Plugin.Aktiv)) FreiwegPatch.AlleNeu();
            };

            if (KlangLog.LiveAktiv.Value) Starten(true);
        }

        static void Starten(bool beimSpielstart)
        {
            live = new LiveTuning(cfg, DateiPfad, Info, Warn);
            live.Angewendet = Anfordern;
            live.Anlegen();
            int n = live.Start(beimSpielstart);
            Plugin.Log.LogInfo($"Live tuning: watching {live.Pfad} ({n} values applied).");
        }

        static void Info(string s) { Plugin.Log.LogInfo(s); KlangLog.Text(s); }
        static void Warn(string s) { Plugin.Log.LogWarning(s); KlangLog.Text(s); }

        static void Anfordern() { auffrischenAb = Time.realtimeSinceStartup + 0.3f; }

        // Jeden Frame aus Plugin.Update
        internal static void Tick()
        {
            float t = Time.realtimeSinceStartup;
            try
            {
                if (KlangLog.MarkTaste != null && KlangLog.MarkTaste.Value.IsDown()) KlangLog.Markieren();
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

        // Spielzustaende, die einen Wert zwischenspeichern, neu aufbauen lassen:
        //  - Tueren/Fenster: Zielwerte verwerfen und alle Portale neu setzen (das loest im Spiel auch eine Neuberechnung
        //    aller Verdeckungen aus - dasselbe, was jede Tuer beim Oeffnen tut)
        //  - Waende zwischen Raeumen: Messungen gelten als veraltet
        static void Auffrischen()
        {
            TuerenFenster.Leeren();
            WandZwischenRaeumen.Neu();
            if (SpatialAudioSystem.Initialized && DataProvider.TryGetData<SpatialAudioDataContainer>(out var d) && d != null)
                d.UpdateInitialPortalsData();
        }
    }
}
