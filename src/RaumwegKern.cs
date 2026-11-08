using System;

namespace Materialklang
{
    // ------------------------------------------------------------------------------------------
    // 0.8.4 - Raumweg physikalisch (Physik-Modus). Reine Rechnung ohne Unity (Offline-Test). Spiel-Teil: Raumweg.cs.
    //
    // Ursache (Lighthouse-Log 08.11.2026 19:12, MARK 7): Granate draussen 40,6 m, Hoerer im Wasserwerk, offenes Garagentor -
    //   "path=via doors/windows (rooms) effect=0.23 -> insulation 13.7 dB". Der Wert 0,23 ist KEINE Daemmung, sondern die
    //   Wegkosten-Heuristik des Spiels (PropagationJob.GetRouteCostAndDistance, beste Route = kleinste Kosten):
    //     je Portal  (Segmentanteil * (Knickwinkel^diffractionExponent + Durchgangskosten traversalMaxCost) + Schliessung)
    //                * WallOcclusion(Raeume)
    //     + Hoehe    Segmentanteil * (|dy| / typicalRoomHeight * absoluteHeightWeight + Steigung^heightExponent * Gewicht hoch/runter)
    //     + Umweg    (1 - Direktlinie / Weglaenge) * distanceWeight * Anteil bis zum letzten Portal
    //   danach mal GetEnvironmentFactor der Klanggruppe (draussen <-> drinnen, bei Figurenklaengen 1,5-2,0).
    //   Ein OFFENES Tor (Schliessung 0) kostet also trotzdem Durchgang, Knick, Hoehe und Umweg. Der Physik-Modus las diese
    //   Kosten bisher linear als dB-Skala (Wertung 0..1 = 0..60 dB): 0,1 Wegkosten = 6 dB, egal ob dort ein Tor offen steht.
    // Loesung: im Physik-Modus wird die Route aus Geometrie und Portalzustand gerechnet (Energie, zwei parallele Wege):
    //   1. Direkt durch die Oeffnungen (geometrisch): Kugelwelle ueber die ganze Weglaenge, an jeder Oeffnung Beugung um die
    //      Kante nach Kurze-Anderson (1971), N = 2*Umweg/lambda, Illuminated-Zone-Fortsetzung bis N = -0,2, je Kante hoechstens
    //      20 dB (Deckel der Einzelbeugung, ISO 9613-2). Nur der offene Teil einer Oeffnung traegt diesen Weg.
    //   2. Ueber die Raeume (diffus, Sabine/Kuttruff): Leistung durch die Oeffnung P = I * S * t (t = offen + zu * tau),
    //      im Innenraum trifft sie als P/A auf die naechste Oeffnung, am Hoerer gilt 4P/A; im Freien strahlt sie als
    //      P/(2 pi r^2) weiter. A = aequivalente Absorptionsflaeche des Raums (Raumoberflaeche * alpha + offene Portalflaechen).
    //      Der Hall des Quellraums zaehlt nicht (den spielt das Spiel selbst ueber Meta XR).
    //   Bezug: Freifeld W/(4 pi d^2) in Direktlinie (die Entfernungskurve rechnet Unity/SEB). Daemmung = 10 lg(Bezug / Summe), >= 0.
    //   Geschlossene Tueren/heile Fenster: tau aus den bisherigen Werten (Abschnitt 4, Zielwert * Max. insulation), also weiter
    //   die Tuerwerte der Mod; deren Tiefpass bleibt die Abbildung des Physik-Modus. Beugung macht den Rest frequenzabhaengig
    //   (Tiefpass aus der Mehrdaempfung bei 4 kHz gegenueber der Bezugsfrequenz, 2-poliger Filter).
    // ------------------------------------------------------------------------------------------

    internal struct V3
    {
        public double X, Y, Z;
        public V3(double x, double y, double z) { X = x; Y = y; Z = z; }
        public static V3 operator +(V3 a, V3 b) => new V3(a.X + b.X, a.Y + b.Y, a.Z + b.Z);
        public static V3 operator -(V3 a, V3 b) => new V3(a.X - b.X, a.Y - b.Y, a.Z - b.Z);
        public static V3 operator *(V3 a, double s) => new V3(a.X * s, a.Y * s, a.Z * s);
        public static double Dot(V3 a, V3 b) => a.X * b.X + a.Y * b.Y + a.Z * b.Z;
        public double Laenge => Math.Sqrt(X * X + Y * Y + Z * Z);
        public static double Abstand(V3 a, V3 b) => (a - b).Laenge;
        public bool IstNull => X == 0 && Y == 0 && Z == 0;
    }

    /// <summary>Eine Oeffnung (Portal) des Raumwegs, wie RoomPair.AudioPortalData plus Zustand.</summary>
    internal struct RaumPortal
    {
        public V3 Mitte, Rechts, Oben, Normale;
        public double HalbX, HalbY;
        public double Offen;      // 0 = zu, 1 = ganz offen (Anteil der freien Flaeche)
        public double Tau;        // Energie-Transmissionsgrad des geschlossenen Teils (Tuerblatt, Glas)
        public double Flaeche => 4.0 * HalbX * HalbY;
    }

    internal struct RaumwegErgebnis
    {
        public bool Gueltig;
        public double Db, DbHoch;          // Daemmung bei Bezugs- und hoher Frequenz
        public double DbTuer;              // Anteil der geschlossenen Tueren/Fenster (Massegesetz-Tiefpass)
        public double DbGeo, DbDiffus;     // die beiden Wege einzeln (Bezugsfrequenz)
        public double Laenge, Direkt;      // Weglaenge ueber die Portale, Direktlinie
    }

    internal static class RaumwegKern
    {
        public const double Schall = 343.0;
        public const double KanteMaxDb = 20.0;
        public const double MinFlaecheA = 1.0;

        /// <summary>Wie PropagationJob.ComputeClosestPointOnPortal: Punkt auf dem Portal-Rechteck zwischen Eintritt und Austritt.</summary>
        public static V3 PortalPunkt(in RaumPortal p, V3 ein, V3 aus)
        {
            double a = V3.Dot(ein - p.Mitte, p.Rechts), b = V3.Dot(ein - p.Mitte, p.Oben);
            double c = V3.Dot(aus - p.Mitte, p.Rechts), d = V3.Dot(aus - p.Mitte, p.Oben);
            double n1 = Math.Abs(V3.Dot(ein - p.Mitte, p.Normale)), n2 = Math.Abs(V3.Dot(aus - p.Mitte, p.Normale));
            double s = n1 + n2;
            double u = s < 1e-6 ? (a + c) * 0.5 : (n2 * a + n1 * c) / s;
            double v = s < 1e-6 ? (b + d) * 0.5 : (n2 * b + n1 * d) / s;
            u = Math.Max(-p.HalbX, Math.Min(p.HalbX, u));
            v = Math.Max(-p.HalbY, Math.Min(p.HalbY, v));
            return p.Mitte + p.Rechts * u + p.Oben * v;
        }

        /// <summary>Kurze-Anderson (1971): Einfuegungsdaempfung einer Kante, N = Fresnel-Zahl (negativ = Sichtlinie frei).
        /// 5 dB an der Schattengrenze, 0 dB ab N = -0,2 in der freien Zone, gedeckelt bei 20 dB.</summary>
        public static double KurzeAnderson(double n)
        {
            double db;
            if (n >= 0)
            {
                double x = Math.Sqrt(2 * Math.PI * n);
                db = x < 1e-6 ? 5.0 : 5.0 + 20.0 * Math.Log10(x / Math.Tanh(x));
            }
            else if (n > -0.2)
            {
                double x = Math.Sqrt(2 * Math.PI * -n);
                db = x < 1e-6 ? 5.0 : 5.0 + 20.0 * Math.Log10(x / Math.Tan(x));
            }
            else db = 0;
            return Math.Max(0, Math.Min(KanteMaxDb, db));
        }

        /// <summary>Umweg (m) um die naechste Kante der Oeffnung fuer den Weg q -> r. positiv = Schatten (Sichtlinie durch die
        /// Oeffnung verdeckt), negativ = Sichtlinie frei (Abstand zur Kante als Umweg, fuer die Illuminated-Zone).</summary>
        public static double Umweg(in RaumPortal p, V3 q, V3 r, V3 spielPunkt)
        {
            double dq = V3.Dot(q - p.Mitte, p.Normale), dr = V3.Dot(r - p.Mitte, p.Normale);
            double direkt = V3.Abstand(q, r);
            bool durch = false; double u = 0, v = 0;
            if (dq * dr < 0)                                     // q und r auf verschiedenen Seiten: Schnittpunkt mit der Ebene
            {
                double t = dq / (dq - dr);
                V3 s = q + (r - q) * t;
                u = V3.Dot(s - p.Mitte, p.Rechts); v = V3.Dot(s - p.Mitte, p.Oben);
                durch = Math.Abs(u) <= p.HalbX && Math.Abs(v) <= p.HalbY;
            }
            if (!durch)
            {
                double um = V3.Abstand(q, spielPunkt) + V3.Abstand(spielPunkt, r) - direkt;
                return Math.Max(um, 0);
            }
            // freie Sicht: naechste Kante
            double ex = p.HalbX - Math.Abs(u), ey = p.HalbY - Math.Abs(v);
            V3 kante = ex < ey
                ? p.Mitte + p.Rechts * (Math.Sign(u == 0 ? 1 : u) * p.HalbX) + p.Oben * v
                : p.Mitte + p.Rechts * u + p.Oben * (Math.Sign(v == 0 ? 1 : v) * p.HalbY);
            return -(V3.Abstand(q, kante) + V3.Abstand(kante, r) - direkt);
        }

        public static double Fresnel(double umweg, double f) => 2.0 * umweg * f / Schall;

        /// <summary>Eine Route von der Quelle zum Hoerer (beide in echter Richtung). punkte[k] = Portalpunkt k (wie im Spiel),
        /// raumA[k] = Absorptionsflaeche des Raums VOR Portal k (k = 0: Quellraum, k = n: Hoererraum), &lt;= 0 = im Freien.
        /// beugungRef/beugungHoch (Laenge n) bekommen die Kanten-dB fuer das Log.</summary>
        public static RaumwegErgebnis Route(V3 quelle, V3 hoerer, RaumPortal[] p, V3[] punkte, int n, double[] raumA,
            double fRef, double fHoch, double maxDb, double[] beugungRef = null, double[] umwegLog = null)
        {
            var e = new RaumwegErgebnis();
            double d = Math.Max(V3.Abstand(quelle, hoerer), 0.5);
            double laenge = 0; V3 vor = quelle;
            for (int k = 0; k < n; k++) { laenge += V3.Abstand(vor, punkte[k]); vor = punkte[k]; }
            laenge += V3.Abstand(vor, hoerer);
            laenge = Math.Max(laenge, d);
            e.Laenge = laenge; e.Direkt = d;

            // Intensitaeten je Watt Quellleistung (W = 1)
            // 1. geometrisch durch die Oeffnungen: Kugelwelle ueber die Weglaenge, offener Anteil, Kantenbeugung
            double geoRef = 1.0 / (4 * Math.PI * laenge * laenge), geoHoch = geoRef, tuer = 1.0;
            for (int k = 0; k < n; k++)
            {
                V3 q = k == 0 ? quelle : punkte[k - 1], r = k == n - 1 ? hoerer : punkte[k + 1];
                double um = Umweg(p[k], q, r, punkte[k]);
                double ilR = KurzeAnderson(Fresnel(um, fRef)), ilH = KurzeAnderson(Fresnel(um, fHoch));
                if (beugungRef != null) beugungRef[k] = ilR;
                if (umwegLog != null) umwegLog[k] = um;
                double o = Clamp01(p[k].Offen);
                geoRef *= o * Math.Pow(10, -ilR / 10);
                geoHoch *= o * Math.Pow(10, -ilH / 10);
                tuer *= o + (1 - o) * Clamp01(p[k].Tau);
            }

            // 2. diffus ueber die Raeume (frequenzunabhaengig: alpha und tau flach, Tiefpass der Tueren separat).
            //    Leistung durch Oeffnung k: P = I * S * t. Innenraum: auf die naechste Oeffnung trifft P/A, am Hoerer 4P/A
            //    (Kuttruff, Diffusfeld); im Freien strahlt die Oeffnung in den Halbraum: P/(2 pi r^2).
            double diffus = 0;
            if (n > 0)
            {
                double r1 = Math.Max(V3.Abstand(quelle, punkte[0]), 0.5);
                double inzident = 1.0 / (4 * Math.PI * r1 * r1);        // Hall des Quellraums zaehlt nicht (macht das Spiel)
                for (int k = 0; k < n; k++)
                {
                    double o = Clamp01(p[k].Offen);
                    double leistung = inzident * p[k].Flaeche * (o + (1 - o) * Clamp01(p[k].Tau));
                    double a = raumA[k + 1];
                    bool letzte = k == n - 1;
                    if (a > 0) inzident = (letzte ? 4.0 : 1.0) * leistung / Math.Max(a, MinFlaecheA);
                    else
                    {
                        double r = Math.Max(V3.Abstand(punkte[k], letzte ? hoerer : punkte[k + 1]), 0.5);
                        inzident = leistung / (2 * Math.PI * r * r);
                    }
                }
                diffus = inzident;
            }
            e.Gueltig = true;
            double bezug = 1.0 / (4 * Math.PI * d * d);
            e.DbGeo = Db(bezug, geoRef, maxDb);
            e.DbDiffus = Db(bezug, diffus, maxDb);
            e.Db = Db(bezug, geoRef + diffus, maxDb);
            e.DbHoch = Db(bezug, geoHoch + diffus, maxDb);
            e.DbTuer = Math.Min(maxDb, -10 * Math.Log10(Math.Max(tuer, 1e-12)));
            return e;
        }

        static double Db(double bezug, double wert, double maxDb)
        {
            if (wert <= 0) return maxDb;
            return Math.Max(0, Math.Min(maxDb, 10 * Math.Log10(bezug / wert)));
        }

        /// <summary>Grenzfrequenz eines 2-poligen Tiefpasses (|H|^2 = 1/(1+(f/fc)^4)), der bei fHoch genau die Mehrdaempfung
        /// mehrDb hat. Keine Mehrdaempfung -> 22 kHz.</summary>
        public static double Tiefpass(double mehrDb, double fHoch, double fMin = 200, double fMax = 22000)
        {
            if (mehrDb <= 0.05) return fMax;
            double fc = fHoch / Math.Pow(Math.Pow(10, mehrDb / 10) - 1, 0.25);
            return Math.Max(fMin, Math.Min(fMax, fc));
        }

        /// <summary>Aequivalente Absorptionsflaeche eines Innenraums (m2): Oberflaeche * alpha + offene Portalflaechen.</summary>
        public static double Absorption(double oberflaeche, double alpha, double offeneFlaeche)
            => Math.Max(MinFlaecheA, oberflaeche * Clamp01(alpha) + Math.Max(0, offeneFlaeche));

        static double Clamp01(double x) => x < 0 ? 0 : (x > 1 ? 1 : x);
    }
}
