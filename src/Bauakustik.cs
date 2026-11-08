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

        // 0.8.3: Dicke einer EINZELNEN Flaeche (Mesh ohne Rueckseite, nur eine Seite getroffen) je Materialgruppe, in m.
        // Bis 0.8.2 galt fuer jede Gruppe die "Shell thickness" (0,15 m): ein Plakat (Kunststoff), ein Autoblech oder eine
        // Glasscheibe zaehlten wie 15 cm Wand (34-41 dB). Einseitige Flaechen sind im Spiel fast immer duenne Teile; nur bei
        // massiven Gruppen (Beton/Stein, Erde, Kies, Wasser, Schnee, Unbekannt) steht die Flaeche fuer eine Wandschale.
        // Werte = uebliche Bauteildicken, ANNAHMEN (keine Messung): Stahlwand Container 6-10 mm, Karosserie-/Trapezblech
        // 0,7-2 mm, Brett/Sperrholz 12 mm, Bohle 40 mm, Floatglas 4-6 mm (EN 572-2), Folie/Plakat/Pappe ~5 mm,
        // Innentuerblatt 40 mm (DIN 18101). NaN = die Einstellung "Shell thickness". Reihenfolge wie enum Gruppe.
        static readonly float[] EinzelTab =
        {
            float.NaN, float.NaN, float.NaN, 0.01f, 0.002f, 0.04f, 0.012f, 0.006f, 0.005f, 0f, float.NaN, float.NaN, float.NaN, 0.04f, float.NaN
        };

        /// <summary>
        /// 0.8.4: Hochpass im Physik-Modus. Hinter einer Wand bleiben nach dem Massengesetz (+6 dB je Oktave) die Tiefen am
        /// laengsten erhalten; ein Hochpass, der mit der Verdeckung steigt (Spielkurve), ist physikalisch falsch. Ergebnis: der
        /// Hochpass bei offenem Weg (hpOffen), nie hoeher als der des Spiels und nie ueber dem Tiefpass.
        /// </summary>
        public static float HochpassPhysik(float hpSpiel, float hpOffen, float lp)
        {
            float hp = hpOffen > 0f && hpOffen < hpSpiel ? hpOffen : hpSpiel;
            return hp > lp ? lp : hp;
        }

        // ---- Hall (HallZonen.cs), 0.8.3 ----
        // Sabine mit Luftabsorption: T60 = 0.161 * V / (S*alpha + 4*m*V), m = Energie-Daempfungskoeffizient der Luft in 1/m
        // = alpha_Luft(dB/m) / (10*log10(e)). Quelle: Kuttruff, Room Acoustics (6. Aufl., CRC 2017), Kap. 5; ISO 354:2003 (4mV-Term).
        // Hier je V/S geschrieben: T60 = 0.161 * VzuS / (alpha + 4*m*VzuS).
        public static double Sabine(double vzus, double alpha, double mLuft) => 0.161 * vzus / (alpha + 4.0 * mLuft * vzus);

        /// <summary>m (1/m) aus der Luftdaempfung in dB/m.</summary>
        public static double LuftM(double dbJeMeter) => dbJeMeter / (10.0 * Math.Log10(Math.E));

        /// <summary>
        /// Verschiebung von Nachhallzeit (s) und Hallpegel (dB) gegenueber dem eingebackenen alpha 'gebacken'.
        /// alphaMin = Untergrenze der mittleren Absorption (nicht gesehene Absorber: Oeffnungen, Moebel, Fugen); damit ist die neue
        /// Nachhallzeit hoechstens gebacken/alphaMin mal die gebackene. Pegel: diffuses Feld ~ 1/A (A = S*alpha + 4mV).
        /// </summary>
        public static void HallKorrektur(double vzus, double alpha, double gebacken, double alphaMin, double mLuft, out double dRt, out double dPegel)
        {
            double a = Math.Max(alpha, alphaMin);
            double luft = 4.0 * mLuft * vzus;
            dRt = Sabine(vzus, a, mLuft) - Sabine(vzus, gebacken, mLuft);
            dPegel = 10.0 * Math.Log10((gebacken + luft) / (a + luft));
        }

        /// <summary>0.8.4: steht eine Einzelflaeche der Gruppe g fuer eine Wandschale (Beton, Erde, Kies, ...; Tabellenwert NaN)?</summary>
        public static bool IstSchalenGruppe(int g) => g >= 0 && g < EinzelTab.Length && float.IsNaN(EinzelTab[g]);

        /// <summary>Angenommene Dicke (m) einer einzelnen Flaeche der Gruppe g; nie dicker als die Schale.</summary>
        public static float EinzelDicke(int g, float schale)
        {
            float t = g >= 0 && g < EinzelTab.Length ? EinzelTab[g] : float.NaN;
            return float.IsNaN(t) ? schale : Math.Min(t, schale);
        }
    }
}
