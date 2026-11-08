using System;

namespace Materialklang
{
    // ---------------------------------------------------------------------------------------------
    //  Luftabsorption nach ISO 9613-1:1993 - reine Rechnung ohne Spiel- oder Unity-Typen (laeuft auch im Offline-Test).
    //  Dazu: Schaetzung der relativen Feuchte aus dem Raid-Wetter und eine vorberechnete Tabelle
    //  "Grenzfrequenz je Entfernung", damit pro Geraeusch nur ein Tabellenzugriff noetig ist.
    // ---------------------------------------------------------------------------------------------
    internal static class Iso9613
    {
        public const double Bezugsdruck = 101.325;     // kPa
        const double T0 = 293.15;                      // K, Bezugstemperatur (20 C)
        const double T01 = 273.16;                     // K, Tripelpunkt von Wasser

        /// <summary>
        /// Absorptionskoeffizient alpha in dB/m fuer reine Toene (ISO 9613-1:1993, Gl. 3-5).
        /// f in Hz, tC in Grad Celsius, pKPa = statischer Luftdruck in kPa, rf = relative Feuchte in Prozent.
        /// </summary>
        public static double Alpha(double f, double tC, double pKPa, double rf)
        {
            double T = tC + 273.15;
            double pr = pKPa / Bezugsdruck;
            // Molare Konzentration von Wasserdampf in Prozent: h = rf * (psat/pr) / (pa/pr)   (Anhang B)
            double C = -6.8346 * Math.Pow(T01 / T, 1.261) + 4.6151;
            double psatRel = Math.Pow(10.0, C);                // psat / p_r
            double h = rf * psatRel / pr;
            // Relaxationsfrequenzen Sauerstoff / Stickstoff (Gl. 3 und 4)
            double frO = pr * (24.0 + 4.04e4 * h * (0.02 + h) / (0.391 + h));
            double frN = pr * Math.Pow(T / T0, -0.5) * (9.0 + 280.0 * h * Math.Exp(-4.170 * (Math.Pow(T / T0, -1.0 / 3.0) - 1.0)));
            double f2 = f * f;
            return 8.686 * f2 * (
                1.84e-11 / pr * Math.Sqrt(T / T0)
                + Math.Pow(T / T0, -2.5) * (
                    0.01275 * Math.Exp(-2239.1 / T) / (frO + f2 / frO)
                    + 0.1068 * Math.Exp(-3352.0 / T) / (frN + f2 / frN)));
        }

        /// <summary>
        /// Frequenz, bei der die Luft ueber die Entfernung d genau 'schwelleDb' daempft (alpha(f) * d = schwelleDb).
        /// alpha steigt mit f streng monoton -> Bisektion. Ergebnis auf [fMin, fMax] begrenzt.
        /// </summary>
        public static double Grenzfrequenz(double d, double tC, double pKPa, double rf, double schwelleDb = 3.0, double fMin = 100.0, double fMax = 40000.0)
        {
            double ziel = schwelleDb / Math.Max(d, 0.01);       // dB/m
            if (Alpha(fMax, tC, pKPa, rf) <= ziel) return fMax;
            if (Alpha(fMin, tC, pKPa, rf) >= ziel) return fMin;
            double lo = Math.Log(fMin), hi = Math.Log(fMax);
            for (int i = 0; i < 40; i++)
            {
                double mid = 0.5 * (lo + hi);
                if (Alpha(Math.Exp(mid), tC, pKPa, rf) < ziel) lo = mid; else hi = mid;
            }
            return Math.Exp(0.5 * (lo + hi));
        }

        // ---- Schaetzung der relativen Feuchte (das Spiel hat keine Luftfeuchte) ----

        /// <summary>
        /// Relative Feuchte in % aus Basiswert, Wolken, Regen und Nebel (ALLES SCHAETZUNG, keine Spielgroesse):
        ///  - Regen (&gt; 0.02, Skala 0..1): 95 + 5 * Regen
        ///  - Nebel (Streudichte ab 0.012; 0.004 = klar, 0.015 leicht, 0.035 Nebel, 0.075 dicht): 80 .. 100, linear 0.015 -> 0.075
        ///  - sonst Basiswert, bis +10 Punkte bei bedecktem Himmel (Bewoelkung -0.5 .. +0.5 im Spiel; -1 = klar, +1 = voll bedeckt)
        /// Von den Faellen gilt der hoechste Wert; NaN-Eingaben zaehlen als "nicht vorhanden".
        /// </summary>
        public static double FeuchteSchaetzen(double basis, double wolken, double regen, double nebel)
        {
            double rf = basis;
            if (!double.IsNaN(wolken))
                rf += 10.0 * Math.Max(0.0, Math.Min(1.0, (wolken + 0.5) / 1.0));
            if (!double.IsNaN(nebel) && nebel >= 0.012)
                rf = Math.Max(rf, 80.0 + 20.0 * Math.Max(0.0, Math.Min(1.0, (nebel - 0.015) / 0.06)));
            if (!double.IsNaN(regen) && regen > 0.02)
                rf = Math.Max(rf, 95.0 + 5.0 * Math.Min(1.0, regen));
            return Math.Max(1.0, Math.Min(100.0, rf));
        }

        /// <summary>
        /// Druck als Zahl aus dem Spiel (SPT liefert mmHg, ~760) in kPa. Bereiche: unter 200 = schon kPa, 200..899 = mmHg, ab 900 = hPa.
        /// Unsinn (NaN, &lt;= 0) -> Bezugsdruck.
        /// </summary>
        public static double DruckZuKPa(double wert)
        {
            if (double.IsNaN(wert) || wert <= 0) return Bezugsdruck;
            double kpa = wert < 200 ? wert : wert < 900 ? wert * 0.133322 : wert * 0.1;
            return Math.Max(60.0, Math.Min(120.0, kpa));
        }
    }

    /// <summary>
    /// Vorberechnete Tabelle: Grenzfrequenz des Luft-Tiefpasses je Entfernung (logarithmisch gestuft, 1 m .. 3 km).
    /// Neu aufgebaut, sobald sich Wetterwerte aendern (selten); Abfrage = zwei Tabellenwerte mischen.
    /// </summary>
    internal sealed class LuftTabelle
    {
        public const double DMin = 1.0, DMax = 3000.0;
        public const int Stufen = 96;
        readonly float[] fc = new float[Stufen];
        readonly double logMin = Math.Log(DMin), schritt = (Math.Log(DMax) - Math.Log(DMin)) / (Stufen - 1);

        public double T, P, RF, Schwelle;

        public LuftTabelle() { Neu(20.0, Iso9613.Bezugsdruck, 70.0, 3.0); }

        public void Neu(double tC, double pKPa, double rf, double schwelleDb)
        {
            T = tC; P = pKPa; RF = rf; Schwelle = schwelleDb;
            for (int i = 0; i < Stufen; i++)
                fc[i] = (float)Iso9613.Grenzfrequenz(Math.Exp(logMin + i * schritt), tC, pKPa, rf, schwelleDb);
        }

        public float Hole(float d)
        {
            if (!(d > (float)DMin)) return fc[0];
            double x = (Math.Log(d) - logMin) / schritt;
            if (x >= Stufen - 1) return fc[Stufen - 1];
            int i = (int)x;
            float t = (float)(x - i);
            return fc[i] + (fc[i + 1] - fc[i]) * t;
        }
    }
}
