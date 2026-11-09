using System;
using System.Collections.Generic;

namespace Materialklang
{
    // 0.8.5 - 15. Ambient emitters: reine Rechnung (ohne Unity, im Offline-Test).
    // Kleine ortsfeste Umgebungsquellen des Spiels (BaseAmbientSoundPlayer: Leuchtreklame "ElectricSign", Lagerfeuer, Wassertropfen ...)
    // haben eine eigene Kurve (Unity CustomRolloff, x = d / MaxDistance), die bei MaxDistance (Customs 3,5-15 m) auf 0 geht.
    // Neue Kurve:
    //  - bis zum Bezugsradius r0 exakt die Spielkurve (an der Quelle gleich laut),
    //  - r0 = erste Entfernung, ab der die Spielkurve steiler faellt als 1/r (-6 dB je Verdopplung),
    //  - danach Pegel(r0) * r0 / d (Punktquelle im Freifeld),
    //  - neue Reichweite = MaxDistance * Faktor (hoechstens Obergrenze, nie kuerzer als vorher),
    //  - weiches Ausblenden (cos^2) auf den letzten 25 % der neuen Reichweite statt Abschneiden.
    // Luftdaempfung (ISO 9613-1) bleibt weg: bis 30 m unter 0,2 dB bei 1 kHz.
    internal static class UmgebungKern
    {
        public const double Obergrenze = 30.0;        // m, groesste neue Reichweite
        public const double AusblendAnteil = 0.25;    // letzte 25 % der neuen Reichweite
        public const double MinBezug = 0.25;          // m, kleinster Bezugsradius (1/r darf nicht gegen 0 laufen)

        /// <summary>Neue Reichweite: MaxDistance * Faktor, hoechstens Obergrenze, nie kuerzer als die alte.</summary>
        public static double NeueReichweite(double alt, double faktor, double grenze = Obergrenze)
            => Math.Max(alt, Math.Min(alt * Math.Max(1.0, faktor), grenze));

        /// <summary>Bezugsradius (m): erste Entfernung, ab der die Spielkurve steiler faellt als 1/r.
        /// alt(x) = Spielkurve mit x = d / maxAlt.</summary>
        public static double Bezugsradius(Func<double, double> alt, double maxAlt)
        {
            if (maxAlt <= MinBezug) return maxAlt;
            int n = 400;
            double schritt = (maxAlt - MinBezug) / n;
            double dVor = MinBezug, gVor = alt(dVor / maxAlt);
            if (gVor <= 1e-6) return MinBezug;
            for (int i = 1; i <= n; i++)
            {
                double d = MinBezug + i * schritt;
                double g = alt(d / maxAlt);
                // lokale Steigung in log-log: dln g / dln d; 1/r = -1
                if (g <= 1e-6) return dVor;
                double s = Math.Log(g / gVor) / Math.Log(d / dVor);
                if (s <= -1.0) return dVor;
                dVor = d; gVor = g;
            }
            return maxAlt;
        }

        /// <summary>Zielpegel (linear, 0..1) in Entfernung d.</summary>
        public static double Ziel(Func<double, double> alt, double maxAlt, double r0, double maxNeu, double trimGain, double d)
        {
            if (d >= maxNeu) return 0.0;
            double g = d <= r0 ? alt(d / maxAlt) : alt(r0 / maxAlt) * r0 / d;
            double start = Math.Max(r0, maxNeu * (1.0 - AusblendAnteil));
            if (d > start)
            {
                double t = (d - start) / (maxNeu - start);
                double c = Math.Cos(t * Math.PI * 0.5);
                g *= c * c;
            }
            g *= trimGain;
            if (g > 1.0) g = 1.0;
            return g < 0 ? 0 : g;
        }

        /// <summary>Baut die Kurve (x = d / maxNeu) als Hermite-Stuetzstellen mit Steigungen.</summary>
        public static KurvenPunkt[] Bauen(Func<double, double> alt, double maxAlt, double maxNeu, double trimDb, out double r0)
        {
            r0 = Bezugsradius(alt, maxAlt);
            double trim = FernschussKern.GainAusDb(trimDb);
            var d = new List<double>();
            // Nahbereich: Spielkurve dicht abtasten (0..r0)
            int nNah = 10;
            for (int i = 0; i <= nNah; i++) d.Add(r0 * i / nNah);
            // 1/r-Teil geometrisch bis zum Ausblendbeginn, dann linear im Ausblendbereich
            double start = Math.Max(r0, maxNeu * (1.0 - AusblendAnteil));
            if (start > r0 * 1.0001)
            {
                int nLog = Math.Max(4, (int)Math.Ceiling(Math.Log(start / Math.Max(r0, MinBezug), 2) * 4));
                double q = Math.Pow(start / r0, 1.0 / nLog);
                double v = r0;
                for (int i = 1; i <= nLog; i++) { v *= q; d.Add(i == nLog ? start : v); }
            }
            int nAus = 8;
            for (int i = 1; i <= nAus; i++) d.Add(start + (maxNeu - start) * i / nAus);

            var k = new KurvenPunkt[d.Count];
            double h = maxNeu * 1e-4;
            for (int i = 0; i < d.Count; i++)
            {
                double di = d[i], xi = di / maxNeu;
                double v = Ziel(alt, maxAlt, r0, maxNeu, trim, di);
                // einseitige Ableitungen (Knick bei r0 und am Ausblendbeginn sauber halten)
                double links = di - h <= 0 ? double.NaN : (v - Ziel(alt, maxAlt, r0, maxNeu, trim, di - h)) / h * maxNeu;
                double rechts = di + h >= maxNeu ? double.NaN : (Ziel(alt, maxAlt, r0, maxNeu, trim, di + h) - v) / h * maxNeu;
                if (double.IsNaN(links)) links = double.IsNaN(rechts) ? 0 : rechts;
                if (double.IsNaN(rechts)) rechts = links;
                if (i == d.Count - 1) { v = 0; rechts = 0; }
                k[i] = new KurvenPunkt((float)xi, (float)v, (float)links, (float)rechts);
            }
            Monoton(k);
            return k;
        }

        /// <summary>0.8.5 (16. Loud signals): Steigungen je Abschnitt nach Fritsch-Carlson begrenzen, damit das Hermite-Stueck nicht
        /// ueberschwingt. Ursache: an einem Knick der Spielkurve (Ladenalarm: flach bis 1 m, dann steil) zwischen zwei weit
        /// auseinanderliegenden Stuetzstellen stieg die Kurve kurz an (+0,002 dB ueber 1,0).</summary>
        internal static void Monoton(KurvenPunkt[] k)
        {
            for (int i = 0; i + 1 < k.Length; i++)
            {
                double h = k[i + 1].T - k[i].T;
                if (h <= 0) continue;
                double delta = (k[i + 1].V - k[i].V) / h;
                if (Math.Abs(delta) < 1e-12) { k[i].Out = 0f; k[i + 1].In = 0f; continue; }
                double a = k[i].Out / delta, b = k[i + 1].In / delta;
                if (a < 0) { k[i].Out = 0f; a = 0; }
                if (b < 0) { k[i + 1].In = 0f; b = 0; }
                double s = a * a + b * b;
                if (s > 9.0)
                {
                    double tau = 3.0 / Math.Sqrt(s);
                    k[i].Out = (float)(tau * a * delta);
                    k[i + 1].In = (float)(tau * b * delta);
                }
            }
        }

        /// <summary>Soll diese Quelle umgestellt werden? (ohne Unity: Typname, Raumklang-Anteil, Reichweite, Pegel an der Quelle)</summary>
        public static bool Kandidat(string typ, float raumAnteil, float maxDistanz, float maxOriginal, float pegelNull)
        {
            if (raumAnteil < 0.5f) return false;                     // 2D/Stereo-Teppich
            if (maxDistanz <= 0f || maxDistanz > maxOriginal) return false;
            if (pegelNull <= 0f) return false;
            switch (typ)
            {
                case "LoopAmbientSoundPlayer":
                case "OneShotAmbientSoundPlayer":
                case "SeasonAmbientSoundPlayer":
                case "SeasonLoopAmbientSoundPlayer":
                case "AmbientSoundPlayer":
                    return true;
                default:
                    // WeatherRandomAmbientSoundPlayer (Wetter/Wind), EventLoopPlayer/EventRandomPlayer (Ereignis-Musik), Unbekanntes
                    return false;
            }
        }
    }
}
