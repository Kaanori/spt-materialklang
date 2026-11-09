using System;

namespace Materialklang
{
    // ------------------------------------------------------------------------------------------
    // 0.8.4 (Ausbaustufe 1 nach der Machbarkeitsstudie "True Acoustics") - alle Schallwege in echten dB, je Oktavband.
    // Reine Rechnung ohne Unity, damit der Offline-Test (Materialklang-KlangTest) sie mituebersetzen kann. Spiel-Teil: Wege.cs.
    //
    // Konvention (wie RaumwegKern): Pegel RELATIV zum Freifeld-Direktweg in der Direktlinie (die Entfernungskurve rechnet
    // Unity/SEB): dL_i = 10 lg(I_i / I_0), I_0 = W / (4 pi d^2). dL <= 0 heisst leiser als ohne Hindernis.
    // Baender: 0 = Bezugsfrequenz (Einstellung, Standard 250 Hz), 1 = 1 kHz, 2 = 4 kHz.
    //
    //  D (Beugung um eine Kante, Spiel: EdgeDiffractionCalculator, EIN Kantenpunkt): UTD (Uniform Theory of Diffraction,
    //     Kouyoumjian & Pathak 1974) in der Form von Valve Steam Audio (Utd.cs, Apache 2.0), mit dem echten Abstandsparameter
    //     L = s s'/(s+s') aus Quelle, Kantenpunkt und Hoerer. Keilwinkel: das Spiel kennt keine Flaechen -> Einstellung (90 Grad = Hausecke).
    //     Der Winkelfaktor des Spiels (energyFactor) wird NICHT zusaetzlich angewendet - der Winkel steckt in der UTD.
    //  R (Reflexion, Spiel: ReflectionCalculator, kuerzester Weg Quelle -> Treffer -> ... -> Hoerer):
    //     dL_R(f) = -20 lg(L_R/d) + Sum_k 10 lg(1 - alpha_k(f)) - n * Zusatzverlust
    //     Spiegelquellen-Naeherung (Kuttruff, Room Acoustics, Kap. 4). Ersetzt den Spielverlust 0,7 je Treffer und die lineare
    //     Abstandsdaempfung des Spiels. Zusatzverlust (Einstellung, Annahme): der letzte Abschnitt zum Hoerer ist im Spiel ein
    //     Sichttest vom Treffpunkt aus, kein Spiegelweg - die Flaeche ist endlich und streut (siehe README).
    //  Summe: E_b = Sum_i 10^(dL_i,b / 10) ueber alle Wege, dL_b = 10 lg(min(E_b, 1)). Deckel 0 dB: freie Sicht bleibt 0 dB,
    //     Reflexionen machen einen freien Klang nicht lauter (fruehe Reflexionen rendert Meta XR aus seiner Geometrie).
    // ------------------------------------------------------------------------------------------

    /// <summary>Letzter Wert eines Wegs (D oder R) zum Halten, solange das Spiel ihn nicht neu rechnet.</summary>
    internal sealed class WegSpeicher
    {
        public bool Hat, Direkt;
        public int Fehl;                                           // so viele Rechnungen in Folge gelaufen, aber nicht gefunden
        public readonly double[] Db = new double[WegeKern.N];    // dL je Band (<= 0)
        public V3 Q, H;                                            // Quelle/Hoerer bei der Messung
        public void Setzen(double[] db, V3 q, V3 h, bool direkt = false) { Hat = true; Direkt = direkt; Fehl = 0; Array.Copy(db, Db, WegeKern.N); Q = q; H = h; }
        public void Vergessen() { Hat = false; Direkt = false; Fehl = 0; }
    }

    /// <summary>0.8.5: zeitliche Ueberblendung eines Wegs (D oder R) in Energie je Band: von dem, was gerade zu hoeren ist,
    /// linear in 'dauer' Sekunden zum neuen Ziel. Gleiches Prinzip wie der Raumweg (RoutenMischer): ein Weg, der dazukommt,
    /// blendet ein, einer, der verschwindet, blendet mit seinem letzten Wert aus; springt er (Haltestufe, Hysterese, neue Kante),
    /// blendet der alte Stand aus und der neue ein (in Energie dasselbe wie die lineare Ueberblendung).</summary>
    internal sealed class WegBlende
    {
        public bool Hat;
        public double T0;
        public readonly double[] Alt = new double[WegeKern.N], Ziel = new double[WegeKern.N];
        public double Anteil(double jetzt, double dauer) => !Hat || dauer <= 0 ? 1.0 : Math.Max(0.0, Math.Min(1.0, (jetzt - T0) / dauer));
        public void Wert(double jetzt, double dauer, double[] aus)
        {
            double k = Anteil(jetzt, dauer);
            for (int b = 0; b < WegeKern.N; b++) aus[b] = Alt[b] + (Ziel[b] - Alt[b]) * k;
        }
        public void Setzen(double[] ziel, double jetzt, double dauer)
        {
            if (Hat && dauer > 0) Wert(jetzt, dauer, Alt);
            else Array.Copy(ziel, Alt, WegeKern.N);
            Array.Copy(ziel, Ziel, WegeKern.N);
            T0 = jetzt; Hat = true;
        }
        public bool Laeuft(double jetzt, double dauer)
        {
            if (!Hat || Anteil(jetzt, dauer) >= 1.0) return false;
            for (int b = 0; b < WegeKern.N; b++) if (Math.Abs(Ziel[b] - Alt[b]) > 1e-9) return true;
            return false;
        }
        public void Vergessen() { Hat = false; }
    }

    /// <summary>Ergebnis einer fertigen Rechnung (je Rechner des Spiels), Wege einzeln und als Summe.</summary>
    internal sealed class WegZustand
    {
        public bool Gueltig;            // es gibt ein Ergebnis
        public bool Frei;               // das Spiel hat "frei" gemeldet
        public bool HatT, HatD, HatR;   // Weg traegt bei (D/R auch gehalten)
        public bool DGehalten, RGehalten, DDirekt, RDirekt, RHysterese;
        public int Bester;              // 1 = T, 2 = D, 3 = R (lautester Weg im Bezugsband), 0 = keiner / frei
        public readonly double[] T = new double[WegeKern.N], D = new double[WegeKern.N], R = new double[WegeKern.N];
        public readonly double[] TWand = new double[WegeKern.N];      // T nur durch die Wand (freie Strahlen nicht mitgezaehlt)
        public double TFrei;            // Anteil der freien T-Strahlen (0..1) = geometrischer Anteil am Rand eines Hindernisses
        public readonly double[] Ergebnis = new double[WegeKern.N];   // Summe (oder bester Weg, wenn die Summe aus ist)
        public double Wertung;          // -Ergebnis[0] / Dynamik, 0..1
        public double LD, LR, Direkt;   // Weglaengen (Log)
        public int RAnzahl;             // Reflexionen des R-Wegs (Log)
        public readonly WegSpeicher DSp = new WegSpeicher(), RSp = new WegSpeicher();
        public int Rechnungen;
        // 0.8.5: zeitliche Ueberblendung von D und R (WegBlende); Jetzt = was gerade gilt (Ergebnis = Ziel der Rechnung)
        public readonly WegBlende DBl = new WegBlende(), RBl = new WegBlende();
        public readonly double[] Jetzt = new double[WegeKern.N];
        public double WertungJetzt;
        public bool Blendet;

        public void Vergessen()
        {
            Gueltig = Frei = HatT = HatD = HatR = DGehalten = RGehalten = DDirekt = RDirekt = RHysterese = false;
            Bester = 0; Wertung = 0; DSp.Vergessen(); RSp.Vergessen(); DBl.Vergessen(); RBl.Vergessen(); Blendet = false; WertungJetzt = 0;
        }
    }

    internal static class WegeKern
    {
        public const int N = 3;
        public const double C = 343.0;
        public const double KeinWeg = -1000.0;

        /// <summary>
        /// Beugung um EINE Kante nach UTD (Utd.cs, portiert aus Valve Steam Audio): dL (dB, &lt;= 0) gegenueber dem Freifeld in
        /// Direktlinie. q = Quelle, kante = Kantenpunkt (Spiel: DiffractionPathResult.diffractionPoint), h = Hoerer, n = Keilzahl.
        /// UTD (Kouyoumjian-Pathak): u_d/u_0 = |D| / sqrt(L) * d / (s + s'), L = s s' / (s + s'), Ablenkwinkel wie Steam Audio
        /// (Winkel zwischen einfallender und weiterlaufender Richtung), Einfall entlang der Keilflaeche (alpha_i = 0, wie Steam Audio).
        /// </summary>
        public static double Beugung(V3 q, V3 kante, V3 h, double f, double n = 1.5)
        {
            double s1 = Math.Max(V3.Abstand(q, kante), 0.05), s2 = Math.Max(V3.Abstand(kante, h), 0.05);
            double d = Math.Max(V3.Abstand(q, h), 0.05);
            V3 ein = (kante - q) * (1.0 / s1), aus = (h - kante) * (1.0 / s2);
            double cos = Math.Max(-1.0, Math.Min(1.0, V3.Dot(ein, aus)));
            double winkel = Math.Max(1e-6, Math.Acos(cos));
            double l = s1 * s2 / (s1 + s2);
            double dk = Utd.Koeffizient(winkel, f, n, 0.0, l, C);
            double kantenDb = Math.Min(0.0, 20.0 * Math.Log10(Math.Max(dk, 1e-12) / Math.Sqrt(l)));
            return kantenDb - 20.0 * Math.Log10(Math.Max(1.0, (s1 + s2) / d));
        }

        /// <summary>Keilzahl n aus dem Innenwinkel des Keils (Grad): 0 = duenne Kante (n = 2), 90 = Hausecke (n = 1,5).</summary>
        public static double KeilN(double innenGrad) => (360.0 - Math.Max(0.0, Math.Min(170.0, innenGrad))) / 180.0;

        /// <summary>Vergleich (vor der UTD, nur Test): Kurze-Anderson, d = Direktlinie, l = Weg ueber die Kante.</summary>
        public static double BeugungKA(double d, double l, double f)
        {
            d = Math.Max(d, 0.1);
            l = Math.Max(l, d);
            double umweg = l - d;
            return -20.0 * Math.Log10(l / d) - RaumwegKern.KurzeAnderson(RaumwegKern.Fresnel(umweg, f));
        }

        /// <summary>Maekawa (1968) als Gegenprobe: A = 10 lg(3 + 20 N), N &gt;= 0.</summary>
        public static double Maekawa(double n) => 10.0 * Math.Log10(3.0 + 20.0 * Math.Max(0.0, n));

        /// <summary>Reflexionsweg: dL (dB) bei Weglaenge l, n Reflexionen mit alpha[k] (dieses Band), Zusatzverlust je Reflexion (dB).</summary>
        public static double Reflexion(double d, double l, double[] alpha, int n, double zusatzDb)
        {
            d = Math.Max(d, 0.1);
            l = Math.Max(l, d);
            double db = -20.0 * Math.Log10(l / d);
            for (int k = 0; k < n; k++)
            {
                double a = alpha != null && k < alpha.Length ? alpha[k] : 0.1;
                a = a < 0 ? 0 : (a > 0.999 ? 0.999 : a);
                db += 10.0 * Math.Log10(1.0 - a) - zusatzDb;
            }
            return db;
        }

        public static double Energie(double db) => db <= KeinWeg ? 0.0 : Math.Pow(10.0, db / 10.0);
        public static double DbAus(double e, double minDb) => e <= 0 ? minDb : Math.Max(minDb, 10.0 * Math.Log10(e));

        /// <summary>Energiesumme je Band, gedeckelt bei 0 dB, nach unten bei -maxDb. Keiner da -> -maxDb.</summary>
        public static void Summe(double[] t, bool hatT, double[] d, bool hatD, double[] r, bool hatR, double maxDb, double[] aus)
        {
            for (int b = 0; b < N; b++)
            {
                double e = 0;
                if (hatT) e += Energie(t[b]);
                if (hatD) e += Energie(d[b]);
                if (hatR) e += Energie(r[b]);
                aus[b] = Math.Min(0.0, DbAus(e, -maxDb));
            }
        }

        /// <summary>
        /// Energiesumme mit getrenntem T: E = E(T durch die Wand) + G + E(R), G = max(Anteil freier T-Strahlen, E(D)).
        /// Freie T-Strahlen und der Beugungsweg beschreiben am Rand eines Hindernisses DIESELBE Energie (Schall, der an der Kante
        /// vorbeikommt) - sie werden darum nicht addiert, sondern der groessere zaehlt. Meldet D "Sichtlinie frei", gilt 0 dB
        /// (die UTD hat an der Schattengrenze 0 dB, so geht der Pegel stetig in den Schatten ueber).
        /// </summary>
        public static void SummeGetrennt(double[] tWand, double tFrei, bool hatT, double[] d, bool hatD, bool dDirekt,
                                         double[] r, bool hatR, bool rDirekt, double maxDb, double[] aus)
        {
            for (int b = 0; b < N; b++)
            {
                double g = hatT ? tFrei : 0;
                if (hatD) g = dDirekt ? 1.0 : Math.Max(g, Energie(d[b]));   // UTD ist an der Schattengrenze 0 dB -> stetig zur Sichtlinie
                double e = g;
                if (hatT) e += Energie(tWand[b]);
                if (hatR && !rDirekt) e += Energie(r[b]);
                else if (hatR && !hatT) e = Math.Max(e, 1.0);
                aus[b] = Math.Min(0.0, DbAus(e, -maxDb));
            }
        }

        /// <summary>Haltefaktor eines gemerkten Wegs nach Bewegung (m) von Quelle/Hoerer: 1 bis 'nah', 0 ab 'fern', dazwischen linear.</summary>
        public static double Haltefaktor(double bewegung, double nah, double fern)
        {
            if (bewegung <= nah) return 1.0;
            if (bewegung >= fern || fern <= nah) return 0.0;
            return 1.0 - (bewegung - nah) / (fern - nah);
        }

        /// <summary>
        /// Eine fertige Rechnung auswerten (alle Werte als dL &lt;= 0 je Band).
        /// frei = das Spiel meldet "frei" (Direkttest) -> alles 0 dB.
        /// T: tWand = nur die Energie durch Waende (freie Strahlen = 0), tFrei = Anteil freier Strahlen.
        /// D: ranD/dDirekt (Sichtlinie frei = 0 dB)/dHat (Kante gefunden)/d; R: ranR/rDirekt (Direktweg frei)/rHat/r.
        /// Nicht gelaufene Wege: gemerkter Wert, abgeblendet mit der Bewegung (haltNah..haltFern m). Gelaufen, aber nichts gefunden:
        /// der gemerkte Wert klingt um 'abklingDb' je Rechnung ab (Monte-Carlo-Strahlen finden den Weg nicht jedes Mal).
        /// R-Hysterese: neuer R-Wert wird erst uebernommen, wenn er sich um mehr als 'hystDb' (Bezugsband) aendert.
        /// summe = Energiesumme (sonst: lautester Weg im Bezugsband, wie das Minimum der Wertungen).
        /// </summary>
        public static void Auswerten(WegZustand z, bool frei,
            bool ranT, double[] tWand, double tFrei,
            bool ranD, bool dDirekt, bool dHat, double[] d,
            bool ranR, bool rDirekt, bool rHat, double[] r,
            V3 q, V3 h, double maxDb, bool summe,
            double haltNah = 1.0, double haltFern = 3.0, double abklingDb = 3.0, double hystDb = 1.0, double[] freiT = null)
        {
            z.Gueltig = true; z.Rechnungen++;
            z.Frei = frei;
            z.DGehalten = z.RGehalten = z.DDirekt = z.RDirekt = z.RHysterese = false;
            if (frei)
            {
                // Spiel: Direkttest T <= Freiweg -> "frei". Statt hart 0 dB gilt der (kleine) Wert des Direkttests, sonst springt der Pegel
                // an der Freiweg-Schwelle um bis zu 1,2 dB (Grenzfall-Test). freiT = null (ausser Reichweite) -> 0 dB.
                for (int b = 0; b < N; b++) { z.T[b] = z.Ergebnis[b] = freiT != null ? Math.Max(-maxDb, Math.Min(0, freiT[b])) : 0; z.D[b] = z.R[b] = 0; z.TWand[b] = KeinWeg; }
                z.TFrei = 1; z.HatT = true; z.HatD = z.HatR = false; z.Bester = 0;
                z.Wertung = Math.Max(0, Math.Min(1, -z.Ergebnis[0] / maxDb));
                z.DSp.Vergessen(); z.RSp.Vergessen();
                return;
            }
            z.HatT = ranT;
            z.TFrei = ranT ? Math.Max(0, Math.Min(1, tFrei)) : 0;
            for (int b = 0; b < N; b++)
            {
                z.TWand[b] = ranT ? Math.Min(0, tWand[b]) : KeinWeg;
                z.T[b] = ranT ? Math.Max(-maxDb, Math.Min(0, DbAus(z.TFrei + Energie(z.TWand[b]), -maxDb))) : KeinWeg;
            }

            z.HatD = Weg(z.DSp, z.D, ranD, dDirekt, dHat, d, q, h, maxDb, haltNah, haltFern, abklingDb, 0, out z.DGehalten, out _);
            z.DDirekt = z.HatD && z.DSp.Direkt;
            z.HatR = Weg(z.RSp, z.R, ranR, rDirekt, rHat, r, q, h, maxDb, haltNah, haltFern, abklingDb, hystDb, out z.RGehalten, out z.RHysterese);
            z.RDirekt = z.HatR && z.RSp.Direkt;

            // lautester Weg (Bezugsband) - fuer das Log und fuer "Summe aus"
            z.Bester = 0; double best = double.NegativeInfinity;
            if (z.HatT && z.T[0] > best) { best = z.T[0]; z.Bester = 1; }
            if (z.HatD && z.D[0] > best) { best = z.D[0]; z.Bester = 2; }
            if (z.HatR && z.R[0] > best) { best = z.R[0]; z.Bester = 3; }

            if (summe) SummeGetrennt(z.TWand, z.TFrei, z.HatT, z.D, z.HatD, z.DDirekt, z.R, z.HatR, z.RDirekt, maxDb, z.Ergebnis);
            else
            {
                double[] src = z.Bester == 1 ? z.T : z.Bester == 2 ? z.D : z.Bester == 3 ? z.R : null;
                for (int b = 0; b < N; b++) z.Ergebnis[b] = src != null ? Math.Max(-maxDb, Math.Min(0, src[b])) : -maxDb;
            }
            z.Wertung = Math.Max(0, Math.Min(1, -z.Ergebnis[0] / maxDb));
        }

        static readonly double[] eD = new double[N], eR = new double[N], wD = new double[N], wR = new double[N];

        /// <summary>0.8.5: Nach Auswerten (nur mit Energiesumme): Ziele der D/R-Ueberblendung setzen. "frei" gilt sofort
        /// (der Uebergang an der Freiweg-Schwelle ist schon stetig, siehe Auswerten).</summary>
        public static void BlendeSetzen(WegZustand z, double jetzt, double dauer)
        {
            if (z.Frei || !z.Gueltig) { z.DBl.Vergessen(); z.RBl.Vergessen(); return; }
            for (int b = 0; b < N; b++)
            {
                eD[b] = z.HatD ? (z.DDirekt ? 1.0 : Energie(z.D[b])) : 0.0;
                eR[b] = z.HatR && !z.RDirekt ? Energie(z.R[b]) : 0.0;
            }
            z.DBl.Setzen(eD, jetzt, dauer);
            z.RBl.Setzen(eR, jetzt, dauer);
        }

        /// <summary>0.8.5: was zur Zeit 'jetzt' gilt (z.Jetzt, z.WertungJetzt): T sofort, D und R uebergeblendet, sonst wie
        /// SummeGetrennt. Am Ende der Ueberblendung genau z.Ergebnis.</summary>
        public static void Mischen(WegZustand z, double jetzt, double dauer, double maxDb)
        {
            if (z.Frei || !z.DBl.Hat)
            {
                Array.Copy(z.Ergebnis, z.Jetzt, N); z.WertungJetzt = z.Wertung; z.Blendet = false;
                return;
            }
            z.DBl.Wert(jetzt, dauer, wD); z.RBl.Wert(jetzt, dauer, wR);
            for (int b = 0; b < N; b++)
            {
                double g = z.HatT ? z.TFrei : 0;
                g = Math.Max(g, wD[b]);
                double e = g + wR[b];
                if (z.HatT) e += Energie(z.TWand[b]);
                if (z.HatR && z.RDirekt && !z.HatT) e = Math.Max(e, 1.0);
                z.Jetzt[b] = Math.Min(0.0, DbAus(e, -maxDb));
            }
            z.WertungJetzt = Math.Max(0, Math.Min(1, -z.Jetzt[0] / maxDb));
            z.Blendet = z.DBl.Laeuft(jetzt, dauer) || z.RBl.Laeuft(jetzt, dauer);
        }

        static bool Weg(WegSpeicher sp, double[] ziel, bool ran, bool direkt, bool hat, double[] neu, V3 q, V3 h, double maxDb,
            double haltNah, double haltFern, double abklingDb, double hystDb, out bool gehalten, out bool hysterese)
        {
            gehalten = hysterese = false;
            if (ran && direkt)
            {
                for (int b = 0; b < N; b++) ziel[b] = 0;
                sp.Setzen(ziel, q, h, true);
                return true;
            }
            if (ran && hat)
            {
                if (hystDb > 0 && sp.Hat && !sp.Direkt && sp.Fehl == 0 && Math.Abs(neu[0] - sp.Db[0]) <= hystDb)
                {
                    hysterese = true;
                    Array.Copy(sp.Db, ziel, N);
                    return true;
                }
                for (int b = 0; b < N; b++) ziel[b] = Math.Max(-maxDb, Math.Min(0, neu[b]));
                sp.Setzen(ziel, q, h);
                return true;
            }
            if (!sp.Hat) { for (int b = 0; b < N; b++) ziel[b] = KeinWeg; return false; }
            if (sp.Direkt) { sp.Vergessen(); for (int b = 0; b < N; b++) ziel[b] = KeinWeg; return false; }   // "frei" nie halten
            if (ran)
            {
                // gelaufen, nichts gefunden: einmal halten (Monte-Carlo-Strahlen treffen nicht jedes Mal), danach abklingen
                sp.Fehl++;
                if (sp.Fehl > 1) for (int b = 0; b < N; b++) sp.Db[b] -= abklingDb;
                if (sp.Db[0] < -maxDb) { sp.Vergessen(); for (int b = 0; b < N; b++) ziel[b] = KeinWeg; return false; }
                gehalten = true;
                Array.Copy(sp.Db, ziel, N);
                return true;
            }
            // nicht gelaufen (niedrige Prioritaet / Spielschwelle): gemerkter Wert, mit der Bewegung abgeblendet
            double bew = Math.Max(V3.Abstand(q, sp.Q), V3.Abstand(h, sp.H));
            double k = Haltefaktor(bew, haltNah, haltFern);
            if (k <= 0) { sp.Vergessen(); for (int b = 0; b < N; b++) ziel[b] = KeinWeg; return false; }
            gehalten = true;
            double kDb = 10.0 * Math.Log10(k);
            for (int b = 0; b < N; b++) ziel[b] = sp.Db[b] + kDb;
            return true;
        }

        // ---------------------------------------------------------------- Filter aus der Bandkurve

        /// <summary>Mehrdaempfung (dB) eines 2-poligen Tiefpasses mit Grenzfrequenz fc bei f (|H|^2 = 1/(1+(f/fc)^4)).</summary>
        public static double MehrDb(double fc, double f) => 10.0 * Math.Log10(1.0 + Math.Pow(f / Math.Max(fc, 1.0), 4));

        /// <summary>
        /// Tiefpass-Fit aus der Bandkurve: Mehrdaempfung gegenueber dem Bezugsband bei f1 (1 kHz) und f2 (4 kHz).
        /// Gesucht ist fc, das den quadratischen Fehler ueber beide Punkte minimiert (2-poliger Filter des Spiels).
        /// m1/m2 &lt;= 0,05 dB -> offen (fMax). Ergebnis zwischen fMin und fMax.
        /// </summary>
        public static double TiefpassFit(double m1, double m2, double f1, double f2, double fMin, double fMax = 22000)
        {
            m1 = Math.Max(0, m1); m2 = Math.Max(0, m2);
            if (m1 <= 0.05 && m2 <= 0.05) return fMax;
            double lo = Math.Log(fMin), hi = Math.Log(fMax);
            // Fehlerfunktion ist in log fc unimodal: goldener Schnitt
            double g = (Math.Sqrt(5) - 1) / 2, a = lo, b = hi;
            double c = b - g * (b - a), dd = a + g * (b - a);
            double fc_ = Fehler(Math.Exp(c), m1, m2, f1, f2), fd = Fehler(Math.Exp(dd), m1, m2, f1, f2);
            for (int i = 0; i < 40; i++)
            {
                if (fc_ < fd) { b = dd; dd = c; fd = fc_; c = b - g * (b - a); fc_ = Fehler(Math.Exp(c), m1, m2, f1, f2); }
                else { a = c; c = dd; fc_ = fd; dd = a + g * (b - a); fd = Fehler(Math.Exp(dd), m1, m2, f1, f2); }
            }
            return Math.Max(fMin, Math.Min(fMax, Math.Exp((a + b) / 2)));
        }

        static double Fehler(double fc, double m1, double m2, double f1, double f2)
        {
            double e1 = MehrDb(fc, f1) - m1, e2 = MehrDb(fc, f2) - m2;
            return e1 * e1 + e2 * e2;
        }

        // ---------------------------------------------------------------- Empfangsraum (EN ISO 12354-1, vereinfacht)

        /// <summary>
        /// Korrektur (dB, + = lauter) des Wegs durch eine Wand fuer den Raum, in dem der Hoerer steht, gegenueber "Freifeld durch eine
        /// unendliche Wand" (dL = -R). Quelle als Punktquelle, Wandabstand r1, Hoerer-Wandabstand r2, Wandflaeche s, d = Direktlinie.
        /// Auf die Wand: I = 1/(4 pi r1^2), durch: P = I s tau. Am Hoerer: Direktabstrahlung P/(2 pi r2^2) (Halbraum, nie mehr als die
        /// unendliche Wand) + diffus 4P/A2 (Kuttruff), Diffusanteil mit 'kDiffus' gewichtet (Meta XR hallt evtl. selbst im Hoererraum).
        /// a2 &lt;= 0 = Hoerer im Freien (nur Direktabstrahlung). Ergebnis auf +-maxDb begrenzt.
        /// </summary>
        public static double Empfangsraum(double s, double r1, double r2, double d, double a2, double kDiffus, double maxDb)
        {
            s = Math.Max(s, 0.1); r1 = Math.Max(r1, 0.25); r2 = Math.Max(r2, 0.25); d = Math.Max(d, r1 + r2);
            double direkt = Math.Min(1.0, s * d * d / (r1 * r1 * 2 * Math.PI * r2 * r2));
            double diffus = a2 > 0 ? kDiffus * 4.0 * s * d * d / (r1 * r1 * Math.Max(a2, RaumwegKern.MinFlaecheA)) : 0;
            double db = 10.0 * Math.Log10(direkt + diffus);
            return Math.Max(-maxDb, Math.Min(maxDb, db));
        }

        // ---------------------------------------------------------------- Flankenuebertragung (EN ISO 12354-1, 4.4 + Anhang E)

        /// <summary>Stossstellendaemm-Mass starrer Kreuzstoss, gerader Weg (K_13). M = lg(m'_senkrecht / m'_i).</summary>
        public static double K13(double m) => 8.7 + 17.1 * m + 5.7 * m * m;
        /// <summary>Starrer Kreuzstoss, Weg um die Ecke (K_12).</summary>
        public static double K12(double m) => 8.7 + 5.7 * m * m;

        /// <summary>
        /// Bau-Schalldaemm-Mass R' aus Direktweg R_D und vier Stossstellen (Boden, Decke, zwei Seitenwaende) mit je drei Flankenwegen
        /// (Ff, Fd, Df). rF/mF je Stossstelle (Flankenbauteil: R in dB, m' in kg/m2), lf = Stossstellenlaenge (m), sS = Trennwandflaeche.
        /// R_ij = (R_i + R_j)/2 + K_ij + 10 lg(S_s / (l0 l_f)), l0 = 1 m. R' = -10 lg(10^(-R_D/10) + Sum 10^(-R_ij/10)).
        /// </summary>
        public static double Flanke(double rD, double mD, double sS, double[] rF, double[] mF, double[] lf, int n)
        {
            mD = Math.Max(mD, 0.1); sS = Math.Max(sS, 0.5);
            double e = Math.Pow(10, -rD / 10);
            for (int j = 0; j < n; j++)
            {
                double mf = Math.Max(mF[j], 0.1);
                double flaeche = 10.0 * Math.Log10(sS / Math.Max(lf[j], 0.3));
                double rff = rF[j] + K13(Math.Log10(mD / mf)) + flaeche;
                double rfd = (rF[j] + rD) / 2 + K12(Math.Log10(mD / mf)) + flaeche;
                e += Math.Pow(10, -rff / 10) + 2 * Math.Pow(10, -rfd / 10);
            }
            return -10.0 * Math.Log10(e);
        }
    }
}
