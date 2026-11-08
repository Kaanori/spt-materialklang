using System;
using System.Collections.Generic;

namespace Materialklang
{
    // 0.8.4 - 13. Distance (SEB): reine Rechnung (ohne Unity/SEB, im Offline-Test).
    // Baut die Lautstaerkekurve (Unity "CustomRolloff", x = Entfernung / MaxDistance) fuer ferne Schuesse:
    //  - bis 25 m exakt SEBs eigene Kurve (Hermite-Abschnitt an 25 m geteilt -> derselbe kubische Verlauf),
    //  - danach die Zielwerte (dB rel. 1 m) 50 m -6 / 100 m -8 / 200 m -11 / 400 m -16, weiter -5 dB je Oktave,
    //  - mal Fern-Ausgleich k(d) fuer die weniger dichte Fernaufnahme (Blend-Stufe B2..B3 der Bank),
    //  - mal weiches Ausblenden (cos^2) auf den letzten 20 % bis MaxDistance.
    internal struct KurvenPunkt
    {
        public float T, V, In, Out;
        public KurvenPunkt(float t, float v, float i, float o) { T = t; V = v; In = i; Out = o; }
    }

    internal enum ClipEntscheid { Unveraendert, MitteVoll, MitteNehmen }

    internal static class FernschussKern
    {
        public const float NahGrenze = 25f;                       // bis hier exakt SEB
        public static readonly float[] ZielM = { 50f, 100f, 200f, 400f };
        public static readonly float[] ZielDb = { -6f, -8f, -11f, -16f };
        public const float SteigungDanach = -5f;                  // dB je Oktave nach 400 m (wie 200 -> 400)
        public const float AusblendAnteil = 0.2f;                 // letzte 20 % der MaxDistance

        // ---- Unity-AnimationCurve (Keyframes ohne Gewichtung): kubisches Hermite je Abschnitt ----
        public static double Auswerten(KurvenPunkt[] k, double x)
        {
            if (k == null || k.Length == 0) return 0;
            if (x <= k[0].T) return k[0].V;
            if (x >= k[k.Length - 1].T) return k[k.Length - 1].V;
            int i = Abschnitt(k, x);
            var a = k[i]; var b = k[i + 1];
            double dt = b.T - a.T, t = (x - a.T) / dt;
            double m0 = a.Out * dt, m1 = b.In * dt;
            double t2 = t * t, t3 = t2 * t;
            return (2 * t3 - 3 * t2 + 1) * a.V + (t3 - 2 * t2 + t) * m0 + (-2 * t3 + 3 * t2) * b.V + (t3 - t2) * m1;
        }

        /// <summary>Ableitung dV/dx der Kurve (innerhalb eines Abschnitts).</summary>
        public static double Ableitung(KurvenPunkt[] k, double x)
        {
            if (k == null || k.Length < 2 || x < k[0].T || x > k[k.Length - 1].T) return 0;
            int i = Abschnitt(k, x);
            var a = k[i]; var b = k[i + 1];
            double dt = b.T - a.T, t = (x - a.T) / dt;
            double m0 = a.Out * dt, m1 = b.In * dt;
            double t2 = t * t;
            double dh = (6 * t2 - 6 * t) * a.V + (3 * t2 - 4 * t + 1) * m0 + (-6 * t2 + 6 * t) * b.V + (3 * t2 - 2 * t) * m1;
            return dh / dt;
        }

        static int Abschnitt(KurvenPunkt[] k, double x)
        {
            int i = 0;
            while (i < k.Length - 2 && x >= k[i + 1].T) i++;
            return i;
        }

        public static double DbAusGain(double g) => g <= 1e-9 ? -200.0 : 20.0 * Math.Log10(g);
        public static double GainAusDb(double db) => Math.Pow(10.0, db / 20.0);

        // ---- Zielverlauf in dB rel. 1 m (log-linear zwischen den Stuetzstellen) ----
        public static double ZielVerlauf(double d, double db25)
        {
            double l = Math.Log(d, 2);
            double xa = Math.Log(NahGrenze, 2), ya = db25;
            for (int i = 0; i < ZielM.Length; i++)
            {
                double xb = Math.Log(ZielM[i], 2), yb = ZielDb[i];
                if (l <= xb) return ya + (yb - ya) * (l - xa) / (xb - xa);
                xa = xb; ya = yb;
            }
            return ya + SteigungDanach * (l - xa);
        }

        /// <summary>Fern-Ausgleich: Faktor auf die Kurve, damit die Fernaufnahme (fernDb weniger dicht) so laut ankommt wie die mittlere.
        /// SEB blendet linear: Mitte mit Anteil p, Fern mit (1-p). Hoerbar: p + (1-p)*D (D = 10^(-fernDb/20)) -> k = 1/(p + (1-p)*D).</summary>
        public static double Ausgleich(double d, float b2, float b3, float fernDb, bool fernVorhanden)
        {
            if (!fernVorhanden || fernDb <= 0f) return 1.0;
            GrenzenKlemmen(ref b2, ref b3);
            double D = GainAusDb(-fernDb);
            if (d <= b2) return 1.0;
            if (d >= b3) return 1.0 / D;
            double p = (b3 - d) / (b3 - b2);
            return 1.0 / (p + (1 - p) * D);
        }

        /// <summary>Der Nahbereich bleibt unberuehrt: liegt die Blend-Stufe einer Bank vor 25 m, beginnt der Ausgleich erst dort.</summary>
        public static void GrenzenKlemmen(ref float b2, ref float b3)
        {
            if (!(b2 >= NahGrenze)) b2 = NahGrenze;
            if (!(b3 > b2 + 1f)) b3 = b2 + 1f;
        }

        public static double Ausblenden(double d, double maxD)
        {
            double start = maxD * (1 - AusblendAnteil);
            if (d <= start) return 1.0;
            if (d >= maxD) return 0.0;
            double c = Math.Cos(0.5 * Math.PI * (d - start) / (maxD - start));
            return c * c;
        }

        /// <summary>Absolute Kurvenhoehe in Metern (ab 25 m): SEB(1 m) * Ziel * Ausgleich * Ausblenden.</summary>
        public static double Fern(double d, double g1, double db25, double maxD, float b2, float b3, float fernDb, bool fernVorhanden)
        {
            double g = g1 * GainAusDb(ZielVerlauf(d, db25)) * Ausgleich(d, b2, b3, fernDb, fernVorhanden) * Ausblenden(d, maxD);
            return Math.Min(1.0, g);
        }

        /// <summary>Die neue Kurve. seb = SEBs Gunshots-Kurve (bezogen auf sebMax, z. B. 600 m), maxD = neue MaxDistance draussen.</summary>
        public static KurvenPunkt[] Bauen(KurvenPunkt[] seb, float sebMax, float maxD, float b2, float b3, float fernDb, bool fernVorhanden)
        {
            if (seb == null || seb.Length < 2 || sebMax <= NahGrenze || maxD <= NahGrenze * 2f) return null;
            GrenzenKlemmen(ref b2, ref b3);
            double s = maxD / sebMax;                     // Zeitachse: x_neu = x_seb * sebMax / maxD
            double x25 = NahGrenze / sebMax;
            double g1 = Auswerten(seb, 1.0 / sebMax);
            double g25 = Auswerten(seb, x25);
            if (g1 <= 1e-6 || g25 <= 1e-6) return null;
            double db25 = DbAusGain(g25 / g1);

            var l = new List<KurvenPunkt>(64);
            // 1) SEB-Punkte vor 25 m, auf die neue Zeitachse umgerechnet (Tangenten dV/dx mal maxD/sebMax)
            foreach (var p in seb)
            {
                if (p.T >= x25 - 1e-7) break;
                l.Add(new KurvenPunkt((float)(p.T / s), p.V, (float)(p.In * s), (float)(p.Out * s)));
            }
            // 2) Teilungspunkt 25 m: Wert und Ableitung exakt aus SEB -> der Abschnitt davor bleibt derselbe Kubik.
            double inTan = Ableitung(seb, x25) * s;
            double outTan = Ableitung1(d => Fern(d, g1, db25, maxD, b2, b3, fernDb, fernVorhanden), NahGrenze, +1) * maxD;
            l.Add(new KurvenPunkt(NahGrenze / maxD, (float)g25, (float)inTan, (float)outTan));

            // 3) Punkte dahinter: Viertel-Oktaven, Stuetzstellen, Blend-Grenzen, Ausblenden
            var dl = new List<double>();
            for (double d = NahGrenze * Math.Pow(2, 0.25); d < maxD; d *= Math.Pow(2, 0.25)) dl.Add(d);
            foreach (var z in ZielM) dl.Add(z);
            if (fernVorhanden && fernDb > 0f)
                for (int i = 0; i <= 6; i++) dl.Add(b2 + (b3 - b2) * i / 6.0);
            double start = maxD * (1 - AusblendAnteil);
            for (int i = 0; i <= 8; i++) dl.Add(start + (maxD - start) * i / 8.0);
            dl.Sort();
            double letzte = NahGrenze;
            foreach (double d in dl)
            {
                if (d <= letzte + 0.25 || d >= maxD - 1e-3) continue;
                letzte = d;
                Func<double, double> f = e => Fern(e, g1, db25, maxD, b2, b3, fernDb, fernVorhanden);
                l.Add(new KurvenPunkt((float)(d / maxD), (float)f(d),
                    (float)(Ableitung1(f, d, -1) * maxD), (float)(Ableitung1(f, d, +1) * maxD)));
            }
            l.Add(new KurvenPunkt(1f, 0f, 0f, 0f));
            return l.ToArray();
        }

        // einseitige Ableitung (Knicke an Stuetzstellen/Blend-Grenzen -> getrennte In-/Out-Tangenten)
        static double Ableitung1(Func<double, double> f, double d, int seite)
        {
            double h = Math.Max(1e-3, d * 1e-4);
            return seite > 0 ? (f(d + h) - f(d)) / h : (f(d) - f(d - h)) / h;
        }

        /// <summary>Pegelaenderung der neuen Kurve gegenueber SEB an einer Entfernung (dB; +200 = SEB ist dort stumm).</summary>
        public static double Unterschied(KurvenPunkt[] neu, float maxD, KurvenPunkt[] seb, float sebMax, float d)
        {
            double a = d >= maxD ? 0 : Auswerten(neu, d / maxD);
            double b = d >= sebMax ? 0 : Auswerten(seb, d / sebMax);
            if (b <= 1e-9) return a <= 1e-9 ? 0 : 200;
            return DbAusGain(a) - DbAusGain(b);
        }

        /// <summary>Clip-Wahl (nachgebaut aus SoundBank.PickClipsByDistance): Stufe 0..4 aus den Blend-Grenzen.</summary>
        public static int Stufe(float[] blend, float d)
        {
            int i = 0;
            while (i < blend.Length && !(d < blend[i])) i++;
            return i;
        }

        /// <summary>Fehlt die Fernaufnahme (gedaempfte Banks), blendet das Spiel ab der dritten Grenze ins Leere aus und ist ab der
        /// vierten stumm. Dann die mittlere Aufnahme bis MaxDistance weiter nutzen.</summary>
        public static ClipEntscheid ClipWahl(float[] blend, float d, bool fernLeer, bool mitteLeer)
        {
            if (blend == null || blend.Length < 4 || !fernLeer || mitteLeer) return ClipEntscheid.Unveraendert;
            int st = Stufe(blend, d);
            if (st == 3) return ClipEntscheid.MitteVoll;     // Mitte laeuft schon (clip1), nur der Anteil fuer die leere Fernaufnahme fehlt
            if (st >= 4) return ClipEntscheid.MitteNehmen;   // Spiel waehlt die leere Fernaufnahme -> kein Klang
            return ClipEntscheid.Unveraendert;
        }
    }
}
