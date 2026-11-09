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

    // ==========================================================================================
    //  0.8.5 - Scan-Export: Geometrie per Physik-Abfrage statt aus Mesh-Daten (nicht lesbare MeshCollider: 99,9 % auf Customs).
    //  Der Unity-Teil (KartenExport.cs) schiesst waagerechte Strahlen entlang Gitterlinien in x- und z-Richtung, jeweils vorwaerts
    //  (Eintritte = Vorderseiten) und rueckwaerts (Austritte), und gibt je Linie die Treffer hierher. Reine Rechnung OHNE Unity:
    //   1. LinieEintragen: Ein-/Austritt desselben Colliders paaren -> Belegungsraster (Zelle fest + Collider + gemessene Dicke).
    //      Treffer ohne Partner (einseitige Flaeche, duenne Wand) markieren ihre Zelle trotzdem, Dicke = Gruppendicke.
    //   2. Auswerten: Raster -> dicke Bereiche (> DuennGrenze, Abstandstransformation + Oeffnung) als Umriss (Marching Squares, Kante),
    //      duenne Waende als Mittellinie (Zhang-Suen-Verduennung, m-Nachbarschaft), je Material getrennt, Douglas-Peucker,
    //      Dicke = Median der gemessenen Dicken der Zellen.
    // ==========================================================================================

    /// <summary>Ein Treffer auf einer Scanlinie: T = Abstand vom Linienanfang (m), Eintritt = Vorderseite in Linienrichtung
    /// (Vorwaertsstrahl), sonst Austritt (Rueckwaertsstrahl). Cos = |Flaechennormale . Linienrichtung| (Dicke senkrecht zur Wand).</summary>
    internal struct ScanTreffer
    {
        public double T; public int Slot; public bool Eintritt; public float Cos;
        public ScanTreffer(double t, int slot, bool eintritt, float cos) { T = t; Slot = slot; Eintritt = eintritt; Cos = cos; }
    }

    /// <summary>Belegungsraster. Zelle (i,j) hat die Mitte (X0 + (i+0,5)·Res, Z0 + (j+0,5)·Res); Index j·W + i.
    /// x-Linie k laeuft durch die Zellmitten der Zeile k, z-Linie k durch die der Spalte k.</summary>
    internal sealed class ScanRaster
    {
        internal const byte TrefferX = 1, TrefferZ = 2, InnenX = 4, InnenZ = 8, Kurz = 16, SlotTreffer = 32;
        public readonly int W, H;
        public readonly double X0, Z0, Res;
        public readonly byte[] Flag;
        public readonly ushort[] Slot;      // Collider-Slot + 1 (0 = keiner)
        public readonly ushort[] DickeMm;   // kleinste gemessene Dicke in mm (0 = keine)
        public ScanRaster(int w, int h, double x0, double z0, double res)
        {
            W = w; H = h; X0 = x0; Z0 = z0; Res = res;
            Flag = new byte[w * h]; Slot = new ushort[w * h]; DickeMm = new ushort[w * h];
        }
        public double LaengeX => W * Res;
        public double LaengeZ => H * Res;
        /// <summary>fest: getroffen, kurz durchquert (bis KurzFuellung) oder lang durchquert aus BEIDEN Richtungen.</summary>
        public bool Fest(int i)
        {
            byte f = Flag[i];
            return (f & (TrefferX | TrefferZ | Kurz)) != 0 || (f & (InnenX | InnenZ)) == (InnenX | InnenZ);
        }
        public int Zellen => W * H;
    }

    internal static class KartenScan
    {
        /// <summary>Durchquerungen bis zu dieser Laenge (m) gelten aus einer Richtung als fest; laengere nur, wenn die Linie
        /// der anderen Richtung durch dieselbe Zelle es bestaetigt (schuetzt vor falsch gepaarten offenen Meshes).</summary>
        internal const double KurzFuellung = 2.0;
        /// <summary>Laengere Ein-/Austrittspaare werden nicht gepaart (beide gelten als einzelne Flaechen).</summary>
        internal const double MaxFuellung = 150.0;
        /// <summary>Seitenast der Mittellinie, der hoechstens so viele Zellen lang ist, faellt weg (Rauschen der Verduennung).</summary>
        internal const int AstMax = 3;

        internal sealed class Zahlen
        {
            public int Paare, Einzel, FesteZellen, DickeZellen, DuenneZellen, Skelett, Umrisse, AesteWeg, Linien, Kanten, Mitten, OeffnungZellen;
        }

        // ------------------------------------------------------------------ 1. Linie -> Raster

        /// <summary>Traeger einer Linie ins Raster: Treffer nach T sortieren, je Collider Eintritt mit dem naechsten Austritt
        /// paaren (kein anderer Treffer desselben Colliders dazwischen, Laenge bis MaxFuellung). gruppenDicke[slot] = Dicke
        /// fuer Treffer ohne Partner (m).</summary>
        internal static void LinieEintragen(ScanRaster r, bool xLinie, int k, List<ScanTreffer> ev, IList<float> gruppenDicke, Zahlen z)
        {
            if (ev == null || ev.Count == 0) return;
            // gleiche Stelle: Austritt vor Eintritt (zwei aneinanderstossende Teile desselben Colliders: erst das eine verlassen)
            ev.Sort((a, b) => a.T != b.T ? a.T.CompareTo(b.T) : a.Eintritt.CompareTo(b.Eintritt));
            var offen = new Dictionary<int, int>();   // slot -> Index des offenen Eintritts
            for (int i = 0; i < ev.Count; i++)
            {
                var e = ev[i];
                if (e.Eintritt)
                {
                    if (offen.TryGetValue(e.Slot, out int alt)) Einzeln(r, xLinie, k, ev[alt], gruppenDicke, z);
                    offen[e.Slot] = i;
                    continue;
                }
                if (offen.TryGetValue(e.Slot, out int ei))
                {
                    offen.Remove(e.Slot);
                    var a = ev[ei];
                    double len = e.T - a.T;
                    if (len <= MaxFuellung)
                    {
                        Durchquerung(r, xLinie, k, a.T, e.T, e.Slot, Math.Max(a.Cos, 0.05f), z);
                        continue;
                    }
                    Einzeln(r, xLinie, k, a, gruppenDicke, z);
                }
                Einzeln(r, xLinie, k, e, gruppenDicke, z);
            }
            foreach (int ei in offen.Values) Einzeln(r, xLinie, k, ev[ei], gruppenDicke, z);
        }

        static int Zelle(ScanRaster r, bool xLinie, int k, double t)
        {
            int n = xLinie ? r.W : r.H;
            int c = (int)Math.Floor(t / r.Res);
            if (c < 0) c = 0; else if (c >= n) c = n - 1;
            return xLinie ? k * r.W + c : c * r.W + k;
        }

        static void Setzen(ScanRaster r, int idx, byte flag, int slot, double dicke, bool vonTreffer)
        {
            r.Flag[idx] |= flag;
            if (vonTreffer && (r.Flag[idx] & ScanRaster.SlotTreffer) == 0) { r.Slot[idx] = (ushort)(slot + 1); r.Flag[idx] |= ScanRaster.SlotTreffer; }
            else if (r.Slot[idx] == 0) r.Slot[idx] = (ushort)(slot + 1);
            if (dicke <= 0) return;
            int mm = (int)Math.Round(dicke * 1000.0);
            if (mm < 1) mm = 1; else if (mm > 65000) mm = 65000;
            if (r.DickeMm[idx] == 0 || mm < r.DickeMm[idx]) r.DickeMm[idx] = (ushort)mm;
        }

        static void Einzeln(ScanRaster r, bool xLinie, int k, ScanTreffer e, IList<float> gruppenDicke, Zahlen z)
        {
            double d = e.Slot >= 0 && e.Slot < gruppenDicke.Count ? gruppenDicke[e.Slot] : 0.15;
            Setzen(r, Zelle(r, xLinie, k, e.T), xLinie ? ScanRaster.TrefferX : ScanRaster.TrefferZ, e.Slot, Math.Max(d, 0.002), true);
            z.Einzel++;
        }

        static void Durchquerung(ScanRaster r, bool xLinie, int k, double t0, double t1, int slot, float cos, Zahlen z)
        {
            double dicke = (t1 - t0) * cos;
            if (t1 - t0 < 0.005) { dicke = 0; }   // Beruehrung zweier Teile, keine Messung
            byte treffer = xLinie ? ScanRaster.TrefferX : ScanRaster.TrefferZ;
            byte innen = (t1 - t0) <= KurzFuellung ? ScanRaster.Kurz : (xLinie ? ScanRaster.InnenX : ScanRaster.InnenZ);
            int n = xLinie ? r.W : r.H;
            // Austritt genau auf einer Zellgrenze gehoert zur Zelle davor
            int c0 = Math.Max(0, Math.Min(n - 1, (int)Math.Floor(t0 / r.Res))), c1 = Math.Max(c0, Math.Min(n - 1, (int)Math.Floor(t1 / r.Res - 1e-6)));
            for (int c = c0; c <= c1; c++)
            {
                int idx = xLinie ? k * r.W + c : c * r.W + k;
                bool rand = c == c0 || c == c1;
                Setzen(r, idx, rand ? (byte)(treffer | innen) : innen, slot, dicke > 0 ? Math.Max(dicke, 0.002) : 0, rand);
            }
            z.Paare++;
        }

        // ------------------------------------------------------------------ 2. Raster -> Waende

        /// <summary>Raster in Wandsegmente. slotMat[slot] = Gruppe, gruppenDicke[slot] = Ersatzdicke (m), oeffnungen = Tueren/Fenster
        /// (ihre Zellen werden freigeraeumt, damit keine Wand durch die Oeffnung laeuft). duennGrenze: breiter = Umriss.</summary>
        internal static List<Seg> Auswerten(ScanRaster r, IList<int> slotMat, IList<float> gruppenDicke, List<(P2 a, P2 b)> oeffnungen, Zahlen z,
            double duennGrenze = KartenSchnitt.DuennGrenze, int unbekannt = 12)
        {
            int W = r.W, H = r.H, N = W * H;
            if (oeffnungen != null) foreach (var o in oeffnungen) z.OeffnungZellen += Freiraeumen(r, o.a, o.b);
            var fest = new byte[N];
            for (int i = 0; i < N; i++) if (r.Fest(i)) { fest[i] = 1; z.FesteZellen++; }

            // dicke Bereiche: Kern = Zellen tiefer als duennGrenze/2 in fest (Chamfer 3-4), dann um dieselbe Tiefe in fest erweitert
            var tiefe = Chamfer(fest, W, H, true);
            int schwelle = (int)Math.Ceiling(3.0 * ((duennGrenze / r.Res) + 1.0) / 2.0);   // Streifen > duennGrenze breit hat eine Mitte mit dieser Tiefe
            var kern = new byte[N];
            bool irgendKern = false;
            for (int i = 0; i < N; i++) if (fest[i] != 0 && tiefe[i] >= schwelle) { kern[i] = 1; irgendKern = true; }
            var dick = new byte[N];
            if (irgendKern)
            {
                var abKern = Chamfer(kern, W, H, false);
                for (int i = 0; i < N; i++) if (fest[i] != 0 && abKern[i] <= schwelle) { dick[i] = 1; z.DickeZellen++; }
            }
            var duenn = new byte[N];
            for (int i = 0; i < N; i++) if (fest[i] != 0 && dick[i] == 0) { duenn[i] = 1; z.DuenneZellen++; }
            fest = null; tiefe = null; kern = null;

            var aus = new List<Seg>();
            double tol = 0.5 * r.Res + 1e-6;
            Umriss(r, dick, slotMat, gruppenDicke, unbekannt, tol, aus, z);
            Mittellinien(r, duenn, dick, slotMat, gruppenDicke, unbekannt, tol, aus, z);
            var fertig = new List<Seg>(aus.Count);
            foreach (var s in aus) if (s.Laenge >= 0.05) fertig.Add(s);
            return fertig;
        }

        /// <summary>Zellen, deren Mitte hoechstens 0,6 Zellen neben der Oeffnungslinie liegt (Enden um 10 cm gekuerzt), leeren.</summary>
        internal static int Freiraeumen(ScanRaster r, P2 a, P2 b)
        {
            double dx = b.X - a.X, dz = b.Z - a.Z, l = Math.Sqrt(dx * dx + dz * dz);
            if (l < 0.3) return 0;
            dx /= l; dz /= l;
            double t0 = 0.1, t1 = l - 0.1, band = 0.6 * r.Res;
            int i0 = (int)Math.Floor((Math.Min(a.X, b.X) - band - r.X0) / r.Res) - 1, i1 = (int)Math.Floor((Math.Max(a.X, b.X) + band - r.X0) / r.Res) + 1;
            int j0 = (int)Math.Floor((Math.Min(a.Z, b.Z) - band - r.Z0) / r.Res) - 1, j1 = (int)Math.Floor((Math.Max(a.Z, b.Z) + band - r.Z0) / r.Res) + 1;
            int n = 0;
            for (int j = Math.Max(0, j0); j <= Math.Min(r.H - 1, j1); j++)
                for (int i = Math.Max(0, i0); i <= Math.Min(r.W - 1, i1); i++)
                {
                    double cx = r.X0 + (i + 0.5) * r.Res - a.X, cz = r.Z0 + (j + 0.5) * r.Res - a.Z;
                    double t = cx * dx + cz * dz, q = Math.Abs(-cx * dz + cz * dx);
                    if (t < t0 || t > t1 || q > band) continue;
                    int idx = j * r.W + i;
                    if (r.Flag[idx] != 0) { r.Flag[idx] = 0; n++; }
                }
            return n;
        }

        /// <summary>Chamfer-3-4-Abstand (gesaettigt bei 255). innen = true: Abstand fester Zellen zur naechsten leeren
        /// (ausserhalb des Rasters = leer); innen = false: Abstand jeder Zelle zur naechsten gesetzten.</summary>
        internal static byte[] Chamfer(byte[] m, int W, int H, bool innen)
        {
            int N = W * H;
            var d = new int[N];
            const int Gross = 1 << 20;
            for (int i = 0; i < N; i++) d[i] = innen ? (m[i] != 0 ? Gross : 0) : (m[i] != 0 ? 0 : Gross);
            int Rand(int x, int y) => innen ? 0 : Gross;
            for (int y = 0; y < H; y++)
                for (int x = 0; x < W; x++)
                {
                    int i = y * W + x, v = d[i];
                    if (v == 0) continue;
                    int l = x > 0 ? d[i - 1] : Rand(x - 1, y), u = y > 0 ? d[i - W] : Rand(x, y - 1);
                    int ul = x > 0 && y > 0 ? d[i - W - 1] : Rand(x - 1, y - 1), ur = x < W - 1 && y > 0 ? d[i - W + 1] : Rand(x + 1, y - 1);
                    v = Math.Min(v, Math.Min(l + 3, u + 3)); v = Math.Min(v, Math.Min(ul + 4, ur + 4));
                    d[i] = v;
                }
            for (int y = H - 1; y >= 0; y--)
                for (int x = W - 1; x >= 0; x--)
                {
                    int i = y * W + x, v = d[i];
                    if (v == 0) continue;
                    int rr = x < W - 1 ? d[i + 1] : Rand(x + 1, y), dn = y < H - 1 ? d[i + W] : Rand(x, y + 1);
                    int dr = x < W - 1 && y < H - 1 ? d[i + W + 1] : Rand(x + 1, y + 1), dl = x > 0 && y < H - 1 ? d[i + W - 1] : Rand(x - 1, y + 1);
                    v = Math.Min(v, Math.Min(rr + 3, dn + 3)); v = Math.Min(v, Math.Min(dr + 4, dl + 4));
                    d[i] = v;
                }
            var b = new byte[N];
            for (int i = 0; i < N; i++) b[i] = (byte)Math.Min(255, d[i]);
            return b;
        }

        // ------------------------------------------------------------------ Material / Dicke je Zelle

        static int MatVon(ScanRaster r, int idx, IList<int> slotMat, int unbekannt)
        {
            int s = r.Slot[idx] - 1;
            if (s >= 0 && s < slotMat.Count) return slotMat[s];
            // Zelle ohne Collider (lang von beiden Seiten gefuellt): Nachbarn
            int x = idx % r.W, y = idx / r.W;
            for (int dy = -1; dy <= 1; dy++)
                for (int dx = -1; dx <= 1; dx++)
                {
                    int xx = x + dx, yy = y + dy;
                    if (xx < 0 || yy < 0 || xx >= r.W || yy >= r.H) continue;
                    int q = r.Slot[yy * r.W + xx] - 1;
                    if (q >= 0 && q < slotMat.Count) return slotMat[q];
                }
            return unbekannt;
        }

        static double Median(List<double> v, double ersatz)
        {
            if (v.Count == 0) return ersatz;
            v.Sort();
            int n = v.Count;
            return (n & 1) == 1 ? v[n / 2] : 0.5 * (v[n / 2 - 1] + v[n / 2]);
        }

        /// <summary>Polylinie (Punkte p, je Punkt Zelle) in Stuecke gleichen Materials teilen, je Stueck Douglas-Peucker,
        /// Dicke je Segment = Median der Zelldicken zwischen seinen Endpunkten.</summary>
        static void Ausgeben(ScanRaster r, List<P2> p, List<int> zellen, IList<int> slotMat, IList<float> gruppenDicke, int unbekannt, double tol,
            SegArt art, bool geschlossen, List<Seg> aus)
        {
            int n = p.Count;
            if (n < 2) return;
            var mat = new int[n];
            for (int i = 0; i < n; i++) mat[i] = MatVon(r, zellen[i], slotMat, unbekannt);
            // einzelne abweichende Zellen zwischen gleichem Material glätten
            for (int i = 1; i + 1 < n; i++) if (mat[i] != mat[i - 1] && mat[i - 1] == mat[i + 1]) mat[i] = mat[i - 1];
            int start = -1;
            if (geschlossen)
            {
                // Anfang an einen Materialwechsel legen, damit ein Stueck nicht ueber den Ringanfang zerfaellt; ohne Wechsel an den
                // Punkt mit kleinstem x (dann z) - eine Ecke der Huelle, die Douglas-Peucker ohnehin behaelt
                for (int i = 1; i < n; i++) if (mat[i] != mat[i - 1]) { start = i; break; }
                if (start < 0)
                {
                    start = 0;
                    for (int i = 1; i < n; i++) if (p[i].X < p[start].X - 1e-9 || (Math.Abs(p[i].X - p[start].X) <= 1e-9 && p[i].Z < p[start].Z)) start = i;
                }
            }
            if (start < 0) start = 0;
            var q = new List<P2>(n + 1); var qz = new List<int>(n + 1); var qm = new List<int>(n + 1);
            int cnt = geschlossen ? n + 1 : n;
            for (int k = 0; k < cnt; k++) { int i = (start + k) % n; q.Add(p[i]); qz.Add(zellen[i]); qm.Add(mat[i]); }
            int a = 0;
            for (int i = 1; i <= q.Count; i++)
            {
                if (i < q.Count && qm[i] == qm[a]) continue;
                int b = Math.Min(i, q.Count - 1);   // Stueck a..b (b = erster Punkt des naechsten Materials: gemeinsamer Punkt, keine Luecke)
                if (b > a) Stueck(r, q, qz, a, b, qm[a], gruppenDicke, tol, art, aus);
                a = b;
                if (i >= q.Count - 1) break;
            }
        }

        static void Stueck(ScanRaster r, List<P2> q0, List<int> qz, int a, int b, int mat, IList<float> gruppenDicke, double tol, SegArt art, List<Seg> aus)
        {
            // Treppenstufen des Rasters glaetten (Mittel mit den Nachbarn, Enden fest), dann Douglas-Peucker
            var q = new List<P2>(q0);
            if (art == SegArt.Mitte)   // nur Mittellinien (m-Pfad = Treppe); Umrisse behalten ihre Ecken
            for (int i = a + 1; i < b; i++)
                q[i] = new P2(0.25 * q0[i - 1].X + 0.5 * q0[i].X + 0.25 * q0[i + 1].X, 0.25 * q0[i - 1].Z + 0.5 * q0[i].Z + 0.25 * q0[i + 1].Z);
            var keep = DouglasPeucker(q, a, b, tol);
            var werte = new List<double>();
            for (int k = 0; k + 1 < keep.Count; k++)
            {
                int i0 = keep[k], i1 = keep[k + 1];
                werte.Clear();
                double ersatz = 0.15;
                for (int i = i0; i <= i1; i++)
                {
                    int c = qz[i];
                    if (r.DickeMm[c] > 0) werte.Add(r.DickeMm[c] / 1000.0);
                    else { int s = r.Slot[c] - 1; if (s >= 0 && s < gruppenDicke.Count) ersatz = gruppenDicke[s]; }
                }
                double d = Median(werte, ersatz);
                aus.Add(new Seg { AX = q[i0].X, AZ = q[i0].Z, BX = q[i1].X, BZ = q[i1].Z, Mat = mat, Dicke = (float)Math.Max(d, 0.002), Art = art });
            }
        }

        /// <summary>Douglas-Peucker auf p[a..b]; liefert die behaltenen Indizes (aufsteigend, mit a und b).</summary>
        internal static List<int> DouglasPeucker(List<P2> p, int a, int b, double tol)
        {
            var keep = new List<int> { a };
            var stapel = new Stack<(int, int)>();
            var behalten = new SortedSet<int> { a, b };
            stapel.Push((a, b));
            while (stapel.Count > 0)
            {
                var (i0, i1) = stapel.Pop();
                if (i1 - i0 < 2) continue;
                P2 u = p[i0], v = p[i1];
                double dx = v.X - u.X, dz = v.Z - u.Z, l = Math.Sqrt(dx * dx + dz * dz);
                double best = -1; int bi = -1;
                for (int i = i0 + 1; i < i1; i++)
                {
                    double ex = p[i].X - u.X, ez = p[i].Z - u.Z;
                    double d = l < 1e-12 ? Math.Sqrt(ex * ex + ez * ez) : Math.Abs(ex * dz - ez * dx) / l;
                    if (d > best) { best = d; bi = i; }
                }
                if (best > tol) { behalten.Add(bi); stapel.Push((i0, bi)); stapel.Push((bi, i1)); }
            }
            keep.Clear(); keep.AddRange(behalten);
            return keep;
        }

        // ------------------------------------------------------------------ Umriss (Marching Squares)

        /// <summary>Umriss der dicken Bereiche: Abtastpunkte = Zellmitten, Kantenpunkte = Mitte zwischen fester und leerer Zelle,
        /// gegen den Uhrzeigersinn um das Feste; Sattel = Diagonale verbunden (keine Luecke in schraegen Waenden).</summary>
        static void Umriss(ScanRaster r, byte[] m, IList<int> slotMat, IList<float> gruppenDicke, int unbekannt, double tol, List<Seg> aus, Zahlen z)
        {
            int W = r.W, H = r.H, W2 = W + 2;
            bool S(int x, int y) => x >= 0 && y >= 0 && x < W && y < H && m[y * W + x] != 0;
            // Kanten-Id: waagerechte Kante zwischen (x,y)-(x+1,y): 2·((y+1)·W2 + x+1); senkrechte (x,y)-(x,y+1): +1
            int Hk(int x, int y) => 2 * ((y + 1) * W2 + (x + 1));
            int Vk(int x, int y) => 2 * ((y + 1) * W2 + (x + 1)) + 1;
            var next = new Dictionary<int, int>();
            var zelle = new Dictionary<int, int>();
            var ecke = new bool[4]; var kid = new int[4]; var kx = new int[4]; var ky = new int[4];
            for (int y = -1; y < H; y++)
                for (int x = -1; x < W; x++)
                {
                    // Ecken gegen den Uhrzeigersinn: 0 (x,y), 1 (x+1,y), 2 (x+1,y+1), 3 (x,y+1)
                    ecke[0] = S(x, y); ecke[1] = S(x + 1, y); ecke[2] = S(x + 1, y + 1); ecke[3] = S(x, y + 1);
                    if (ecke[0] == ecke[1] && ecke[1] == ecke[2] && ecke[2] == ecke[3]) continue;
                    // Kante e verbindet Ecke e und Ecke e+1
                    kid[0] = Hk(x, y); kid[1] = Vk(x + 1, y); kid[2] = Hk(x, y + 1); kid[3] = Vk(x, y);
                    kx[0] = x; ky[0] = y; kx[1] = x + 1; ky[1] = y; kx[2] = x + 1; ky[2] = y + 1; kx[3] = x; ky[3] = y + 1;
                    for (int e = 0; e < 4; e++)
                    {
                        if (!(ecke[e] && !ecke[(e + 1) & 3])) continue;      // Start: fest -> leer
                        for (int f = 1; f < 4; f++)
                        {
                            int g = (e + f) & 3;
                            if (!ecke[g] && ecke[(g + 1) & 3])          // naechstes Ende: leer -> fest
                            {
                                next[kid[e]] = kid[g];
                                zelle[kid[e]] = ky[e] * W + kx[e];
                                break;
                            }
                        }
                    }
                }
            var pkt = new List<P2>(); var zl = new List<int>();
            while (next.Count > 0)
            {
                int start = -1;
                foreach (var kv in next) { start = kv.Key; break; }
                pkt.Clear(); zl.Clear();
                int id = start;
                while (next.TryGetValue(id, out int nx))
                {
                    next.Remove(id);
                    pkt.Add(Kantenpunkt(r, id, W2)); zl.Add(zelle[id]);
                    id = nx;
                    if (id == start) break;
                }
                z.Umrisse++;
                int vor = aus.Count;
                Ausgeben(r, pkt, zl, slotMat, gruppenDicke, unbekannt, tol, SegArt.Kante, id == start, aus);
                z.Kanten += aus.Count - vor;
            }
        }

        static P2 Kantenpunkt(ScanRaster r, int id, int W2)
        {
            int c = id >> 1, x = c % W2 - 1, y = c / W2 - 1;
            return (id & 1) == 0
                ? new P2(r.X0 + (x + 1) * r.Res, r.Z0 + (y + 0.5) * r.Res)
                : new P2(r.X0 + (x + 0.5) * r.Res, r.Z0 + (y + 1) * r.Res);
        }

        // ------------------------------------------------------------------ Mittellinien (Verduennung)

        static readonly int[] NX = { 0, 1, 1, 1, 0, -1, -1, -1 };   // P2..P9: N, NO, O, SO, S, SW, W, NW (N = +y)
        static readonly int[] NY = { 1, 1, 0, -1, -1, -1, 0, 1 };

        /// <summary>Zhang-Suen-Verduennung auf m (in place), nur Zellen der Liste.</summary>
        internal static void Verduennen(byte[] m, int W, int H, List<int> zellen)
        {
            var weg = new List<int>();
            var p = new bool[8];
            bool geaendert = true;
            while (geaendert)
            {
                geaendert = false;
                for (int schritt = 0; schritt < 2; schritt++)
                {
                    weg.Clear();
                    foreach (int i in zellen)
                    {
                        if (m[i] == 0) continue;
                        int x = i % W, y = i / W, b = 0;
                        for (int k = 0; k < 8; k++)
                        {
                            int xx = x + NX[k], yy = y + NY[k];
                            p[k] = xx >= 0 && yy >= 0 && xx < W && yy < H && m[yy * W + xx] != 0;
                            if (p[k]) b++;
                        }
                        if (b < 2 || b > 6) continue;
                        int a = 0;
                        for (int k = 0; k < 8; k++) if (!p[k] && p[(k + 1) & 7]) a++;
                        if (a != 1) continue;
                        // p[0]=P2(N) p[2]=P4(O) p[4]=P6(S) p[6]=P8(W)
                        if (schritt == 0 ? (p[0] && p[2] && p[4]) || (p[2] && p[4] && p[6]) : (p[0] && p[2] && p[6]) || (p[0] && p[4] && p[6])) continue;
                        weg.Add(i);
                    }
                    foreach (int i in weg) m[i] = 0;
                    if (weg.Count > 0) geaendert = true;
                }
                zellen.RemoveAll(i => m[i] == 0);
            }
        }

        /// <summary>m-Nachbarn: waagerecht/senkrecht immer, schraeg nur, wenn keiner der beiden gemeinsamen geraden Nachbarn gesetzt ist
        /// (so wird eine Treppe ein Pfad und kein Dreieck).</summary>
        static int MNachbarn(byte[] m, int W, int H, int i, int[] aus)
        {
            int x = i % W, y = i / W, n = 0;
            bool G(int xx, int yy) => xx >= 0 && yy >= 0 && xx < W && yy < H && m[yy * W + xx] != 0;
            for (int k = 0; k < 8; k++)
            {
                int xx = x + NX[k], yy = y + NY[k];
                if (!G(xx, yy)) continue;
                if ((k & 1) == 1 && (G(x + NX[k], y) || G(x, y + NY[k]))) continue;
                aus[n++] = yy * W + xx;
            }
            return n;
        }

        static void Mittellinien(ScanRaster r, byte[] duenn, byte[] dick, IList<int> slotMat, IList<float> gruppenDicke, int unbekannt, double tol,
            List<Seg> aus, Zahlen z)
        {
            int W = r.W, H = r.H;
            var zellen = new List<int>();
            for (int i = 0; i < duenn.Length; i++) if (duenn[i] != 0) zellen.Add(i);
            if (zellen.Count == 0) return;
            var vorher = (byte[])duenn.Clone();
            Verduennen(duenn, W, H, zellen);
            z.Skelett = zellen.Count;
            var nb = new int[8]; var nb2 = new int[8];
            var grad = new Dictionary<int, int>(zellen.Count);
            foreach (int i in zellen) grad[i] = MNachbarn(duenn, W, H, i, nb);
            var besucht = new HashSet<long>();
            long Kante(int a, int b) => a < b ? ((long)a << 32) | (uint)b : ((long)b << 32) | (uint)a;
            var linien = new List<List<int>>();

            List<int> Gehen(int von, int erst)
            {
                var pfad = new List<int> { von, erst };
                besucht.Add(Kante(von, erst));
                int vor = von, cur = erst;
                while (grad[cur] == 2)
                {
                    int n = MNachbarn(duenn, W, H, cur, nb2), nx = -1;
                    for (int k = 0; k < n; k++) if (nb2[k] != vor && !besucht.Contains(Kante(cur, nb2[k]))) { nx = nb2[k]; break; }
                    if (nx < 0) break;
                    besucht.Add(Kante(cur, nx));
                    pfad.Add(nx); vor = cur; cur = nx;
                }
                return pfad;
            }
            foreach (int i in zellen)
            {
                if (grad[i] == 2) continue;
                int n = MNachbarn(duenn, W, H, i, nb);
                var eigene = new int[n]; Array.Copy(nb, eigene, n);
                foreach (int q in eigene) if (!besucht.Contains(Kante(i, q))) linien.Add(Gehen(i, q));
            }
            // Ringe ohne Knoten (geschlossene Raeume)
            var ringe = new List<List<int>>();
            foreach (int i in zellen)
            {
                if (grad[i] != 2) continue;
                int n = MNachbarn(duenn, W, H, i, nb);
                int q = -1;
                for (int k = 0; k < n; k++) if (!besucht.Contains(Kante(i, nb[k]))) { q = nb[k]; break; }
                if (q < 0) continue;
                var pfad = Gehen(i, q);
                if (pfad[pfad.Count - 1] != i && n == 2) { int a = pfad[pfad.Count - 1]; if (!besucht.Contains(Kante(a, i))) { besucht.Add(Kante(a, i)); } pfad.Add(i); }
                ringe.Add(pfad);
            }
            // Seitenaeste: Endpunkt -> Knoten mit Grad >= 3, hoechstens AstMax Zellen
            var pkt = new List<P2>(); var zl = new List<int>();
            foreach (var l in linien)
            {
                int ga = grad[l[0]], gb = grad[l[l.Count - 1]];
                if (l.Count - 1 <= AstMax && ((ga == 1 && gb >= 3) || (gb == 1 && ga >= 3))) { z.AesteWeg++; continue; }
                pkt.Clear(); zl.Clear();
                foreach (int c in l) { pkt.Add(Mitte(r, c)); zl.Add(c); }
                // freies Ende an einen dicken Bereich heranziehen (bis an dessen Umriss, halbe Zelle)
                Verlaengern(r, dick, vorher, l, ga, pkt, zl, true);
                Verlaengern(r, dick, vorher, l, gb, pkt, zl, false);
                z.Linien++;
                int vor = aus.Count;
                Ausgeben(r, pkt, zl, slotMat, gruppenDicke, unbekannt, tol, SegArt.Mitte, false, aus);
                z.Mitten += aus.Count - vor;
            }
            foreach (var l in ringe)
            {
                pkt.Clear(); zl.Clear();
                bool zu = l.Count > 3 && l[0] == l[l.Count - 1];
                int bis = zu ? l.Count - 1 : l.Count;
                for (int k = 0; k < bis; k++) { pkt.Add(Mitte(r, l[k])); zl.Add(l[k]); }
                z.Linien++;
                int vor = aus.Count;
                Ausgeben(r, pkt, zl, slotMat, gruppenDicke, unbekannt, tol, SegArt.Mitte, zu, aus);
                z.Mitten += aus.Count - vor;
            }
        }

        static P2 Mitte(ScanRaster r, int c) => new P2(r.X0 + (c % r.W + 0.5) * r.Res, r.Z0 + (c / r.W + 0.5) * r.Res);

        /// <summary>Freies Ende einer Mittellinie: an einen angrenzenden dicken Bereich bis an dessen Umriss heranziehen, sonst in
        /// Linienrichtung bis ans Ende der festen Zellen vor der Verduennung (die Verduennung kuerzt Enden um bis zu eine halbe Wandbreite).</summary>
        static void Verlaengern(ScanRaster r, byte[] dick, byte[] vorher, List<int> l, int grad, List<P2> pkt, List<int> zl, bool vorne)
        {
            if (grad != 1) return;
            int c = vorne ? l[0] : l[l.Count - 1];
            int x = c % r.W, y = c / r.W;
            for (int k = 0; k < 8; k++)
            {
                int xx = x + NX[k], yy = y + NY[k];
                if (xx < 0 || yy < 0 || xx >= r.W || yy >= r.H || dick[yy * r.W + xx] == 0) continue;
                var p = Mitte(r, c); var q = Mitte(r, yy * r.W + xx);
                var e = new P2((p.X + q.X) * 0.5, (p.Z + q.Z) * 0.5);
                if (vorne) { pkt.Insert(0, e); zl.Insert(0, c); } else { pkt.Add(e); zl.Add(c); }
                return;
            }
            // Richtung aus den letzten (bis zu 4) Zellen
            int back = Math.Min(4, l.Count - 1);
            if (back < 1) return;
            int c2 = vorne ? l[back] : l[l.Count - 1 - back];
            P2 pe = Mitte(r, c), pb = Mitte(r, c2);
            double dx = pe.X - pb.X, dz = pe.Z - pb.Z, len = Math.Sqrt(dx * dx + dz * dz);
            if (len < 1e-9) return;
            dx /= len; dz /= len;
            double step = 0.25 * r.Res, weit = 0;
            for (int s = 1; s <= 4 * 8; s++)
            {
                double px = pe.X + dx * step * s, pz = pe.Z + dz * step * s;
                int i = (int)Math.Floor((px - r.X0) / r.Res), j = (int)Math.Floor((pz - r.Z0) / r.Res);
                if (i < 0 || j < 0 || i >= r.W || j >= r.H || vorher[j * r.W + i] == 0) break;
                weit = step * s;
            }
            weit += 0.5 * step;   // bis in die Mitte zwischen letzter fester Abtastung und der ersten leeren
            if (weit < 0.5 * step + 1e-9 && vorher[c] != 0) weit = 0.5 * r.Res;   // Endzelle selbst: bis an ihren Rand
            var ep = new P2(pe.X + dx * weit, pe.Z + dz * weit);
            if (vorne) { pkt.Insert(0, ep); zl.Insert(0, c); } else { pkt.Add(ep); zl.Add(c); }
        }
    }
}
