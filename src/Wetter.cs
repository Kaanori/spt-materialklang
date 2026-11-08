using System;
using System.Globalization;
using BepInEx.Configuration;
using Comfort.Common;
using EFT;
using EFT.Weather;
using HarmonyLib;
using UnityEngine;

namespace Materialklang
{
    // Raid-Wetter fuer die Luftdaempfung (ISO 9613-1, siehe Luftabsorption.cs).
    //  Quellen im Spiel:
    //   - Temperatur (Grad C), Regen (0..1), Nebel (Streudichte 0.004 klar .. 0.075 dicht), Bewoelkung (-1..1):
    //     WeatherController.Instance.WeatherCurve (IWeatherCurve), zur aktuellen Spielzeit ausgewertet.
    //   - Luftdruck: steht NUR in den WeatherNode-Knoten, die der Server schickt (AtmospherePressure, SPT: mmHg, ~760),
    //     nicht in der Kurve. Darum werden SetWeatherNodes / SetWeatherForce mit einem Postfix mitgelesen und der Druck
    //     zur Spielzeit zwischen den Knoten interpoliert.
    //   - Luftfeuchte gibt es im Spiel nicht -> Schaetzung aus Basiswert + Wolken + Regen + Nebel (Iso9613.FeuchteSchaetzen).
    //  Die Tabelle "Grenzfrequenz je Entfernung" wird nur bei Aenderungen neu gerechnet (Wetter alle 30 s geprueft,
    //  Einstellungen sofort), pro Geraeusch kostet es zwei Tabellenwerte.
    internal static class Wetter
    {
        internal static ConfigEntry<bool> Aktiv, ImLog;
        internal static ConfigEntry<float> BasisFeuchte;

        internal const double Schwelle = 3.0;          // dB: Luft daempft ueber die Entfernung gerade so viel -> Grenzfrequenz (wie im README-Vergleich)
        const float Intervall = 30f;

        // zuletzt gelesene Spielwerte (NaN = nicht vorhanden)
        static double temp = double.NaN, druckKPa = double.NaN, regen = double.NaN, nebel = double.NaN, wolken = double.NaN;
        static bool hatWetter;
        static double feuchte = 70.0;

        static volatile LuftTabelle tabelle = new LuftTabelle();
        static volatile LuftBaender baender = LuftBaender.Standard();     // Luftabsorption je Oktavband (fuer die Laubdaempfung, Wald.cs)
        static float naechste;
        static bool neuBerechnen = true, meldeStart, vollGemeldet;
        static string letzteMeldung = "";

        // Luftdruck-Knoten des Servers (Zeit in Ticks, Druck in kPa)
        static long[] knotenZeit;
        static double[] knotenDruck;

        // Diagnose
        internal static int Berechnungen;

        internal static bool An => Aktiv != null && Aktiv.Value;

        /// <summary>Luftabsorption (dB/m) bei 63 Hz .. 16 kHz fuer das aktuelle Wetter.</summary>
        internal static LuftBaender Baender => baender;

        internal static void Binden(ConfigFile c, string sek)
        {
            Aktiv = c.Bind(sek, "Use raid weather (ISO 9613-1)", true,
                "The air low-pass is calculated from the real air absorption formula (ISO 9613-1) with the raid's temperature and pressure and an estimated humidity, instead of the fixed curve. Off = the old fixed curve (12 kHz at 19 m). The \"Air clarity factor\" and the 2.5 kHz floor still apply. Applies live.");
            BasisFeuchte = c.Bind(sek, "Base humidity (%)", 60f,
                new ConfigDescription("The game has no air humidity, so it is estimated: this is the value for dry weather (no rain, no fog); clouds add up to +10 points, fog gives 80-100 %, rain 95-100 %. Lower = dry air = duller highs at distance. Applies live.",
                    new AcceptableValueRange<float>(10f, 100f)));
            ImLog = c.Bind(sek, "Show weather in log", true,
                "Writes temperature, pressure, estimated humidity, rain, fog and clouds to the BepInEx log and the sound log at raid start and whenever they change noticeably. Applies live.");
            Aktiv.SettingChanged += (_, __) => neuBerechnen = true;
            BasisFeuchte.SettingChanged += (_, __) => neuBerechnen = true;
        }

        internal static void Anwenden(Harmony h)
        {
            try
            {
                var m1 = AccessTools.DeclaredMethod(typeof(WeatherController), nameof(WeatherController.SetWeatherNodes));
                var m2 = AccessTools.DeclaredMethod(typeof(WeatherController), nameof(WeatherController.SetWeatherForce));
                if (m1 == null || m2 == null)
                {
                    Plugin.Log.LogWarning("Weather: WeatherController targets not found (game update?) -> pressure stays at 101.325 kPa.");
                    return;
                }
                h.Patch(m1, postfix: new HarmonyMethod(typeof(Wetter), nameof(NachKnoten)));
                h.Patch(m2, postfix: new HarmonyMethod(typeof(Wetter), nameof(NachErzwungen)));
            }
            catch (Exception e) { Statistik.Fehler(e); }
        }

        static void NachKnoten(WeatherNode[] nodes)
        {
            try
            {
                if (nodes == null || nodes.Length == 0) return;
                var z = new long[nodes.Length]; var d = new double[nodes.Length];
                for (int i = 0; i < nodes.Length; i++) { z[i] = nodes[i].Time; d[i] = Iso9613.DruckZuKPa(nodes[i].AtmospherePressure); }
                knotenZeit = z; knotenDruck = d;
                naechste = 0f;      // beim naechsten Tick neu lesen
            }
            catch (Exception e) { Statistik.Fehler(e); }
        }

        static void NachErzwungen(WeatherNode end)
        {
            try
            {
                knotenZeit = new[] { end.Time }; knotenDruck = new[] { Iso9613.DruckZuKPa(end.AtmospherePressure) };
                naechste = 0f;
            }
            catch (Exception e) { Statistik.Fehler(e); }
        }

        internal static void RaidStart()
        {
            meldeStart = true; vollGemeldet = false; naechste = 0f; letzteMeldung = "";
            Lesen();
        }

        internal static void RaidEnde()
        {
            hatWetter = false; neuBerechnen = true;
            temp = druckKPa = regen = nebel = wolken = double.NaN;
            knotenZeit = null; knotenDruck = null;
        }

        // Jeden Frame aus Plugin.Update
        internal static void Tick()
        {
            try
            {
                float t = Time.realtimeSinceStartup;
                if (t >= naechste) { naechste = t + Intervall; Lesen(); }
                if (neuBerechnen) Neuberechnen();
            }
            catch (Exception e) { Statistik.Fehler(e); }
        }

        static void Lesen()
        {
            double t = double.NaN, r = double.NaN, n = double.NaN, w = double.NaN, p = double.NaN;
            bool ok = false;
            try
            {
                var wc = WeatherController.Instance;
                if (wc != null)
                {
                    var k = wc.WeatherCurve;
                    if (k != null) { t = k.Temperature; r = k.Rain; n = k.Fog; w = k.Cloudiness; ok = true; }
                }
            }
            catch (Exception e) { Statistik.Fehler(e); }
            if (ok)
            {
                if (double.IsNaN(t) || double.IsInfinity(t) || t < -60 || t > 60) t = double.NaN;
                if (double.IsNaN(r) || double.IsInfinity(r)) r = double.NaN;
                if (double.IsNaN(n) || double.IsInfinity(n)) n = double.NaN;
                if (double.IsNaN(w) || double.IsInfinity(w)) w = double.NaN;
                p = DruckJetzt();
            }
            bool vorher = hatWetter;
            hatWetter = ok;
            temp = t; regen = r; nebel = n; wolken = w; druckKPa = p;
            if (ok != vorher) neuBerechnen = true;
            if (Math.Abs(feuchte - Schaetzen()) > 0.01 || Math.Abs(tabelle.T - Effektiv(temp, 20.0)) > 0.05 || Math.Abs(tabelle.P - Effektiv(druckKPa, Iso9613.Bezugsdruck)) > 0.05)
                neuBerechnen = true;
            if (ok) Melden();
        }

        static double Effektiv(double wert, double ersatz) => double.IsNaN(wert) ? ersatz : wert;

        static double Schaetzen() => Iso9613.FeuchteSchaetzen(BasisFeuchte.Value, wolken, regen, nebel);

        static double DruckJetzt()
        {
            var z = knotenZeit; var d = knotenDruck;
            if (z == null || d == null || z.Length == 0) return double.NaN;
            if (z.Length == 1) return d[0];
            long jetzt = 0;
            try
            {
                var gw = Singleton<GameWorld>.Instance;
                if (gw != null && gw.GameDateTime != null) jetzt = gw.GameDateTime.Calculate().Ticks;
            }
            catch { jetzt = 0; }
            if (jetzt == 0)
            {
                double s = 0; for (int i = 0; i < d.Length; i++) s += d[i];
                return s / d.Length;
            }
            if (jetzt <= z[0]) return d[0];
            for (int i = 1; i < z.Length; i++)
            {
                if (jetzt <= z[i])
                {
                    double a = (double)(jetzt - z[i - 1]) / Math.Max(1L, z[i] - z[i - 1]);
                    return d[i - 1] + (d[i] - d[i - 1]) * a;
                }
            }
            return d[d.Length - 1];
        }

        static void Neuberechnen()
        {
            neuBerechnen = false;
            feuchte = Schaetzen();
            double t = Effektiv(temp, 20.0), p = Effektiv(druckKPa, Iso9613.Bezugsdruck);
            var neu = new LuftTabelle();
            neu.Neu(t, p, feuchte, Schwelle);
            tabelle = neu;
            baender = LuftBaender.Neu(t, p, feuchte);
            Berechnungen++;
        }

        /// <summary>Grenzfrequenz der Luft fuer die Entfernung d (m), ohne Klarheitsfaktor und ohne Grenzen.</summary>
        internal static float Grenzfrequenz(float d) => tabelle.Hole(d);

        static string Text()
        {
            var inv = CultureInfo.InvariantCulture;
            if (!hatWetter) return "no raid weather (defaults 20 C, 101.3 kPa, " + feuchte.ToString("F0", inv) + " % RH)";
            return string.Format(inv, "T {0} C, p {1} kPa, RH {2:F0} % (estimate), rain {3}, fog {4}, clouds {5}",
                double.IsNaN(temp) ? "n/a (20)" : temp.ToString("F1", inv),
                double.IsNaN(druckKPa) ? "n/a (101.3)" : druckKPa.ToString("F1", inv),
                feuchte,
                double.IsNaN(regen) ? "n/a" : regen.ToString("F2", inv),
                double.IsNaN(nebel) ? "n/a" : nebel.ToString("F3", inv),
                double.IsNaN(wolken) ? "n/a" : wolken.ToString("F2", inv));
        }

        // 0.8.3: Beim Raidstart (AudioRoomStorage) sind die Wetterknoten oft noch nicht da (Druck n/a, Kurve noch Vorgabe).
        internal static bool Vollstaendig => hatWetter && !double.IsNaN(druckKPa);

        /// <summary>Eine Zeile fuer den Kopf des Klang-Logs.</summary>
        internal static string Kopfzeile()
        {
            string s = (Vollstaendig ? "weather: " : "weather (not complete yet at file start - the raid values follow in the first '[Weather] complete' line): ")
                       + Text() + " | air absorption " + (An ? "ISO 9613-1 from weather" : "fixed curve");
            if (An) s += string.Format(CultureInfo.InvariantCulture, " | cut-off 50 m {0:F0} Hz, 100 m {1:F0} Hz, 300 m {2:F0} Hz (3 dB point, before clarity factor)",
                         tabelle.Hole(50f), tabelle.Hole(100f), tabelle.Hole(300f));
            return s;
        }

        static void Melden()
        {
            if (neuBerechnen) Neuberechnen();     // Text zeigt die frische Schaetzung
            string schluessel = string.Format(CultureInfo.InvariantCulture, "{0:F0}|{1:F0}|{2:F0}|{3}|{4}", temp, druckKPa, feuchte / 5.0,
                regen > 0.02, nebel >= 0.012);
            bool start = meldeStart;
            bool voll = !vollGemeldet && Vollstaendig;     // erste vollstaendige Lesung dieses Raids immer melden
            if (!start && !voll && schluessel == letzteMeldung) return;
            letzteMeldung = schluessel; meldeStart = false;
            if (voll) vollGemeldet = true;
            if (ImLog == null || !ImLog.Value) return;
            string z = "[Weather] " + (voll ? (start ? "raid start (complete): " : "complete: ") : start ? "raid start (not complete yet): " : "changed: ") + Text();
            Plugin.Log.LogInfo(z);
            KlangLog.Text(z);
        }
    }
}
