using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace Materialklang
{
    // 0.8.4 - Kartenexport fuer den Sandkasten der Demo-Seite: reine Rechnung OHNE Unity (Schnitt von Dreieck/Quader/Kugel/Kapsel
    // mit einer waagerechten Ebene, Zusammenfassen der Segmente, JSON). Der Unity-Teil (Collider sammeln) steht in KartenExport.cs.
    // Der Offline-Test (Materialklang-KlangTest) uebersetzt diese Datei mit.
    //
    // Koordinaten: Spiel-x/z in Metern (y = Hoehe). Ein Segment liegt in der Ebene y = Schnitthoehe.

    internal struct P3 { public double X, Y, Z; public P3(double x, double y, double z) { X = x; Y = y; Z = z; } }
    internal struct P2 { public double X, Z; public P2(double x, double z) { X = x; Z = z; } }

    internal enum SegArt : byte
    {
        Kante,      // Rand eines Schnittpolygons (dicker Quader, Saeule) - Dicke = kleinere Ausdehnung des Polygons
        Mitte,      // Mittellinie einer duennen Wand (Quader oder zusammengefuehrtes Flaechenpaar) - Dicke gemessen
        Flaeche     // Schnitt eines Mesh-Dreiecks: eine Seite einer Wand, Dicke = Gruppendicke (Bauakustik.EinzelDicke)
    }

    internal struct Seg
    {
        public double AX, AZ, BX, BZ;
        public int Mat;           // Gruppe (enum Gruppe)
        public float Dicke;       // m
        public SegArt Art;
        public bool Notloesung;   // Bounds-Rechteck eines nicht lesbaren Meshes
        public double Laenge => Math.Sqrt((BX - AX) * (BX - AX) + (BZ - AZ) * (BZ - AZ));
    }

    internal static class KartenSchnitt
    {
        /// <summary>Waende duenner als das (m) werden als Mittellinie mit Dicke ausgegeben statt als Polygonrand.</summary>
        internal const double DuennGrenze = 1.0;
        /// <summary>Segmente kuerzer als das (m) fallen nach dem Zusammenfassen weg.</summary>
        internal const double MinLaenge = 0.2;
        /// <summary>Hoechster Abstand zweier paralleler Flaechen, die als EINE Wand gelten (m).</summary>
        internal const double PaarMaxAbstand = 0.6;
        internal const double PaarMinAbstand = 0.02;
        const double WinkelTol = 1.0 * Math.PI / 180.0;   // gleiche Richtung (Zusammenfassen)
        const double LinieTol = 0.05;                      // gleiche Gerade: Abstand der Geraden (m)
        const double LueckeTol = 0.05;                     // Abstand zweier Stuecke auf derselben Geraden, der noch verbunden wird (m)
        const double PaarWinkelTol = 2.0 * Math.PI / 180.0;
        const double PaarUeberlapp = 0.8;                  // Anteil der Laenge, die beide Flaechen gemeinsam haben muessen

        // ------------------------------------------------------------------ Schnitte

        /// <summary>Schnitt eines Dreiecks mit der Ebene y = h. true, wenn ein Segment (Laenge &gt; 0) entsteht.
        /// Waagerechte Dreiecke in der Ebene ergeben nichts (Boden/Decke).</summary>
        internal static bool Dreieck(P3 a, P3 b, P3 c, double h, out P2 s0, out P2 s1)
        {
            s0 = s1 = default;
            double da = a.Y - h, db = b.Y - h, dc = c.Y - h;
            // Punkte genau auf der Ebene leicht anheben: so zaehlt jede Kante eindeutig (kein doppelter Punkt)
            const double eps = 1e-7;
            if (Math.Abs(da) < eps) da = eps;
            if (Math.Abs(db) < eps) db = eps;
            if (Math.Abs(dc) < eps) dc = eps;
            if ((da > 0 && db > 0 && dc > 0) || (da < 0 && db < 0 && dc < 0)) return false;
            int n = 0;
            P2 p0 = default, p1 = default;
            void Kante(P3 u, double du, P3 v, double dv)
            {
                if ((du > 0) == (dv > 0)) return;
                double t = du / (du - dv);
                var p = new P2(u.X + (v.X - u.X) * t, u.Z + (v.Z - u.Z) * t);
                if (n == 0) p0 = p; else p1 = p;
                n++;
            }
            Kante(a, da, b, db); Kante(b, db, c, dc); Kante(c, dc, a, da);
            if (n != 2) return false;
            s0 = p0; s1 = p1;
            return (p0.X - p1.X) * (p0.X - p1.X) + (p0.Z - p1.Z) * (p0.Z - p1.Z) > 1e-10;
        }

        /// <summary>Schnittpolygon (konvex, gegen den Uhrzeigersinn) eines gedrehten Quaders mit Mitte c und halben
        /// Kantenvektoren ex/ey/ez (Welt) mit der Ebene y = h. Leer, wenn die Ebene ihn nicht trifft.</summary>
        internal static List<P2> Quader(P3 c, P3 ex, P3 ey, P3 ez, double h)
        {
            var ecken = new P3[8];
            for (int i = 0; i < 8; i++)
            {
                double sx = (i & 1) != 0 ? 1 : -1, sy = (i & 2) != 0 ? 1 : -1, sz = (i & 4) != 0 ? 1 : -1;
                ecken[i] = new P3(c.X + sx * ex.X + sy * ey.X + sz * ez.X, c.Y + sx * ex.Y + sy * ey.Y + sz * ez.Y, c.Z + sx * ex.Z + sy * ey.Z + sz * ez.Z);
            }
            var pkt = new List<P2>(6);
            for (int i = 0; i < 8; i++)
                if (ecken[i].Y == h) pkt.Add(new P2(ecken[i].X, ecken[i].Z));   // Ecke genau auf der Ebene
            for (int i = 0; i < 8; i++)
                for (int k = 0; k < 3; k++)
                {
                    int j = i | (1 << k);
                    if (j == i) continue;                 // jede der 12 Kanten einmal (von der Ecke mit Bit k = 0)
                    double di = ecken[i].Y - h, dj = ecken[j].Y - h;
                    if (di == 0 || dj == 0 || (di > 0) == (dj > 0)) continue;
                    double t = di / (di - dj);
                    pkt.Add(new P2(ecken[i].X + (ecken[j].X - ecken[i].X) * t, ecken[i].Z + (ecken[j].Z - ecken[i].Z) * t));
                }
            return Huelle(pkt);
        }

        /// <summary>Kreis-Schnitt einer Kugel (Mitte c, Radius r) mit y = h als n-Eck.</summary>
        internal static List<P2> Kugel(P3 c, double r, double h, int n = 12)
        {
            var l = new List<P2>(n);
            double dy = h - c.Y;
            if (Math.Abs(dy) >= r) return l;
            double rr = Math.Sqrt(r * r - dy * dy);
            for (int i = 0; i < n; i++) { double w = 2 * Math.PI * i / n; l.Add(new P2(c.X + rr * Math.Cos(w), c.Z + rr * Math.Sin(w))); }
            return l;
        }

        /// <summary>Schnitt einer Kapsel (Achse p0-p1, Radius r) mit y = h: Huelle der Kreise von Kugeln entlang der Achse
        /// (die Kapsel ist konvex, ihr Schnitt also auch).</summary>
        internal static List<P2> Kapsel(P3 p0, P3 p1, double r, double h, int n = 12)
        {
            var pkt = new List<P2>();
            const int k = 9;
            void Bei(double t) => pkt.AddRange(Kugel(new P3(p0.X + (p1.X - p0.X) * t, p0.Y + (p1.Y - p0.Y) * t, p0.Z + (p1.Z - p0.Z) * t), r, h, n));
            for (int i = 0; i < k; i++) Bei(i / (double)(k - 1));
            double dy = p1.Y - p0.Y;
            if (Math.Abs(dy) > 1e-9) Bei(Math.Max(0, Math.Min(1, (h - p0.Y) / dy)));   // Achsenpunkt in Ebenenhoehe: voller Radius
            return Huelle(pkt);
        }

        /// <summary>Konvexe Huelle (Andrew), gegen den Uhrzeigersinn, ohne doppelte Punkte.</summary>
        internal static List<P2> Huelle(List<P2> p)
        {
            var r = new List<P2>();
            if (p.Count < 3) { if (p.Count > 0) r.AddRange(p); return r; }
            var s = new List<P2>(p);
            s.Sort((u, v) => u.X != v.X ? u.X.CompareTo(v.X) : u.Z.CompareTo(v.Z));
            var h = new P2[2 * s.Count];
            int k = 0;
            double Kreuz(P2 o, P2 a, P2 b) => (a.X - o.X) * (b.Z - o.Z) - (a.Z - o.Z) * (b.X - o.X);
            for (int i = 0; i < s.Count; i++) { while (k >= 2 && Kreuz(h[k - 2], h[k - 1], s[i]) <= 1e-12) k--; h[k++] = s[i]; }
            for (int i = s.Count - 2, t = k + 1; i >= 0; i--) { while (k >= t && Kreuz(h[k - 2], h[k - 1], s[i]) <= 1e-12) k--; h[k++] = s[i]; }
            for (int i = 0; i < k - 1; i++) r.Add(h[i]);
            return r;
        }

        /// <summary>Kleinste Breite eines konvexen Polygons (Drehschieber ueber die Kanten) mit Richtung (ux,uz) der dazu
        /// gehoerenden Kante und Lage des Streifens: Mittellinie = Punkt (mx,mz) + t*(ux,uz), t in [t0,t1].</summary>
        internal static double Breite(List<P2> poly, out double ux, out double uz, out double mx, out double mz, out double t0, out double t1)
        {
            ux = 1; uz = 0; mx = mz = t0 = t1 = 0;
            int n = poly.Count;
            if (n < 2) return 0;
            double best = double.MaxValue;
            for (int i = 0; i < n; i++)
            {
                P2 a = poly[i], b = poly[(i + 1) % n];
                double dx = b.X - a.X, dz = b.Z - a.Z, l = Math.Sqrt(dx * dx + dz * dz);
                if (l < 1e-9) continue;
                dx /= l; dz /= l;
                double nmin = double.MaxValue, nmax = double.MinValue, tmin = double.MaxValue, tmax = double.MinValue;
                foreach (var q in poly)
                {
                    double nn = -(q.X - a.X) * dz + (q.Z - a.Z) * dx, tt = (q.X - a.X) * dx + (q.Z - a.Z) * dz;
                    nmin = Math.Min(nmin, nn); nmax = Math.Max(nmax, nn); tmin = Math.Min(tmin, tt); tmax = Math.Max(tmax, tt);
                }
                double w = nmax - nmin;
                if (w < best)
                {
                    best = w; ux = dx; uz = dz;
                    double nm = (nmin + nmax) * 0.5;
                    mx = a.X - dz * nm; mz = a.Z + dx * nm;    // Punkt auf der Mittellinie (Normale = (-uz, ux))
                    t0 = tmin; t1 = tmax;
                }
            }
            return best == double.MaxValue ? 0 : best;
        }

        /// <summary>Ein Schnittpolygon (Quader, Kugel, Kapsel, Notloesungs-Rechteck) als Wandsegmente: duenn und lang -&gt;
        /// EINE Mittellinie mit der Breite als Dicke, sonst die Kanten mit der kleineren Ausdehnung als Dicke.</summary>
        internal static void PolygonZuWand(List<P2> poly, int mat, bool notloesung, List<Seg> aus)
        {
            if (poly.Count < 2) return;
            double w = Breite(poly, out double ux, out double uz, out double mx, out double mz, out double t0, out double t1);
            double len = t1 - t0;
            if (poly.Count >= 3 && w <= DuennGrenze && len >= 2 * w)
            {
                aus.Add(new Seg { AX = mx + ux * t0, AZ = mz + uz * t0, BX = mx + ux * t1, BZ = mz + uz * t1, Mat = mat, Dicke = (float)Math.Max(w, 0.005), Art = SegArt.Mitte, Notloesung = notloesung });
                return;
            }
            int n = poly.Count;
            for (int i = 0; i < n; i++)
            {
                P2 a = poly[i], b = poly[(i + 1) % n];
                if (n == 2 && i == 1) break;
                aus.Add(new Seg { AX = a.X, AZ = a.Z, BX = b.X, BZ = b.Z, Mat = mat, Dicke = (float)Math.Max(w, 0.005), Art = SegArt.Kante, Notloesung = notloesung });
            }
        }

        /// <summary>Oeffnung (Tuer/Fenster) aus einem gedrehten Quader: die waagerecht laengste Kante ist die Breite.
        /// Liefert die Linie durch die Mitte (Hoehe egal) und die zweitlaengste waagerechte Ausdehnung als Dicke.</summary>
        internal static bool Oeffnung(P3 c, P3 ex, P3 ey, P3 ez, out P2 a, out P2 b, out double dicke)
        {
            var ax = new[] { ex, ey, ez };
            int best = -1; double bl = -1, zweit = 0;
            for (int i = 0; i < 3; i++)
            {
                double l = Math.Sqrt(ax[i].X * ax[i].X + ax[i].Z * ax[i].Z);
                if (l > bl) { zweit = Math.Max(zweit, bl); bl = l; best = i; } else zweit = Math.Max(zweit, l);
            }
            a = new P2(c.X - ax[best].X, c.Z - ax[best].Z);
            b = new P2(c.X + ax[best].X, c.Z + ax[best].Z);
            dicke = 2 * Math.Max(zweit, 0);
            return bl > 0.05;
        }

        // ------------------------------------------------------------------ Zusammenfassen

        /// <summary>
        /// Fasst zusammen: (1) Stuecke auf derselben Geraden mit gleichem Material/gleicher Art verschmelzen,
        /// (2) eindeutige Paare paralleler Mesh-Flaechen (Innen-/Aussenseite) zu einer Mittellinie mit dem Abstand als Dicke,
        /// (3) noch einmal (1), (4) Stuecke unter MinLaenge verwerfen. Gibt die neue Liste zurueck; paare = Anzahl der Paare.
        /// </summary>
        internal static List<Seg> Zusammenfassen(List<Seg> ein, out int paare)
        {
            var a = Kollinear(ein);
            a = Paaren(a, out paare);
            a = Kollinear(a);
            var r = new List<Seg>(a.Count);
            foreach (var s in a) if (s.Laenge >= MinLaenge) r.Add(s);
            return r;
        }

        static double Winkel(in Seg s)
        {
            double w = Math.Atan2(s.BZ - s.AZ, s.BX - s.AX);
            if (w < 0) w += Math.PI;
            if (w >= Math.PI) w -= Math.PI;
            return w;
        }

        /// <summary>Stuecke auf derselben Geraden (Richtung +-1 Grad, Endpunkte hoechstens 5 cm neben der Geraden des anderen) mit
        /// gleichem Material, gleicher Art und Luecke bis 5 cm werden eins (auch ueberlappende/doppelte). Je zusammenhaengender Gruppe
        /// wird eine Gerade angepasst; weicht ein Endpunkt mehr als 10 cm davon ab (Bogen aus vielen kleinen Knicken), bleibt die
        /// Gruppe unveraendert. Dicke = laengengewichtetes Mittel.</summary>
        internal static List<Seg> Kollinear(List<Seg> ein)
        {
            int n = ein.Count;
            const double zelle = 2.0;
            var gitter = new Dictionary<long, List<int>>();
            long Key(int i, int j) => ((long)i << 32) ^ (uint)j;
            var zellen = new HashSet<long>();
            for (int i = 0; i < n; i++)
            {
                var s = ein[i];
                double l = s.Laenge;
                int schritte = Math.Max(1, (int)Math.Ceiling(l / 1.0));
                zellen.Clear();
                for (int k = 0; k <= schritte; k++)
                {
                    double t = k / (double)schritte;
                    zellen.Add(Key((int)Math.Floor((s.AX + (s.BX - s.AX) * t) / zelle), (int)Math.Floor((s.AZ + (s.BZ - s.AZ) * t) / zelle)));
                }
                foreach (long key in zellen)
                {
                    if (!gitter.TryGetValue(key, out var li)) gitter[key] = li = new List<int>();
                    li.Add(i);
                }
            }
            var eltern = new int[n];
            for (int i = 0; i < n; i++) eltern[i] = i;
            int Wurzel(int i) { while (eltern[i] != i) { eltern[i] = eltern[eltern[i]]; i = eltern[i]; } return i; }
            foreach (var li in gitter.Values)
                for (int x = 0; x < li.Count; x++)
                    for (int y = x + 1; y < li.Count; y++)
                    {
                        int i = li[x], j = li[y];
                        int wi = Wurzel(i), wj = Wurzel(j);
                        if (wi == wj || !Verbindbar(ein[i], ein[j])) continue;
                        eltern[wi] = wj;
                    }
            var gruppen = new Dictionary<int, List<int>>();
            for (int i = 0; i < n; i++)
            {
                int w = Wurzel(i);
                if (!gruppen.TryGetValue(w, out var g)) gruppen[w] = g = new List<int>(1);
                g.Add(i);
            }
            var aus = new List<Seg>(gruppen.Count);
            foreach (var g in gruppen.Values)
            {
                if (g.Count == 1) { aus.Add(ein[g[0]]); continue; }
                GruppeVerschmelzen(ein, g, aus);
            }
            return aus;
        }

        static bool Verbindbar(in Seg s, in Seg q)
        {
            if (s.Mat != q.Mat || s.Art != q.Art || s.Notloesung != q.Notloesung) return false;
            double dw = Math.Abs(Winkel(s) - Winkel(q));
            if (dw > Math.PI / 2) dw = Math.PI - dw;
            if (dw > WinkelTol) return false;
            double ls = s.Laenge;
            if (ls < 1e-9) return false;
            double ux = (s.BX - s.AX) / ls, uz = (s.BZ - s.AZ) / ls;
            double da = -(q.AX - s.AX) * uz + (q.AZ - s.AZ) * ux, db = -(q.BX - s.AX) * uz + (q.BZ - s.AZ) * ux;
            if (Math.Abs(da) > LinieTol || Math.Abs(db) > LinieTol) return false;
            double ta = (q.AX - s.AX) * ux + (q.AZ - s.AZ) * uz, tb = (q.BX - s.AX) * ux + (q.BZ - s.AZ) * uz;
            return Math.Max(ta, tb) >= -LueckeTol && Math.Min(ta, tb) <= ls + LueckeTol;
        }

        static void GruppeVerschmelzen(List<Seg> ein, List<int> g, List<Seg> aus)
        {
            // Gerade: Richtung = laengengewichtetes Mittel der verdoppelten Winkel, Lage = gewichteter Schwerpunkt
            double cx = 0, cz = 0, sx = 0, sz = 0, sl = 0;
            foreach (int i in g)
            {
                var s = ein[i]; double l = s.Laenge, ww = 2 * Winkel(s);
                cx += l * Math.Cos(ww); cz += l * Math.Sin(ww);
                sx += l * (s.AX + s.BX) * 0.5; sz += l * (s.AZ + s.BZ) * 0.5; sl += l;
            }
            if (sl < 1e-12) { foreach (int i in g) aus.Add(ein[i]); return; }
            double wm = 0.5 * Math.Atan2(cz, cx), ux = Math.Cos(wm), uz = Math.Sin(wm);
            double ox = sx / sl, oz = sz / sl;
            var iv = new List<(double t0, double t1, double th, double l)>(g.Count);
            foreach (int i in g)
            {
                var s = ein[i];
                double da = -(s.AX - ox) * uz + (s.AZ - oz) * ux, db = -(s.BX - ox) * uz + (s.BZ - oz) * ux;
                if (Math.Abs(da) > 2 * LinieTol || Math.Abs(db) > 2 * LinieTol) { foreach (int k in g) aus.Add(ein[k]); return; }   // Bogen: so lassen
                double ta = (s.AX - ox) * ux + (s.AZ - oz) * uz, tb = (s.BX - ox) * ux + (s.BZ - oz) * uz;
                iv.Add((Math.Min(ta, tb), Math.Max(ta, tb), s.Dicke, s.Laenge));
            }
            iv.Sort((p, q) => p.t0.CompareTo(q.t0));
            var v = ein[g[0]];
            double c0 = iv[0].t0, c1 = iv[0].t1, sth = iv[0].th * iv[0].l, sll = iv[0].l;
            void Raus()
            {
                aus.Add(new Seg { AX = ox + ux * c0, AZ = oz + uz * c0, BX = ox + ux * c1, BZ = oz + uz * c1, Mat = v.Mat, Art = v.Art, Notloesung = v.Notloesung,
                                  Dicke = (float)(sll > 1e-12 ? sth / sll : v.Dicke) });
            }
            for (int k = 1; k < iv.Count; k++)
            {
                if (iv[k].t0 <= c1 + LueckeTol) { c1 = Math.Max(c1, iv[k].t1); sth += iv[k].th * iv[k].l; sll += iv[k].l; continue; }
                Raus();
                c0 = iv[k].t0; c1 = iv[k].t1; sth = iv[k].th * iv[k].l; sll = iv[k].l;
            }
            Raus();
        }

        /// <summary>Eindeutige Paare paralleler Flaechen-Segmente (Art Flaeche, gleiches Material, Abstand 2-60 cm, beide zu
        /// mindestens 80 % ueberlappend, fuer beide ist der andere der einzige Kandidat) werden zu einer Mittellinie.</summary>
        internal static List<Seg> Paaren(List<Seg> ein, out int paare)
        {
            paare = 0;
            int n = ein.Count;
            const double zelle = 2.0;
            var gitter = new Dictionary<long, List<int>>();
            long Key(int i, int j) => ((long)i << 32) ^ (uint)j;
            for (int i = 0; i < n; i++)
            {
                var s = ein[i];
                if (s.Art != SegArt.Flaeche || s.Laenge < MinLaenge) continue;
                int gx = (int)Math.Floor((s.AX + s.BX) * 0.5 / zelle), gz = (int)Math.Floor((s.AZ + s.BZ) * 0.5 / zelle);
                if (!gitter.TryGetValue(Key(gx, gz), out var l)) gitter[Key(gx, gz)] = l = new List<int>();
                l.Add(i);
            }
            var partner = new int[n];
            var anzahl = new int[n];
            for (int i = 0; i < n; i++) partner[i] = -1;
            for (int i = 0; i < n; i++)
            {
                var s = ein[i];
                if (s.Art != SegArt.Flaeche || s.Laenge < MinLaenge) continue;
                double mx = (s.AX + s.BX) * 0.5, mz = (s.AZ + s.BZ) * 0.5, r = s.Laenge * 0.5 + PaarMaxAbstand;
                int gx0 = (int)Math.Floor((mx - r) / zelle), gx1 = (int)Math.Floor((mx + r) / zelle);
                int gz0 = (int)Math.Floor((mz - r) / zelle), gz1 = (int)Math.Floor((mz + r) / zelle);
                for (int gx = gx0; gx <= gx1; gx++)
                    for (int gz = gz0; gz <= gz1; gz++)
                    {
                        if (!gitter.TryGetValue(Key(gx, gz), out var l)) continue;
                        foreach (int j in l)
                        {
                            if (j <= i) continue;
                            if (!PaarPasst(s, ein[j], out _)) continue;
                            anzahl[i]++; anzahl[j]++; partner[i] = j; partner[j] = i;
                        }
                    }
            }
            var aus = new List<Seg>(n);
            var fertig = new bool[n];
            for (int i = 0; i < n; i++)
            {
                if (fertig[i]) continue;
                int j = partner[i];
                if (j >= 0 && anzahl[i] == 1 && anzahl[j] == 1 && partner[j] == i && !fertig[j])
                {
                    PaarPasst(ein[i], ein[j], out Seg mitte);
                    aus.Add(mitte); fertig[i] = fertig[j] = true; paare++;
                    continue;
                }
                aus.Add(ein[i]); fertig[i] = true;
            }
            return aus;
        }

        static bool PaarPasst(in Seg s, in Seg q, out Seg mitte)
        {
            mitte = default;
            if (q.Art != SegArt.Flaeche || q.Mat != s.Mat || q.Notloesung != s.Notloesung) return false;
            double ws = Winkel(s), wq = Winkel(q), dw = Math.Abs(ws - wq);
            if (dw > Math.PI / 2) dw = Math.PI - dw;
            if (dw > PaarWinkelTol) return false;
            double ls = s.Laenge, lq = q.Laenge;
            if (ls < 1e-9 || lq < 1e-9) return false;
            double ux = (s.BX - s.AX) / ls, uz = (s.BZ - s.AZ) / ls;
            // Abstand der Mitte von q zur Geraden s
            double qmx = (q.AX + q.BX) * 0.5 - s.AX, qmz = (q.AZ + q.BZ) * 0.5 - s.AZ;
            double d = -qmx * uz + qmz * ux;
            double ad = Math.Abs(d);
            if (ad < PaarMinAbstand || ad > PaarMaxAbstand) return false;
            double qa = (q.AX - s.AX) * ux + (q.AZ - s.AZ) * uz, qb = (q.BX - s.AX) * ux + (q.BZ - s.AZ) * uz;
            double q0 = Math.Min(qa, qb), q1 = Math.Max(qa, qb);
            double ov = Math.Min(ls, q1) - Math.Max(0, q0);
            if (ov < PaarUeberlapp * ls || ov < PaarUeberlapp * lq) return false;
            double t0 = Math.Min(0, q0), t1 = Math.Max(ls, q1), h = d * 0.5;
            double ox = s.AX - uz * h, oz = s.AZ + ux * h;   // um den halben Abstand zur Mitte verschoben (Normale (-uz,ux))
            mitte = new Seg { AX = ox + ux * t0, AZ = oz + uz * t0, BX = ox + ux * t1, BZ = oz + uz * t1, Mat = s.Mat, Dicke = (float)ad, Art = SegArt.Mitte, Notloesung = s.Notloesung };
            return true;
        }

        // ------------------------------------------------------------------ JSON

        static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

        /// <summary>Zahl auf 0,01 gerundet, ohne Exponent und ohne "-0".</summary>
        internal static string Z(double v)
        {
            double r = Math.Round(v, 2, MidpointRounding.AwayFromZero);
            if (r == 0) r = 0;
            return r.ToString("0.##", Inv);
        }

        /// <summary>Zahl mit bis zu 3 Nachkommastellen (Dicken: 2 mm Blech).</summary>
        internal static string Z3(double v)
        {
            double r = Math.Round(v, 3, MidpointRounding.AwayFromZero);
            if (r == 0) r = 0;
            return r.ToString("0.###", Inv);
        }

        internal static string Text(string s)
        {
            var sb = new StringBuilder(s.Length + 2);
            sb.Append('"');
            foreach (char ch in s)
            {
                if (ch == '"' || ch == '\\') sb.Append('\\').Append(ch);
                else if (ch < 0x20) sb.Append("\\u").Append(((int)ch).ToString("x4"));
                else sb.Append(ch);
            }
            return sb.Append('"').ToString();
        }

        internal static void Punkt(StringBuilder sb, double x, double z) => sb.Append('[').Append(Z(x)).Append(',').Append(Z(z)).Append(']');

        /// <summary>Dateiname sandbox-&lt;karte&gt;-&lt;hoehe&gt;.json (Karte nur aus Buchstaben/Ziffern/-/_).</summary>
        internal static string Dateiname(string karte, double hoehe)
        {
            var sb = new StringBuilder();
            foreach (char ch in string.IsNullOrEmpty(karte) ? "map" : karte)
                sb.Append(char.IsLetterOrDigit(ch) || ch == '-' || ch == '_' ? char.ToLowerInvariant(ch) : '_');
            return "sandbox-" + sb + "-" + hoehe.ToString("0.0#", Inv) + ".json";
        }
    }

    /// <summary>Schreibt die Exportdatei (Format version 1, siehe LIESMICH "Map export"). Reine Rechnung, laeuft auch im Hintergrundfaden.</summary>
    internal static class KartenJson
    {
        internal static string Schreiben(string version, string karte, double sliceY, double ueber, double boden, double px, double py, double pz,
            double minX, double minZ, double maxX, double maxZ, List<Seg> waende, List<string> oeffnungen, string[] namen, (string k, double v)[] zahlen)
        {
            var sb = new StringBuilder(64 + waende.Count * 64 + oeffnungen.Count * 100);
            string Z(double v) => KartenSchnitt.Z(v);
            sb.Append("{\"version\":1,\"generator\":").Append(KartenSchnitt.Text("Materialklang " + version))
              .Append(",\"map\":").Append(KartenSchnitt.Text(karte ?? "map"))
              .Append(",\"sliceY\":").Append(Z(sliceY)).Append(",\"sliceAbove\":").Append(Z(ueber)).Append(",\"ground\":").Append(Z(boden))
              .Append(",\"origin\":[").Append(Z(px)).Append(',').Append(Z(py)).Append(',').Append(Z(pz)).Append(']')
              .Append(",\"bounds\":{\"minX\":").Append(Z(minX)).Append(",\"minZ\":").Append(Z(minZ)).Append(",\"maxX\":").Append(Z(maxX)).Append(",\"maxZ\":").Append(Z(maxZ)).Append('}')
              .Append(",\"player\":");
            KartenSchnitt.Punkt(sb, px, pz);
            sb.Append(",\n\"walls\":[");
            for (int i = 0; i < waende.Count; i++)
            {
                var s = waende[i];
                if (i > 0) sb.Append(',');
                sb.Append("\n{\"a\":"); KartenSchnitt.Punkt(sb, s.AX, s.AZ);
                sb.Append(",\"b\":"); KartenSchnitt.Punkt(sb, s.BX, s.BZ);
                sb.Append(",\"mat\":\"").Append(s.Mat >= 0 && s.Mat < namen.Length ? namen[s.Mat] : "Unknown").Append("\",\"th\":").Append(KartenSchnitt.Z3(s.Dicke));
                if (s.Notloesung) sb.Append(",\"approx\":1");
                sb.Append('}');
            }
            sb.Append("],\n\"openings\":[");
            for (int i = 0; i < oeffnungen.Count; i++) { if (i > 0) sb.Append(','); sb.Append('\n').Append(oeffnungen[i]); }
            sb.Append("],\n\"counts\":{");
            for (int i = 0; i < zahlen.Length; i++)
            {
                if (i > 0) sb.Append(',');
                sb.Append(KartenSchnitt.Text(zahlen[i].k)).Append(':').Append(KartenSchnitt.Z(zahlen[i].v));
            }
            return sb.Append("}}\n").ToString();
        }
    }
}
