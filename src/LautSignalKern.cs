using System;
using System.Collections.Generic;

namespace Materialklang
{
    // 0.8.5 - 16. Loud signals: reine Rechnung (ohne Unity, im Offline-Test).
    // Laute Signalgeber der Karten (Ladenalarm Interchange, KIBA-Alarm, Labyrinth-Boss-Alarm) sind Szenen-AudioSources, die das Spiel
    // ueber SourceOccluder in den Occluder haengt. Ihre Reichweite (40-120 m) und Kurve (linear auf 0) kommen aus der Szene.
    // Eine Alarmglocke/Sirene hat ~100-120 dB(A) in 1 m und ist im Freien hunderte Meter hoerbar (-6 dB je Verdopplung, dazu Luft
    // nach ISO 9613-1 - die rechnet True Acoustics im Occluder schon mit, ebenso Waende/Raeume).
    // Neue Kurve wie Abschnitt 15 (UmgebungKern.Bauen): bis r0 die Spielkurve, dann 1/r, weicher Auslauf auf den letzten 25 %.
    internal static class LautSignalKern
    {
        public const double Grenze = 600.0;     // m, groesste einstellbare Reichweite
        public const double Standard = 300.0;   // m
        public const string StandardListe = "alarm, siren, klaxon, bell, horn, announc";

        /// <summary>Stichwortliste aus den Einstellungen: durch Komma/Semikolon getrennt, klein, ohne Leerzeichen am Rand.</summary>
        public static string[] ListeLesen(string s)
        {
            var r = new List<string>();
            if (string.IsNullOrEmpty(s)) return r.ToArray();
            foreach (var t in s.Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries))
            {
                var w = t.Trim().ToLowerInvariant();
                if (w.Length > 0 && !r.Contains(w)) r.Add(w);
            }
            return r.ToArray();
        }

        /// <summary>Stichwort am Wortanfang im Namen? Wortanfang = Namensanfang, nach einem Nicht-Buchstaben oder ein
        /// Grossbuchstabe nach einem Kleinbuchstaben ("KibaAlarm"). So trifft "horn" nicht "Thorn", "bell" nicht "Isabell".</summary>
        public static bool WortTrifft(string name, string wort)
        {
            if (string.IsNullOrEmpty(name) || string.IsNullOrEmpty(wort)) return false;
            int i = 0;
            while ((i = name.IndexOf(wort, i, StringComparison.OrdinalIgnoreCase)) >= 0)
            {
                if (i == 0) return true;
                char v = name[i - 1], c = name[i];
                if (!char.IsLetter(v)) return true;
                if (char.IsLower(v) && char.IsUpper(c)) return true;
                i++;
            }
            return false;
        }

        /// <summary>Erstes Stichwort, das einen der Namen trifft (oder null).</summary>
        public static string Treffer(IList<string> namen, string[] liste)
        {
            if (namen == null || liste == null) return null;
            foreach (var w in liste)
                foreach (var n in namen)
                    if (WortTrifft(n, w)) return w;
            return null;
        }

        /// <summary>Neue Reichweite: Einstellung (hoechstens Grenze), nie kuerzer als die alte.</summary>
        public static double NeueReichweite(double alt, double soll, double grenze = Grenze)
            => Math.Max(alt, Math.Min(soll, grenze));

        /// <summary>Unity "Linear Rolloff": 1 bis minDistance, dann linear auf 0 bei maxDistance.</summary>
        public static double GainLinear(double d, double min, double max)
        {
            if (d <= min) return 1.0;
            if (d >= max || max <= min) return 0.0;
            return 1.0 - (d - min) / (max - min);
        }

        /// <summary>Unity "Logarithmic Rolloff": 1 bis minDistance, dann min/d (ab maxDistance konstant).</summary>
        public static double GainLog(double d, double min, double max)
        {
            if (min <= 0) min = 1e-3;
            if (d <= min) return 1.0;
            return min / Math.Min(d, Math.Max(max, min));
        }

        /// <summary>Soll diese Quelle umgestellt werden? (Stichwort getroffen, 3D, gueltige Reichweite, Pegel an der Quelle,
        /// kein Radio - ClientBroadcastPlayer stellt Kurve und Reichweite selbst)</summary>
        public static bool Kandidat(string treffer, float raumAnteil, float maxDistanz, float pegelNull, bool radio)
        {
            if (treffer == null || radio) return false;
            if (raumAnteil < 0.5f) return false;
            if (maxDistanz <= 0f || maxDistanz >= Grenze) return false;
            return pegelNull > 0f;
        }
    }
}
