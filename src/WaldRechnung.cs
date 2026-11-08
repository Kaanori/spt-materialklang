using System;

namespace Materialklang
{
    // ---------------------------------------------------------------------------------------------
    //  Laubdaempfung (Wald) - reine Rechnung ohne Spiel- oder Unity-Typen (laeuft auch im Offline-Test).
    //   - Tabelle nach ISO 9613-2:1996, Anhang A (Daempfung durch Laub), Wert je Oktavband.
    //   - Abbildung auf das Modell des Plugins: Pegelabsenkung (Mittenband) + Tiefpass (Spektralneigung).
    //   - Raster "Baeume je Zelle" und Wegverfolgung (DDA) durch das Raster.
    //   - Einstufung der Objektnamen (Baum / Strauch / nichts).
    // ---------------------------------------------------------------------------------------------

    /// <summary>ISO 9613-2:1996, Anhang A, Tabelle A.1 (Laub) und die Abbildung auf Pegel + Tiefpass.</summary>
    internal static class Laub
    {
        /// <summary>Oktavband-Mittenfrequenzen der Tabelle (Hz).</summary>
        public static readonly double[] Mitte = { 63, 125, 250, 500, 1000, 2000, 4000, 8000 };

        /// <summary>Tabelle A.1, Zeile "10 m &lt;= d_f &lt;= 20 m": Daempfung insgesamt in dB, je Band.</summary>
        public static readonly double[] Fest = { 0, 0, 1, 1, 1, 1, 2, 3 };

        /// <summary>Tabelle A.1, Zeile "20 m &lt;= d_f &lt;= 200 m": Daempfung in dB je Meter, je Band.</summary>
        public static readonly double[] JeMeter = { 0.02, 0.03, 0.04, 0.05, 0.06, 0.08, 0.09, 0.12 };

        /// <summary>Laengster Weg durch Laub, den die Norm abdeckt; laengere Wege zaehlen wie 200 m.</summary>
        public const double MaxLaenge = 200.0;

        /// <summary>Bezugsband fuer die Pegelabsenkung: 500 Hz (Index 3).</summary>
        public const int BezugsBand = 3;

        /// <summary>
        /// Daempfung in dB fuer Band b bei df Metern Weg durch Laub.
        ///  - ab 20 m: dB/m * df (aber nie weniger als der Festwert der 10-20-m-Zeile, damit die Kurve nicht zurueckfaellt)
        ///  - 10..20 m: Festwert der Tabelle
        ///  - unter 10 m: die Norm hat dafuer keine Zeile; hier linear von 0 bis zum Festwert bei 10 m (EIGENE ANNAHME, damit es keinen Sprung gibt)
        /// </summary>
        public static double Band(int b, double df)
        {
            if (!(df > 0.0)) return 0.0;
            if (df > MaxLaenge) df = MaxLaenge;
            if (df < 10.0) return Fest[b] * df / 10.0;
            if (df <= 20.0) return Fest[b];
            return Math.Max(Fest[b], JeMeter[b] * df);
        }

        /// <summary>Daempfung bei beliebiger Frequenz: linear in log2(f) zwischen den Bandwerten, ausserhalb der Tabelle konstant (nichts erfunden).</summary>
        public static double Bei(double f, double df)
        {
            if (!(df > 0.0)) return 0.0;
            if (f <= Mitte[0]) return Band(0, df);
            int n = Mitte.Length;
            if (f >= Mitte[n - 1]) return Band(n - 1, df);
            int i = 0;
            while (i < n - 2 && f > Mitte[i + 1]) i++;
            double t = Math.Log(f / Mitte[i]) / Math.Log(Mitte[i + 1] / Mitte[i]);
            double a = Band(i, df), c = Band(i + 1, df);
            return a + (c - a) * t;
        }

        /// <summary>Breitband-Pegelabsenkung (dB, positiv) aus dem Mittenband 500 Hz, mit Staerkefaktor.</summary>
        public static double PegelDb(double df, double staerke) => staerke * Band(BezugsBand, df);

        /// <summary>
        /// Grenzfrequenz des Tiefpasses: die Frequenz (ab 500 Hz), bei der Luft ueber 'dLuft' Meter plus der ZUSATZ der Laubdaempfung
        /// gegenueber dem Mittenband gerade 'schwelleDb' erreichen. Der Pegel im Mittenband steckt schon in PegelDb, hier zaehlt nur die Neigung.
        /// Ergebnis 'keine' (22000), wenn bis 16 kHz die Schwelle nicht erreicht wird; untere Grenze 500 Hz.
        /// </summary>
        public static double Grenzfrequenz(double dLuft, double df, double staerke, LuftBaender luft, double schwelleDb = 3.0)
        {
            const double fMin = 500.0, fMax = 16000.0;
            double ref0 = Bei(Mitte[BezugsBand], df);
            if (Ueber(fMin, dLuft, df, staerke, ref0, luft) >= schwelleDb) return fMin;
            if (Ueber(fMax, dLuft, df, staerke, ref0, luft) < schwelleDb) return 22000.0;
            double lo = Math.Log(fMin), hi = Math.Log(fMax);
            for (int i = 0; i < 24; i++)
            {
                double mid = 0.5 * (lo + hi);
                if (Ueber(Math.Exp(mid), dLuft, df, staerke, ref0, luft) < schwelleDb) lo = mid; else hi = mid;
            }
            return Math.Exp(0.5 * (lo + hi));
        }

        // Luft ueber dLuft Meter + Zusatz des Laubs gegenueber dem Mittenband (ohne Schliessung, keine Speicherzuweisung)
        static double Ueber(double f, double dLuft, double df, double staerke, double ref0, LuftBaender luft)
            => luft.Alpha(f) * dLuft + staerke * Math.Max(0.0, Bei(f, df) - ref0);
    }

    /// <summary>Luftabsorption (ISO 9613-1) in dB/m an neun Stuetzstellen 63 Hz .. 16 kHz, dazwischen log-log (Potenzgesetz).</summary>
    internal sealed class LuftBaender
    {
        public static readonly double[] F = { 63, 125, 250, 500, 1000, 2000, 4000, 8000, 16000 };
        static readonly double[] LnF = LnVon(F);
        readonly double[] lnA = new double[9];

        public static LuftBaender Standard() => Neu(20.0, Iso9613.Bezugsdruck, 70.0);

        public static LuftBaender Neu(double tC, double pKPa, double rf)
        {
            var r = new LuftBaender();
            for (int i = 0; i < F.Length; i++) r.lnA[i] = Math.Log(Math.Max(1e-12, Iso9613.Alpha(F[i], tC, pKPa, rf)));
            return r;
        }

        static double[] LnVon(double[] a) { var r = new double[a.Length]; for (int i = 0; i < a.Length; i++) r[i] = Math.Log(a[i]); return r; }

        /// <summary>dB/m bei Frequenz f (Hz); ausserhalb der Stuetzstellen mit dem Anstieg des letzten Abschnitts fortgesetzt.</summary>
        public double Alpha(double f)
        {
            double lf = Math.Log(Math.Max(1.0, f));
            int i = 0;
            while (i < F.Length - 2 && lf > LnF[i + 1]) i++;
            double t = (lf - LnF[i]) / (LnF[i + 1] - LnF[i]);
            return Math.Exp(lnA[i] + (lnA[i + 1] - lnA[i]) * t);
        }
    }

    /// <summary>Was Wald.Berechnen fuer ein Geraeusch liefert.</summary>
    internal struct WaldErgebnis
    {
        public bool Gueltig;
        public float Meter;     // Weg durch dichtes Laub (auf 200 m begrenzt)
        public float Db;        // Pegelabsenkung (positiv = leiser)
        public float LpHz;      // Tiefpass (22000 = keiner)
    }

    /// <summary>Einstufung nach Objektname: Gewicht eines Objekts im Raster (1 = ausgewachsener Baum, 0 = zaehlt nicht).</summary>
    internal static class WaldKlassen
    {
        // Namen aus den Karten-Szenen (Woods, Shoreline, Customs, Lighthouse, Reserve, Interchange; im Spiel-Datenbestand nachgezaehlt):
        //   Baeume: pine, pine02_big, pine04_big, birch, tree, ash          -> 1.0
        //   Straeucher: filbert_big, filbert, fibert_hedge (gross)           -> 0.5;  filbert_small -> 0.25
        //   zaehlen nicht: Billboards (Fern-Attrappen), Stuempfe, Farn, Gras, Totholz (vetky), trockene Straeucher, Schatten-Objekte
        static readonly string[] BaumAnfang = { "pine", "spruce", "fir", "birch", "tree", "ash", "oak", "maple", "linden", "aspen", "alder", "poplar", "willow" };

        public static float Gewicht(string name)
        {
            if (string.IsNullOrEmpty(name)) return 0f;
            string n = name.ToLowerInvariant();
            if (n.Contains("billboard") || n.Contains("shadow") || n.Contains("stump") || n.Contains("dry") || n.Contains("dead")) return 0f;
            if (n.StartsWith("filbert_small", StringComparison.Ordinal)) return 0.25f;
            if (n.StartsWith("filbert", StringComparison.Ordinal) || n.StartsWith("fibert_hedge", StringComparison.Ordinal)) return 0.5f;
            for (int i = 0; i < BaumAnfang.Length; i++)
            {
                if (!n.StartsWith(BaumAnfang[i], StringComparison.Ordinal)) continue;
                // "tree" / "pine" / "ash" nur, wenn danach Ziffern, "_", " ", "(" oder Ende folgen (nicht "ashtray", "treeline")
                int e = BaumAnfang[i].Length;
                if (e == n.Length) return 1f;
                char c = n[e];
                if (char.IsDigit(c) || c == '_' || c == ' ' || c == '(' || c == '-') return 1f;
            }
            return 0f;
        }
    }

    /// <summary>
    /// Grobes 2D-Raster: gewichtete Baeume je Zelle (Standard 10 x 10 m = 100 m2, also Zahl = Baeume je 100 m2).
    /// 'Laenge' verfolgt eine Strecke durch das Raster und summiert die Meter je Zelle mal Dichtefaktor (min(1, Dichte / Schwelle)).
    /// </summary>
    internal sealed class WaldRaster
    {
        public readonly double X0, Z0, Zelle;
        public readonly int Nx, Nz;
        readonly float[] zaehl;
        public double Summe;        // Summe aller Gewichte (gewichtete Baeume)
        public int Eintraege;

        public WaldRaster(double x0, double z0, double x1, double z1, double zelle, int maxZellen = 1500000)
        {
            double w = Math.Max(1.0, x1 - x0), h = Math.Max(1.0, z1 - z0);
            if (w * h / (zelle * zelle) > maxZellen) zelle = Math.Sqrt(w * h / maxZellen);   // Riesenflaeche: groebere Zellen
            Zelle = zelle; X0 = x0; Z0 = z0;
            Nx = (int)Math.Ceiling(w / zelle) + 1; Nz = (int)Math.Ceiling(h / zelle) + 1;
            zaehl = new float[Nx * Nz];
        }

        public void Add(double x, double z, float gewicht)
        {
            int ix = (int)Math.Floor((x - X0) / Zelle), iz = (int)Math.Floor((z - Z0) / Zelle);
            if (ix < 0 || iz < 0 || ix >= Nx || iz >= Nz) return;
            zaehl[iz * Nx + ix] += gewicht;
            Summe += gewicht; Eintraege++;
        }

        /// <summary>Gewichtete Baeume je 100 m2 in der Zelle, in der (x,z) liegt (0 ausserhalb).</summary>
        public double Dichte(double x, double z)
        {
            int ix = (int)Math.Floor((x - X0) / Zelle), iz = (int)Math.Floor((z - Z0) / Zelle);
            if (ix < 0 || iz < 0 || ix >= Nx || iz >= Nz) return 0.0;
            return zaehl[iz * Nx + ix] * 100.0 / (Zelle * Zelle);
        }

        static bool Beschneiden(double a, double d, double lo, double hi, ref double t0, ref double t1)
        {
            if (Math.Abs(d) < 1e-12) return a >= lo && a <= hi;
            double ta = (lo - a) / d, tb = (hi - a) / d;
            if (ta > tb) { double s = ta; ta = tb; tb = s; }
            if (ta > t0) t0 = ta;
            if (tb < t1) t1 = tb;
            return t0 <= t1;
        }

        /// <summary>
        /// Meter "dichtes Laub" entlang der waagrechten Strecke (ax,az)-(bx,bz). Jede Zelle zaehlt mit min(1, Dichte / schwelle),
        /// 'schwelle' = Baeume je 100 m2, ab denen eine Zelle als voll dichtes Laub gilt. Keine Speicherzuweisung.
        /// </summary>
        public double Laenge(double ax, double az, double bx, double bz, double schwelle)
        {
            double dx = bx - ax, dz = bz - az;
            double len = Math.Sqrt(dx * dx + dz * dz);
            if (len < 1e-6 || Eintraege == 0 || !(schwelle > 0.0)) return 0.0;
            double t0 = 0.0, t1 = 1.0;
            if (!Beschneiden(ax, dx, X0, X0 + Nx * Zelle, ref t0, ref t1)) return 0.0;
            if (!Beschneiden(az, dz, Z0, Z0 + Nz * Zelle, ref t0, ref t1)) return 0.0;
            double px = ax + dx * t0, pz = az + dz * t0;
            int ix = (int)Math.Floor((px - X0) / Zelle), iz = (int)Math.Floor((pz - Z0) / Zelle);
            if (ix < 0) ix = 0; else if (ix >= Nx) ix = Nx - 1;
            if (iz < 0) iz = 0; else if (iz >= Nz) iz = Nz - 1;
            int sx = dx > 0 ? 1 : -1, sz = dz > 0 ? 1 : -1;
            double tmx = Math.Abs(dx) < 1e-12 ? double.MaxValue : ((dx > 0 ? X0 + (ix + 1) * Zelle : X0 + ix * Zelle) - ax) / dx;
            double tmz = Math.Abs(dz) < 1e-12 ? double.MaxValue : ((dz > 0 ? Z0 + (iz + 1) * Zelle : Z0 + iz * Zelle) - az) / dz;
            double tdx = Math.Abs(dx) < 1e-12 ? double.MaxValue : Zelle / Math.Abs(dx);
            double tdz = Math.Abs(dz) < 1e-12 ? double.MaxValue : Zelle / Math.Abs(dz);
            double k = 100.0 / (Zelle * Zelle * schwelle);
            double t = t0, summe = 0.0;
            int grenze = Nx + Nz + 4;
            for (int g = 0; g < grenze; g++)
            {
                double tn = tmx < tmz ? tmx : tmz;
                if (tn > t1) tn = t1;
                double f = zaehl[iz * Nx + ix] * k;
                if (f > 1.0) f = 1.0;
                if (tn > t) summe += f * (tn - t);
                if (tn >= t1) break;
                t = tn;
                if (tmx < tmz) { ix += sx; tmx += tdx; if (ix < 0 || ix >= Nx) break; }
                else { iz += sz; tmz += tdz; if (iz < 0 || iz >= Nz) break; }
            }
            return summe * len;
        }
    }
}
