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
        public double DbMitte;             // 0.8.5: Daemmung bei 1 kHz (Baender wie Stufe 1: Bezug / 1 kHz / 4 kHz)
        public double DbTuer;              // Anteil der geschlossenen Tueren/Fenster (Massegesetz-Tiefpass)
        public double DbGeo, DbDiffus;     // die beiden Wege einzeln (Bezugsfrequenz)
        public double DbDiffusHoch;        // 0.8.5: Raeume-Weg bei der hohen Frequenz (Log)
        public double Laenge, Direkt;      // Weglaenge ueber die Portale, Direktlinie
    }

    internal static class RaumwegKern
    {
        public const double Schall = 343.0;
        public const double KanteMaxDb = 20.0;
        public const double MinFlaecheA = 1.0;
        public const double FMitte = 1000.0;

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
            double fRef, double fHoch, double maxDb, double[] beugungRef = null, double[] umwegLog = null, double[] raumV = null)
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
            double geoRef = 1.0 / (4 * Math.PI * laenge * laenge), geoHoch = geoRef, geoMitte = geoRef, tuer = 1.0;
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
                geoMitte *= o * Math.Pow(10, -KurzeAnderson(Fresnel(um, FMitte)) / 10);
                tuer *= o + (1 - o) * Clamp01(p[k].Tau);
            }

            // 2. diffus ueber die Raeume. 0.8.5 (Meldung Interchange 08.11.2026, Gegner Etagen hoeher klang "direkt neben mir"):
            //    a) Quelle -> erste Oeffnung: Leistung = W * Omega / 4 pi (Raumwinkel des Rechtecks, Kuttruff, Room Acoustics,
            //       Punktquelle vor einer Oeffnung). Vorher I * S = W * S / (4 pi r^2) ohne Schraegeinfall und ohne Grenze - bei r = 2 m und 80 m2
            //       ging mehr Leistung durch die Oeffnung, als die Quelle abgibt (1,6 W von 1 W).
            //    b) A je Raum und Band: A = Sum S alpha + alle offenen Oeffnungen des Raums (Aufrufer) + 4 m V (Luftabsorption,
            //       ISO 9613-1 bei 20 C / 50 %, Kuttruff Gl. Sabine mit 4mV; EN 12354-6 Gl. 4). Ohne V: 4mV = 0.
            //    c) Abstand im Raum: Nachhallenergie faellt mit dem Abstand vom Eintritt, E ~ 4/A * exp(-0,04 r / T)
            //       (Barron & Lee 1988, "revised theory", JASA 84; T = 0,161 V / A nach Sabine). Ohne V: kein Abfall.
            //    Innenraum: auf die naechste Oeffnung trifft P/A (Diffusfeld), am Hoerer 4P/A; im Freien Halbraum P/(2 pi r^2)
            //    mit Luftdaempfung. Je Band (Bezug / 1 kHz / 4 kHz): der Weg ueber viele Raeume wird so auch dumpfer.
            double d0 = 0, d1 = 0, d2 = 0;
            if (n > 0)
            {
                Luft(fRef, out double m0, out double l0); Luft(FMitte, out double m1, out double l1); Luft(fHoch, out double m2, out double l2);
                double t0 = Clamp01(p[0].Offen) + (1 - Clamp01(p[0].Offen)) * Clamp01(p[0].Tau);
                double startP = Raumwinkel(p[0], quelle) / (4 * Math.PI) * t0;
                d0 = Kette(startP, p, punkte, n, hoerer, raumA, raumV, m0, l0);
                d1 = Kette(startP, p, punkte, n, hoerer, raumA, raumV, m1, l1);
                d2 = Kette(startP, p, punkte, n, hoerer, raumA, raumV, m2, l2);
            }
            e.Gueltig = true;
            double bezug = 1.0 / (4 * Math.PI * d * d);
            e.DbGeo = Db(bezug, geoRef, maxDb);
            e.DbDiffus = Db(bezug, d0, maxDb);
            e.DbDiffusHoch = Db(bezug, d2, maxDb);
            e.Db = Db(bezug, geoRef + d0, maxDb);
            e.DbHoch = Db(bezug, geoHoch + d2, maxDb);
            e.DbMitte = Db(bezug, geoMitte + d1, maxDb);
            e.DbTuer = Math.Min(maxDb, -10 * Math.Log10(Math.Max(tuer, 1e-12)));
            return e;
        }

        /// <summary>Diffuse Kette ab der Leistung startP hinter Oeffnung 0 (W = 1). m = Luft-Energiedaempfung (1/m),
        /// luftDb = dB/m (im Freien). Ergebnis: Intensitaet am Hoerer (Freifeld-Aequivalent).</summary>
        static double Kette(double startP, RaumPortal[] p, V3[] punkte, int n, V3 hoerer, double[] raumA, double[] raumV, double m, double luftDb)
        {
            double leistung = startP, inzident = 0;
            for (int k = 0; k < n; k++)
            {
                if (k > 0)
                {
                    double o = Clamp01(p[k].Offen);
                    leistung = inzident * p[k].Flaeche * (o + (1 - o) * Clamp01(p[k].Tau));
                }
                double a = raumA[k + 1];
                bool letzte = k == n - 1;
                double r = Math.Max(V3.Abstand(punkte[k], letzte ? hoerer : punkte[k + 1]), 0.5);
                if (a > 0)
                {
                    double v = raumV != null && raumV.Length > k + 1 ? Math.Max(0, raumV[k + 1]) : 0;
                    double aF = Math.Max(a + 4 * m * v, MinFlaecheA);
                    double abfall = v > 0 ? Math.Exp(-0.04 * r / Math.Max(0.161 * v / aF, 0.05)) : 1.0;
                    inzident = (letzte ? 4.0 : 1.0) * leistung / aF * abfall;
                }
                else inzident = leistung / (2 * Math.PI * r * r) * Math.Pow(10, -luftDb * r / 10);
            }
            return inzident;
        }

        /// <summary>Raumwinkel (sr) des Portal-Rechtecks von Punkt q aus (exakt, 0..2 pi).</summary>
        public static double Raumwinkel(in RaumPortal p, V3 q)
        {
            V3 rel = q - p.Mitte;
            double u = V3.Dot(rel, p.Rechts), v = V3.Dot(rel, p.Oben), w = Math.Max(Math.Abs(V3.Dot(rel, p.Normale)), 1e-3);
            double G(double x, double y) => Math.Atan(x * y / (w * Math.Sqrt(x * x + y * y + w * w)));
            double x1 = -p.HalbX - u, x2 = p.HalbX - u, y1 = -p.HalbY - v, y2 = p.HalbY - v;
            return Math.Max(0, Math.Min(2 * Math.PI, G(x2, y2) - G(x1, y2) - G(x2, y1) + G(x1, y1)));
        }

        // Luftabsorption ISO 9613-1 bei 20 C, 50 % r. F., 101,325 kPa (Raeume: ohne Wetter). m = alpha / (10 lg e).
        static void Luft(double f, out double m, out double db)
        {
            // 0.8.5 Leistung: fester Zustand (20 C / 50 %) -> je Frequenz einmal (Iso9613.Alpha: ~10 Exp/Pow, bisher 3x je Route)
            if (LuftCache)
                for (int i = 0; i < luftN; i++)
                    if (luftF[i] == f) { m = luftM[i]; db = luftDb[i]; return; }
            db = Iso9613.Alpha(f, 20, Iso9613.Bezugsdruck, 50);
            m = db / 4.3429;
            if (LuftCache) { int i = luftN < luftF.Length ? luftN++ : (luftNext++ % luftF.Length); luftF[i] = f; luftM[i] = m; luftDb[i] = db; }
        }

        /// <summary>0.8.5: Luftabsorption je Frequenz merken (false = jedes Mal rechnen, Referenz im Benchmark).</summary>
        internal static bool LuftCache = true;
        static readonly double[] luftF = new double[8], luftM = new double[8], luftDb = new double[8];
        static int luftN, luftNext;

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

    // ------------------------------------------------------------------------------------------
    // 0.8.5 - Mehrere Routen statt "die lauteste gewinnt" (Rueckmeldung eines WTT-Entwicklers zur Mehrwege-Demo: an Uebergaengen
    // springt es, wenn die Auswahl hart den Gewinner wechselt oder mehrere Wege gleichzeitig tauschen). Logik wie die Demo
    // (mpGewichte/mpHalten/mpSummen):
    //  - Jede Route hat eine stabile ID aus ihrer Portalfolge (und dem Raumpaar).
    //  - Weiches Rang-Gewicht: Routen nach Pegel sortiert, Bezug r = Mitte zwischen der N-ten und der (N+1)-ten Route,
    //    w = smoothstep(0,5 + (L - r) / Breite); dazu weich raus, was mehr als 46 dB unter der lautesten liegt. Bei hoechstens
    //    N Routen zaehlen alle voll. Breite 0 = harte Top-N-Auswahl.
    //  - Zeitliches Halten: das Gewicht laeuft linear in 'Blendzeit' auf sein Ziel (neu: von 0 herauf; verschwunden: mit dem
    //    letzten Beitrag auf 0 herunter). Springt eine Route mit gleicher ID um mehr als 3 dB in einer Rechnung, wird der alte
    //    Stand ein "Geist", der ausblendet, und der neue blendet ein (in Energie = lineare Ueberblendung).
    //  - Summe: E_b = Sum w * 10^(-Daemmung_b / 10), je Band (Bezug / 1 kHz / 4 kHz). Erste Rechnung eines Rechners: Gewichte
    //    sofort am Ziel (ein neuer Klang blendet nicht ein).
    // Reine Rechnung (Offline-Test), Spiel-Teil: Raumweg.cs.
    // ------------------------------------------------------------------------------------------

    internal sealed class RoutenEintrag
    {
        public ulong Id;
        public bool Geist, Da;
        public double W, Ziel;              // Blendgewicht (aktuell) und Rang-Gewicht (Ziel)
        public int Rang;                     // 1 = lauteste der letzten Rechnung, 0 = nicht mehr geliefert
        public readonly double[] Db = new double[3];   // Daemmung je Band (>= 0)
        public V3 Austritt;                  // letzter Portalpunkt vor dem Hoerer
        public RaumwegErgebnis Erg;
        public int N;
        public readonly int[] Portale = new int[16];
        public double Energie0 => Math.Pow(10, -Db[0] / 10);
    }

    internal sealed class RoutenMischer
    {
        struct Kand
        {
            public ulong Id; public double D0, D1, D2; public V3 Aus; public RaumwegErgebnis Erg; public int Start, N; public double Ziel; public int Rang;
        }

        public const double GeistDb = 3.0;
        public const double TiefCut = 46.0, TiefBreite = 6.0;
        public readonly System.Collections.Generic.List<RoutenEintrag> Eintraege = new System.Collections.Generic.List<RoutenEintrag>();
        readonly System.Collections.Generic.Stack<RoutenEintrag> frei = new System.Collections.Generic.Stack<RoutenEintrag>();
        Kand[] kand = new Kand[32];
        int[] kandPortale = new int[256];
        int kandN, kandP;
        readonly System.Collections.Generic.HashSet<ulong> ids = new System.Collections.Generic.HashSet<ulong>();
        double tLetzt = double.NaN;
        bool frisch = true;
        // Ergebnis der letzten Rechnung (Diagnose/Log)
        public double GewinnerDb = double.NaN;     // lauteste Route der letzten Rechnung (alte Auswahl)
        public V3 GewinnerAustritt;
        public int Geister, Neue;                   // seit dem letzten Abholen
        public bool Laeuft;                         // mindestens ein Gewicht ist noch nicht am Ziel
        public bool Hat => Eintraege.Count > 0;

        public void Leeren()
        {
            foreach (var e in Eintraege) frei.Push(e);
            Eintraege.Clear(); kandN = kandP = 0; frisch = true; tLetzt = double.NaN; Laeuft = false; GewinnerDb = double.NaN;
        }

        public void Beginn() { kandN = 0; kandP = 0; ids.Clear(); }

        /// <summary>Eine gerechnete Route melden (Daemmung je Band >= 0). Doppelte IDs zaehlen einmal (die erste).</summary>
        public void Kandidat(ulong id, double db0, double db1, double db2, V3 austritt, in RaumwegErgebnis erg, int[] portale, int n)
            => Kandidat(id, db0, db1, db2, austritt, erg, portale, n, 0);

        /// <summary>Wie oben, Portalfolge ab portale[start] (0.8.5: gespeicherte Kandidaten aus RaumwegNetz).</summary>
        public void Kandidat(ulong id, double db0, double db1, double db2, V3 austritt, in RaumwegErgebnis erg, int[] portale, int n, int start)
        {
            if (!ids.Add(id)) return;
            if (kandN == kand.Length) Array.Resize(ref kand, kand.Length * 2);
            n = Math.Min(n, 16);
            if (kandP + n > kandPortale.Length) Array.Resize(ref kandPortale, Math.Max(kandPortale.Length * 2, kandP + n));
            if (portale != null) Array.Copy(portale, start, kandPortale, kandP, n);
            kand[kandN++] = new Kand { Id = id, D0 = db0, D1 = db1, D2 = db2, Aus = austritt, Erg = erg, Start = kandP, N = n };
            kandP += n;
        }

        static double Glatt(double x) => x <= 0 ? 0 : x >= 1 ? 1 : x * x * (3 - 2 * x);

        /// <summary>Weiche Rang-Gewichte der gemeldeten Routen (Demo mpGewichte). Pegel L = -Daemmung.</summary>
        void Gewichte(int rangN, double breiteDb)
        {
            // nach Pegel absteigend (= Daemmung aufsteigend); wenige Routen -> Einfuegesortierung reicht
            for (int i = 1; i < kandN; i++)
            {
                var k = kand[i]; int j = i - 1;
                while (j >= 0 && kand[j].D0 > k.D0) { kand[j + 1] = kand[j]; j--; }
                kand[j + 1] = k;
            }
            if (kandN == 0) return;
            double lmax = -kand[0].D0;
            double r = kandN > rangN && rangN > 0 ? (-kand[rangN - 1].D0 - kand[rangN].D0) / 2 : double.NegativeInfinity;
            for (int i = 0; i < kandN; i++)
            {
                double l = -kand[i].D0, w;
                if (breiteDb <= 0) w = rangN <= 0 || i < rangN ? 1 : 0;
                else w = double.IsNegativeInfinity(r) ? 1 : Glatt(0.5 + (l - r) / breiteDb);
                if (breiteDb > 0) w *= Glatt((l - (lmax - TiefCut)) / TiefBreite);
                kand[i].Ziel = w; kand[i].Rang = i + 1;
            }
        }

        RoutenEintrag Finde(ulong id)
        {
            for (int i = 0; i < Eintraege.Count; i++) { var e = Eintraege[i]; if (!e.Geist && e.Id == id) return e; }
            return null;
        }

        /// <summary>Nach einer Rechnung: Gewichte, Geister, neue Routen; danach bis 'jetzt' fortschreiben.</summary>
        public void Uebernehmen(double jetzt, double blendS, int rangN, double breiteDb)
        {
            // laufende Blenden bis jetzt fertigschreiben; die neuen Ziele blenden erst ab jetzt (auch nach langer Pause ohne Rechnung)
            if (!frisch) Fortschreiben(jetzt, blendS);
            Gewichte(rangN, breiteDb);
            GewinnerDb = kandN > 0 ? kand[0].D0 : double.NaN;
            if (kandN > 0) GewinnerAustritt = kand[0].Aus;
            for (int i = 0; i < Eintraege.Count; i++) Eintraege[i].Da = false;
            bool start = frisch || blendS <= 0;
            for (int i = 0; i < kandN; i++)
            {
                ref Kand k = ref kand[i];
                var e = Finde(k.Id);
                if (e != null && !start && Math.Abs(e.Db[0] - k.D0) > GeistDb)
                {
                    e.Geist = true; e.Ziel = 0; e.Rang = 0; Geister++;
                    e = null;
                }
                if (e == null)
                {
                    if (k.Ziel <= 1e-4) continue;
                    e = frei.Count > 0 ? frei.Pop() : new RoutenEintrag();
                    e.Id = k.Id; e.Geist = false; e.W = start ? k.Ziel : 0;
                    Eintraege.Add(e);
                    if (!start) Neue++;
                }
                e.Da = true; e.Ziel = k.Ziel; e.Rang = k.Rang;
                e.Db[0] = k.D0; e.Db[1] = k.D1; e.Db[2] = k.D2; e.Austritt = k.Aus; e.Erg = k.Erg;
                e.N = k.N; Array.Copy(kandPortale, k.Start, e.Portale, 0, k.N);
            }
            for (int i = 0; i < Eintraege.Count; i++) { var e = Eintraege[i]; if (!e.Da) { e.Ziel = 0; e.Rang = 0; } }
            tLetzt = jetzt;
            frisch = false;
            Fortschreiben(jetzt, blendS);     // dt = 0: nur Aufraeumen und Laeuft bestimmen (bei Blendzeit 0: sofort am Ziel)
        }

        /// <summary>Gewichte linear in blendS Sekunden aufs Ziel fuehren (jedes Bild). Ausgeblendete Eintraege fallen weg.</summary>
        public void Fortschreiben(double jetzt, double blendS)
        {
            double dt = double.IsNaN(tLetzt) ? 0 : Math.Max(0, jetzt - tLetzt);
            tLetzt = jetzt;
            double schritt = blendS > 0 ? dt / blendS : double.PositiveInfinity;
            bool laeuft = false;
            for (int i = Eintraege.Count - 1; i >= 0; i--)
            {
                var e = Eintraege[i];
                e.W = e.W < e.Ziel ? Math.Min(e.Ziel, e.W + schritt) : Math.Max(e.Ziel, e.W - schritt);
                if (e.Ziel <= 1e-9 && e.W <= 1e-4) { Eintraege.RemoveAt(i); frei.Push(e); continue; }
                if (Math.Abs(e.W - e.Ziel) > 1e-4) laeuft = true;
            }
            Laeuft = laeuft;
        }

        /// <summary>Energiesumme je Band als Daemmung (>= 0, hoechstens maxDb). richtung = energiegewichteter Mittelwert der
        /// Austrittspunkte (Bezugsband). false = keine Route traegt bei.</summary>
        public bool Summe(double maxDb, double[] db, out V3 austritt, out double gewicht)
        {
            double e0 = 0, e1 = 0, e2 = 0, sw = 0; V3 a = default;
            foreach (var e in Eintraege)
            {
                if (e.W <= 0) continue;
                double x0 = e.W * Math.Pow(10, -e.Db[0] / 10);
                e0 += x0; e1 += e.W * Math.Pow(10, -e.Db[1] / 10); e2 += e.W * Math.Pow(10, -e.Db[2] / 10);
                a = a + e.Austritt * x0; sw += x0;
            }
            gewicht = sw;
            austritt = sw > 0 ? a * (1.0 / sw) : default;
            db[0] = DbAus(e0, maxDb); db[1] = DbAus(e1, maxDb); db[2] = DbAus(e2, maxDb);
            return e0 > 0;
        }

        static double DbAus(double e, double maxDb) => e <= 0 ? maxDb : Math.Max(0, Math.Min(maxDb, -10 * Math.Log10(e)));

        /// <summary>Winkel (Grad) zwischen den Richtungen Hoerer -> a und Hoerer -> b.</summary>
        public static double Winkel(V3 h, V3 a, V3 b)
        {
            V3 u = a - h, v = b - h;
            double lu = u.Laenge, lv = v.Laenge;
            if (lu < 1e-6 || lv < 1e-6) return 0;
            double c = Math.Max(-1, Math.Min(1, V3.Dot(u, v) / (lu * lv)));
            return Math.Acos(c) * 180.0 / Math.PI;
        }

        /// <summary>Stabile Routen-ID: FNV-1a (64 bit) ueber Raumpaar und Portalfolge (Job-Richtung).</summary>
        public static ulong Id(uint paar, int[] portale, int n)
        {
            ulong h = 14695981039346656037UL;
            unchecked
            {
                h = (h ^ paar) * 1099511628211UL;
                for (int i = 0; i < n; i++) h = (h ^ (uint)portale[i]) * 1099511628211UL;
                h = (h ^ (uint)n) * 1099511628211UL;
            }
            return h;
        }
    }

    // ------------------------------------------------------------------------------------------
    // 0.8.5 - Hoerbarkeit leiser Quellen (Meldung Interchange 08.11.2026: "in der Realitaet wuerde man ihn da oben nicht laufen
    // und atmen hoeren"). Der Lautheitsfaktor (Physik-Modus, Standard 0,2, bei Kaan 0,5) staucht JEDE Daemmung: 60 dB -> 30 dB.
    // Begruendung des Faktors: ein Schuss hat real ~150-160 dB, Kopfhoerer spielen ~90 dB - der echte Pegelumfang passt nicht.
    // Fuer einen Schuss stimmt das. Fuer Atem, Schritte, Ausruestung, Stimme ist es umgekehrt: sie sind real leise (60-72 dB(A)
    // in 1 m) und liegen hinter wenigen dB Daemmung schon unter dem Grundgeraeusch. Die Stauchung macht sie hoerbar, obwohl es
    // physikalisch nichts zu hoeren gibt.
    // Loesung: gestaucht wird nur der Teil der Daemmung, bis zu dem die Quelle real noch ueber dem Grundgeraeusch liegt
    // ("Knie"); jedes dB darueber zaehlt voll, und ueber 10 dB unter dem Grundgeraeusch (Mithoerschwelle) blendet sie ganz aus.
    //   Knie = L_Quelle(1 m) - 20 lg(d) - L_Grund (>= 0)
    //   D_Spiel = Lautheit * min(D, Knie) + max(0, D - Knie) + Max. insulation * smoothstep((D - Knie) / 10 dB)
    // Lautes (Schuesse, Explosionen, Einschlaege) hat kein Knie (Tabelle NaN) und bleibt unveraendert gestaucht.
    // Quellpegel (Schaetzwerte, dB(A) in 1 m): Stimme 72 = "laut" nach ISO 9921:2003 Tab. 1 (normal 60, angehoben 66, laut 72,
    // sehr laut 78; Bots rufen meist); Schritte/Ausruestung 65 (Gehen mit Stiefeln auf hartem Boden ~55-70 dB(A), Messwerte der
    // Trittschall-Literatur); Waffenhandhabung 70, Zusammenstoesse 75, Tueren/Schalter 70 (Annahmen). Grundgeraeusch 30 dB(A) =
    // ruhiger Innenraum (Einstellung; Wohnraum nachts 25-30, ruhiges Buero 35-40 dB(A)).
    // ------------------------------------------------------------------------------------------
    internal static class Hoerbarkeit
    {
        // Reihenfolge BetterAudio.AudioSourceGroupType: Gunshots, Weaponry, Impacts, Character, Environment, Collisions, Speech,
        // Distant, NonspatialBypass, Nonspatial, Voip, Grenades, Windows, VehicleInSpeech, Lamp, InteractiveObjects,
        // OutEnvironment, PropagatedExclusive, LightOcclusion
        public static readonly double[] QuellPegel =
        {
            double.NaN, 70, double.NaN, 65, double.NaN, 75, 72,
            double.NaN, double.NaN, double.NaN, double.NaN, double.NaN, double.NaN, double.NaN, double.NaN, 70,
            double.NaN, double.NaN, double.NaN,
        };

        /// <summary>Knie (dB) fuer Gruppe 'typ' in Abstand d bei Grundgeraeusch lGrund; NaN = kein Knie (alles gestaucht).</summary>
        public static double Knie(int typ, double d, double lGrund)
        {
            if (typ < 0 || typ >= QuellPegel.Length || double.IsNaN(QuellPegel[typ])) return double.NaN;
            return Math.Max(0, QuellPegel[typ] - 20 * Math.Log10(Math.Max(d, 1.0)) - lGrund);
        }

        /// <summary>Breite (dB) unter dem Grundgeraeusch, ueber die eine Quelle ganz verdeckt wird: ein Signal 10 dB unter einem
        /// breitbandigen Verdecker ist nicht mehr wahrnehmbar (Mithoerschwelle, Zwicker/Fastl "Psychoacoustics" Kap. 4; Sprache
        /// ab etwa -10 bis -15 dB Stoerabstand unverstaendlich/unhoerbar).</summary>
        public const double MaskBreite = 10.0;

        /// <summary>Daemmung, die das Spiel anwendet: gestaucht bis zum Knie, darueber voll und ueber MaskBreite zusaetzlich bis
        /// 'stumm' (maxDb) ausgeblendet - im Spiel gibt es kein Grundgeraeusch, das die Quelle verdecken wuerde.</summary>
        public static double Daempfung(double d, double lautheit, double knie, double maxDb = 60)
        {
            if (d <= 0) return 0;
            if (double.IsNaN(knie)) return d * lautheit;
            double ueber = Math.Max(0, d - knie);
            double x = Math.Min(1, ueber / MaskBreite), glatt = x * x * (3 - 2 * x);
            return lautheit * Math.Min(d, knie) + ueber + maxDb * glatt;
        }

        // ------------------------------------------------------------------------------------------
        // 0.8.5 - Hoerbarkeit JE BAND (Einwand Kaan 08.11.2026: "Wenn die hoerbar waeren, soll man's hoeren - auch wenn's nur das
        // dumpfe Tiefe ist"). Bisher wurde EIN Pegel (dB(A)) gegen EIN Grundgeraeusch gestellt, und zwar mit der Daemmung des
        // Bezugsbands. Wand und Grundgeraeusch sind aber frequenzabhaengig: das Massegesetz daemmt Tiefen am wenigsten, ein ruhiger
        // Raum ist dafuer tief am lautesten. Jetzt je Band 250 Hz / 1 kHz / 4 kHz (wie Stufe 1):
        //   L_b(d)  = Quellpegel_b - 20 lg d                          (Quellpegel_b aus dB(A) und Spektrum der Klanggruppe)
        //   N_b     = Grundgeraeusch je Band (NR-Kurve, auf den eingestellten dB(A)-Wert gelegt), mind. Hoerschwelle + 10 dB
        //   Knie_b  = L_b(d) - N_b,  ueber_b = Knie_b + 10 - D_b     (> 0 = Band liegt ueber der Mithoerschwelle)
        //   G_b     = Lautheit * D_0 + Neigung_b + Zusatz_b           (Zusatz_b = Daempfung(D_b, Knie_b) - Lautheit * D_b)
        // Lautstaerke = lautestes Band (min G_b), Tiefpass = Fit an G_b - G_0. Die Energiesumme der hoerbaren Baender entsteht dann im
        // Klang selbst: der Filter nimmt die Baender weg, die Lautstaerke haelt das lauteste. (Eine Energiesumme in der Lautstaerke
        // UND der Tiefpass wuerden die Hoehen doppelt abziehen.) Ohne Zusatz in allen Baendern = exakt das Verhalten davor.
        // Stumm erst, wenn alle Baender 10 dB unter ihrem Grundgeraeusch liegen. Alles ist stetig in D, Abstand und Grundgeraeusch
        // (smoothstep ueber die 10 dB Mithoerbreite), es gibt keine Schwelle, die flackern koennte - Hysterese ist nicht noetig.
        // ------------------------------------------------------------------------------------------

        public const int NB = 3;
        /// <summary>A-Bewertung bei 250 Hz / 1 kHz / 4 kHz (IEC 61672-1:2013, Tab. E.1).</summary>
        public static readonly double[] ABew = { -8.6, 0.0, 1.0 };

        /// <summary>Spektrum je Klanggruppe: Oktavpegel 250 Hz / 1 kHz / 4 kHz relativ (dB). SCHAETZWERTE, keine Messung:
        /// Character (Schritte, Ausruestung, Atem) tieflastig - Fersenaufprall auf hartem Boden hat sein Maximum unter 500 Hz, darueber
        /// faellt es ab (Verlauf wie Trittschall-Spektren, EN ISO 10140-3 Normhammerwerk faellt nach oben, Schritte steiler);
        /// Stimme mittig - Verlauf des Langzeit-Sprachspektrums bei angehobener/lauter Stimme (ANSI S3.5-1997 Tab. 3: Maximum
        /// 500 Hz-1 kHz, 4 kHz rund 10 dB darunter), gerundet; Waffenhandhabung (Metallklicken) hochlastig; Zusammenstoesse
        /// breitbandig; Tueren/Schalter mittig.</summary>
        static readonly double[][] Spektrum =
        {
            null, new[] { -10.0, -3.0, 0.0 }, null, new[] { 0.0, -4.0, -10.0 }, null, new[] { 0.0, -2.0, -6.0 }, new[] { -2.0, 0.0, -9.0 },
            null, null, null, null, null, null, null, null, new[] { -3.0, 0.0, -4.0 },
            null, null, null,
        };

        /// <summary>Quellpegel (dB, unbewertet) im Band b in 1 m: Spektrum so verschoben, dass die A-bewertete Summe der drei
        /// Baender den dB(A)-Wert der Tabelle QuellPegel ergibt. NaN = laute Gruppe ohne Hoerbarkeitsrechnung.</summary>
        public static double BandPegel(int typ, int b)
        {
            if (typ < 0 || typ >= QuellPegel.Length || double.IsNaN(QuellPegel[typ]) || Spektrum[typ] == null) return double.NaN;
            var s = Spektrum[typ];
            double e = 0;
            for (int i = 0; i < NB; i++) e += Math.Pow(10, (s[i] + ABew[i]) / 10);
            return QuellPegel[typ] + s[b] - 10 * Math.Log10(e);
        }

        // NR-Kurven (ISO R1996-1971 / BS 8233: L = a + b * NR) fuer 63 Hz ... 8 kHz - Standardspektrum eines Innenraums, tief laut,
        // hoch leise. Die Einstellung "background noise (dB)" bleibt ein dB(A)-Gesamtpegel: gesucht wird das NR, dessen A-bewertete
        // Summe ueber alle 8 Oktaven ihn ergibt (30 dB(A) -> NR 19,8: 250 Hz 30,4 / 1 kHz 19,8 / 4 kHz 14,2 dB).
        static readonly double[] NrA = { 35.5, 22.0, 12.0, 4.8, 0.0, -3.5, -6.1, -8.0 };
        static readonly double[] NrB = { 0.790, 0.870, 0.930, 0.974, 1.000, 1.015, 1.025, 1.030 };
        static readonly double[] NrABew = { -26.2, -16.1, -8.6, -3.2, 0.0, 1.2, 1.0, -1.1 };
        static readonly int[] NrBand = { 2, 4, 6 };   // 250 Hz / 1 kHz / 4 kHz in der Oktavreihe
        /// <summary>Hoerschwelle im freien Schallfeld (ISO 389-7:2005) bei 250 Hz / 1 kHz / 4 kHz.</summary>
        public static readonly double[] Hoerschwelle = { 11.4, 2.4, -5.4 };

        static double NrDbA(double nr)
        {
            double e = 0;
            for (int i = 0; i < 8; i++) e += Math.Pow(10, (NrA[i] + NrB[i] * nr + NrABew[i]) / 10);
            return 10 * Math.Log10(e);
        }

        static double grundFuer = double.NaN;
        static readonly double[] grundBand = new double[NB];

        /// <summary>Grundgeraeusch je Band (dB) fuer den dB(A)-Gesamtpegel lGrund, mindestens Hoerschwelle + MaskBreite (dann ist die
        /// Mithoerschwelle die Hoerschwelle selbst). Ergebnis-Array wird wiederverwendet.</summary>
        public static double[] Grund(double lGrund)
        {
            if (lGrund == grundFuer) return grundBand;
            double lo = -40, hi = 100;
            for (int i = 0; i < 50; i++) { double m = (lo + hi) / 2; if (NrDbA(m) < lGrund) lo = m; else hi = m; }
            double nr = (lo + hi) / 2;
            for (int b = 0; b < NB; b++)
            {
                int o = NrBand[b];
                grundBand[b] = Math.Max(NrA[o] + NrB[o] * nr, Hoerschwelle[b] + MaskBreite);
            }
            grundFuer = lGrund;
            return grundBand;
        }

        /// <summary>Ergebnis der Bandrechnung (wiederverwendbar, Hauptthread).</summary>
        public sealed class Baender
        {
            public bool Gilt;                                   // Gruppe hat eine Hoerbarkeitsrechnung
            public readonly double[] D = new double[NB];        // reale Daemmung je Band (dB)
            public readonly double[] Knie = new double[NB];     // Pegel ueber dem Grundgeraeusch ohne Wand (dB, >= 0)
            public readonly double[] Ueber = new double[NB];    // Pegel hinter der Wand ueber der Mithoerschwelle (dB, > 0 = hoerbar)
            public readonly double[] Zusatz = new double[NB];   // Daempfung ueber die reine Stauchung hinaus (dB, >= 0)
            public readonly double[] G = new double[NB];        // Spiel-Daempfung je Band (dB)
            public double Lautstaerke;                          // dB, die das Spiel als Lautstaerke anwendet (lautestes Band)
            public double ZusatzMax;                            // groesster Zusatz der Hochbaender (fuer die Tiefpass-Untergrenze)
            public bool Hoerbar(int b) => Ueber[b] > 0;
            public bool Stumm => Ueber[0] <= 0 && Ueber[1] <= 0 && Ueber[2] <= 0;
        }

        /// <summary>Tiefste Grenzfrequenz, wenn nur noch das tiefe Band hoerbar ist: bei 300 Hz liegt 1 kHz 21 dB, 4 kHz 45 dB unter
        /// 250 Hz (2-poliger Tiefpass) - dumpfes Wummern. Gewaehlt, keine Literatur.</summary>
        public const double TiefpassDumpf = 300.0;

        /// <summary>
        /// Hoerbarkeit je Band. d0 = reale Daemmung im Bezugsband (dB), m[b] = reale Mehrdaemmung des Bands b gegenueber dem
        /// Bezugsband, neigung[b] = Mehrdaempfung, die das Spiel ohne Hoerbarkeit ueber den Tiefpass anwendet (m[0] = neigung[0] = 0).
        /// false = Gruppe ohne Rechnung (laut) oder keine Daemmung: dann gilt die Stauchung allein.
        /// </summary>
        public static bool Rechnen(int typ, double abstand, double lGrund, double d0, double[] m, double[] neigung,
                                   double lautheit, double maxDb, Baender r)
        {
            r.Gilt = false;
            if (double.IsNaN(BandPegel(typ, 0))) return false;
            r.Gilt = true;
            var n = Grund(lGrund);
            double ab = 20 * Math.Log10(Math.Max(abstand, 1.0));
            d0 = Math.Max(0, d0);
            r.Lautstaerke = double.MaxValue; r.ZusatzMax = 0;
            for (int b = 0; b < NB; b++)
            {
                double db = Math.Max(0, d0 + (b == 0 ? 0 : m[b]));
                double frei = BandPegel(typ, b) - ab - n[b];
                double knie = Math.Max(0, frei);
                r.D[b] = db; r.Knie[b] = knie;
                r.Ueber[b] = frei + MaskBreite - db;
                r.Zusatz[b] = db > 0 ? Math.Max(0, Daempfung(db, lautheit, knie, maxDb) - lautheit * db) : 0;
                r.G[b] = lautheit * d0 + (b == 0 ? 0 : neigung[b]) + r.Zusatz[b];
                if (r.G[b] < r.Lautstaerke) r.Lautstaerke = r.G[b];
                if (b > 0 && r.Zusatz[b] > r.ZusatzMax) r.ZusatzMax = r.Zusatz[b];
            }
            return true;
        }

        /// <summary>Ein Band, das so weit unter dem lautesten liegt, ist im Klang weg; mehr Abstand muss der Tiefpass nicht herstellen.
        /// Ohne diese Grenze verlangt ein verdecktes 4-kHz-Band (G + 60 dB) vom 2-poligen Fit einen so tiefen Tiefpass, dass das
        /// noch gut hoerbare 1-kHz-Band mit wegfaellt. Gewaehlt, keine Literatur.</summary>
        public const double WegAbstand = 30.0;

        /// <summary>Tiefpass zu den hoerbaren Baendern: Fit an die Mehrdaempfung je Band gegenueber dem Bezugsband (G_b - G_0),
        /// hoechstens WegAbstand bzw. die Neigung ohne Hoerbarkeit, wenn die schon groesser ist.
        /// lpAlt = Tiefpass ohne Hoerbarkeit (exakt zurueck, solange kein Band einen Zusatz hat), tiefpassMin = "Low-pass at full
        /// insulation". Die Untergrenze sinkt stetig mit dem Zusatz des 1-kHz-Bands bis TiefpassDumpf (nur Tiefen hoerbar).</summary>
        public static double Tiefpass(Baender r, double lpAlt, double f1, double f2, double tiefpassMin)
        {
            if (!r.Gilt || (r.Zusatz[0] <= 1e-9 && r.ZusatzMax <= 1e-9)) return lpAlt;
            double ziel = Math.Min(tiefpassMin, TiefpassDumpf);
            double x = Math.Min(1.0, r.Zusatz[1] / WegAbstand);
            double fMin = Math.Min(lpAlt, tiefpassMin * Math.Pow(ziel / tiefpassMin, x));
            double t1 = Ziel(r, 1), t2 = Ziel(r, 2);
            // Ein Tiefpass daempft 4 kHz immer staerker als 1 kHz. Verlangen die Baender das Gegenteil (hochlastige Quelle, Tiefen
            // und Mitten schon verdeckt, 4 kHz noch hoerbar), wird 1 kHz nur so weit gesenkt wie 4 kHz - lieber ein hoerbares Band
            // behalten als es mit wegzufiltern (der Fit springt sonst zwischen den beiden unvereinbaren Zielen).
            // Gesenkt wird nur um den Teil, den die Hoerbarkeit (Zusatz 1 kHz > Zusatz 4 kHz) verursacht - ohne Zusatz bleibt es 0.8.4.
            t1 -= Math.Min(Math.Max(0, t1 - t2), Math.Max(0, r.Zusatz[1] - r.Zusatz[2]));
            return WegeKern.TiefpassFit(t1, t2, f1, f2, fMin);
        }

        static double Neigung(Baender r, int b) => r.G[b] - r.Zusatz[b] - (r.G[0] - r.Zusatz[0]);   // Neigung des Spiels ohne Hoerbarkeit
        static double Ziel(Baender r, int b) => Math.Min(r.G[b] - r.G[0], Math.Max(Neigung(r, b), WegAbstand));
    }
}
