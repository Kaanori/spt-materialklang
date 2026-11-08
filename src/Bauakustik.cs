// ---------------------------------------------------------------
//  Reine Rechnung (ohne Unity): Massengesetz + Koinzidenz-Korrektur fuer dicke, schwere Waende.
//  Wird von Physik.Daemmung benutzt und im Offline-Test (Materialklang-KlangTest) geprueft.
//
//  Quellen:
//   - Feldeinfall-Massengesetz  TL = 20*log10(m*f) - 47 dB   (m in kg/m2, f in Hz):
//       Beranek & Ver, "Noise and Vibration Control Engineering" (Wiley); Bies, Hansen & Howard,
//       "Engineering Noise Control" (5. Aufl., CRC 2017). Gilt unterhalb der Koinzidenzfrequenz.
//   - Dreiteilige Form (Massengesetz bis fc/2, Gerade von fc/2 bis fc, Cremer-Ast darueber) nach dem
//       Verfahren von B. H. Sharp, "Prediction methods for the sound transmission of building elements",
//       Noise Control Engineering 11(2), 53-63 (1978) - Literaturangabe geprueft (INCE-Inhaltsverzeichnis Bd. 11 Nr. 2;
//       dieselbe Angabe in Fachaufsaetzen, die Sharps Modell anwenden). Der Originalartikel war hier nicht abrufbar:
//       die Konstanten unten stammen aus der in Lehrbuechern/Aufsaetzen wiedergegebenen Form, NICHT aus dem Artikel selbst.
//   - Koinzidenz-Ast (f >= fc), Cremer:  TL = 20*log10(m*f) - 48 + 10*log10(2*eta*f/(pi*fc))
//       (Konstante -48 in den Quellen, hier -47 wie im uebrigen Massengesetz: 1 dB Unterschied).
//   - Koinzidenzfrequenz einer dicken Platte: fc = c0^2/(2*pi*h) * sqrt(12*rho*(1-nu^2)/E)
//       (Biegewellen-Lehrbuchformel, z.B. Bies & Hansen Kap. 7; Beton: fc*h ~ 19 Hz*m).
//   - eta ist hier ein EFFEKTIVER Gesamtverlustfaktor (innere Daempfung + Randverluste + Abstrahlung). Der Wert 0.1
//       ist an typische Messkurven gemessen (100 mm Beton: ca. 10 dB Einbruch bei 250 Hz) und KEIN Literaturwert.
// ---------------------------------------------------------------
using System;

namespace Materialklang
{
    internal static class Bauakustik
    {
        public const double C0 = 343.0;

        /// <summary>Feldeinfall-Massengesetz in dB (nicht begrenzt). m in kg/m2.</summary>
        public static double Massengesetz(double m, double f) => 20.0 * Math.Log10(m * f) - 47.0;

        /// <summary>Koinzidenzfrequenz (Hz) einer homogenen Platte. E in Pa. 0, wenn kein Plattenmodell gilt (E &lt;= 0).</summary>
        public static double Koinzidenzfrequenz(double dichte, double dicke, double eModul, double nu)
        {
            if (eModul <= 0.0 || dicke <= 0.0 || dichte <= 0.0) return 0.0;
            return C0 * C0 / (2.0 * Math.PI * dicke) * Math.Sqrt(12.0 * dichte * (1.0 - nu * nu) / eModul);
        }

        /// <summary>
        /// Korrigiert das Massengesetz oberhalb von fc/2. Unterhalb von fc/2 oder ohne Plattenmodell (fc = 0)
        /// kommt rMl unveraendert zurueck - duenne Teile bleiben also, wie sie sind.
        /// </summary>
        public static double Koinzidenz(double rMl, double m, double f, double fc, double eta)
        {
            if (fc <= 0.0 || f <= 0.5 * fc) return rMl;
            double rHalb = Massengesetz(m, 0.5 * fc);
            double rFc = Massengesetz(m, fc) + 10.0 * Math.Log10(2.0 * eta / Math.PI);
            double r;
            if (f < fc) r = rHalb + (rFc - rHalb) * (Math.Log(f / (0.5 * fc)) / Math.Log(2.0));   // Gerade in dB ueber log f
            else r = rMl + 10.0 * Math.Log10(2.0 * eta * f / (Math.PI * fc));
            return Math.Min(r, rMl);                                                               // nie ueber dem Massengesetz
        }
    }
}
