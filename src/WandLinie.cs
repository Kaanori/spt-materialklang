using System;

namespace Materialklang
{
    // 0.8.3 - Auswertung EINER Messlinie der Wand zwischen Raeumen (WandZwischenRaeumen.cs) als reine Rechnung ohne Unity,
    // damit der Offline-Test (Materialklang-KlangTest) sie mituebersetzen kann.
    //
    // Je Linie laufen zwei Strahlen (A -> B und B -> A, je hoechstens 8 Treffer, Rueckseiten zaehlen mit). Bis 0.8.2:
    //  - Hatte EIN Strahl 8 Treffer ("voll"), galt die Linie als dicht. Im Sandbox-Log waren das 201 von 230 Linien:
    //    die Wand konnte praktisch nie gewinnen ("wall won 0x of 46"). Ein voller Strahl sagt aber nur, dass DIESER Strahl
    //    nicht bis zum Ende gesehen hat - der Gegenstrahl kann den Rest abdecken. Unbekannt ist nur eine LUECKE: beide Strahlen
    //    voll und der weiteste Treffer von A liegt vor dem weitesten von B aus.
    //  - Die vier um 0,25 m versetzten Linien beginnen oft im Boden oder hinter der Wand, an der die Quelle steht (Fuesse auf
    //    dem Pflaster, Ohr an der Mauer). Sie treffen dann eine Flaeche, die zwischen dem echten Klang und dem Ohr gar nicht liegt.
    // Jetzt:
    //  - Luecke -> die Linie ist unbekannt; nur wenn schon das Bekannte die Hoechstdaemmung erreicht, gilt sie (sicher) als dicht.
    //  - Versetzte Linie mit einer Flaeche naeher als NahGrenze an einem Ende, die die Mittellinie dort nicht hat -> Linie verworfen
    //    (ihr Ende liegt in/hinter Geometrie, die den echten Klang nicht verdeckt). Die uebrigen Linien zaehlen (Gewichte neu verteilt).
    //  - Flaechen ohne Gegenstueck (einseitige Meshes) bekommen die Dicke ihrer Gruppe (Bauakustik.EinzelDicke) statt immer 0,15 m.
    internal struct WandTreffer { public float T; public int Id; }   // T = Abstand vom Linienanfang A (m), auch fuer den Gegenstrahl
    internal struct WandStueck { public int Id; public float Dicke; public bool Einzeln; public float Start; }   // Start = Abstand von A (m), 0.8.4
    // 0.8.4: Lage eines Wandstuecks auf der Linie (A..B in m ab Linienanfang; Einzelflaeche: A = B = Trefferpunkt) und Ergebnis-Schicht.
    internal struct WandLage { public float A, B; public bool Einzeln; public int G; public int Id; }
    internal struct WandSchicht { public int G; public float Dicke; public int Id; public int Teile; }
    internal enum LinienBefund { Gemessen, EndeVerdeckt, Luecke }

    internal static class WandLinie
    {
        /// <summary>Flaechen naeher als das (m) an einem Ende einer VERSETZTEN Linie: Kreis 0,25 m + 5 cm.</summary>
        internal const float NahGrenze = 0.3f;
        const float Gleich = 0.01f;     // dieselbe Flaeche aus beiden Richtungen

        const int Max = 32;
        static readonly WandTreffer[] ev = new WandTreffer[Max];
        static readonly bool[] benutzt = new bool[Max];

        /// <summary>Hat eine Linie eine Flaeche naeher als NahGrenze an A bzw. B?</summary>
        internal static void NahAnEnden(WandTreffer[] vor, int nVor, WandTreffer[] rueck, int nRueck, float len, out bool nahA, out bool nahB)
        {
            nahA = nahB = false;
            for (int i = 0; i < nVor; i++) { if (vor[i].T < NahGrenze) nahA = true; if (len - vor[i].T < NahGrenze) nahB = true; }
            for (int i = 0; i < nRueck; i++) { if (rueck[i].T < NahGrenze) nahA = true; if (len - rueck[i].T < NahGrenze) nahB = true; }
        }

        /// <summary>
        /// Wertet eine Linie aus. vor = Treffer A -> B, rueck = Treffer B -> A (T schon von A gemessen); vorVoll/rueckVoll = der Strahl
        /// hat die Trefferhoechstzahl erreicht. versetzt = nicht die Mittellinie; mitteNahA/B = die Mittellinie hat selbst eine Flaeche
        /// nahe A bzw. B. Ergebnis: Wandstuecke (gepaarte Ein-/Austritte mit Dicke, sonst Einzelflaechen). Bei Luecke stehen die
        /// BEKANNTEN Stuecke drin (Untergrenze der Daemmung), bei EndeVerdeckt keine.
        /// </summary>
        internal static LinienBefund Auswerten(WandTreffer[] vor, int nVor, bool vorVoll, WandTreffer[] rueck, int nRueck, bool rueckVoll,
            float len, bool versetzt, bool mitteNahA, bool mitteNahB, WandStueck[] stuecke, out int nStuecke)
        {
            nStuecke = 0;
            if (versetzt)
            {
                NahAnEnden(vor, nVor, rueck, nRueck, len, out bool nahA, out bool nahB);
                if ((nahA && !mitteNahA) || (nahB && !mitteNahB)) return LinienBefund.EndeVerdeckt;
            }

            bool luecke = false;
            if (vorVoll && rueckVoll)
            {
                float weitVor = 0f, weitRueck = len;
                for (int i = 0; i < nVor; i++) weitVor = Math.Max(weitVor, vor[i].T);
                for (int i = 0; i < nRueck; i++) weitRueck = Math.Min(weitRueck, rueck[i].T);
                luecke = weitVor < weitRueck - Gleich;
            }

            // zusammenfuehren und nach Entfernung sortieren
            int n = 0;
            for (int i = 0; i < nVor && n < Max; i++) ev[n++] = vor[i];
            for (int i = 0; i < nRueck && n < Max; i++) ev[n++] = rueck[i];
            for (int i = 1; i < n; i++)
            {
                var x = ev[i]; int j = i - 1;
                while (j >= 0 && ev[j].T > x.T) { ev[j + 1] = ev[j]; j--; }
                ev[j + 1] = x;
            }
            // dieselbe Flaeche aus beiden Richtungen gesehen -> einmal zaehlen
            int m = 0;
            for (int i = 0; i < n; i++)
            {
                bool doppelt = false;
                for (int j = m - 1; j >= 0 && ev[i].T - ev[j].T < Gleich; j--)
                    if (ev[j].Id == ev[i].Id) { doppelt = true; break; }
                if (!doppelt) ev[m++] = ev[i];
            }

            // je Collider Ein-/Austritt paaren -> Wandstuecke
            Array.Clear(benutzt, 0, m);
            for (int i = 0; i < m; i++)
            {
                if (benutzt[i]) continue;
                benutzt[i] = true;
                int id = ev[i].Id;
                float start = ev[i].T;
                bool offen = true;
                for (int j = i + 1; j < m; j++)
                {
                    if (benutzt[j] || ev[j].Id != id) continue;
                    benutzt[j] = true;
                    if (offen)
                    {
                        if (nStuecke < stuecke.Length) stuecke[nStuecke++] = new WandStueck { Id = id, Dicke = ev[j].T - start, Einzeln = false, Start = start };
                        offen = false;
                    }
                    else { start = ev[j].T; offen = true; }
                }
                if (offen && nStuecke < stuecke.Length) stuecke[nStuecke++] = new WandStueck { Id = id, Dicke = 0f, Einzeln = true, Start = start };
            }
            return luecke ? LinienBefund.Luecke : LinienBefund.Gemessen;
        }

        // ---------------------------------------------------------------- 0.8.4: Schichten statt Einzelstuecke
        // Meldung "roboterhaft" (Labs, Toiletten -> Aufenthaltsraum, 6 m): bis 0.8.3 zaehlte JEDES Stueck mit seiner eigenen
        // Massengesetz-Daemmung, die dB wurden addiert. Im Log standen so fuer eine Trennwand z. B.
        //   Beton 0,15 m 42,6 dB (Einzelflaeche Toilettenraum) + Beton 0,15 m 42,6 dB (Einzelflaeche Flur) + 2x Kunststoff 1 cm je 8,9 dB
        //   Beton 0,66 m 60 dB + "Kunststoff" 0,66 m 41 dB aus demselben Raum-Mesh (gleiche Spanne = ineinander liegende Collider)
        // -> 100+ dB, gedeckelt auf 60 dB (Vollverdeckung) bei 5-16 m. Physikalisch falsch:
        //  - Lagen, die sich BERUEHREN (Verkleidung auf Beton), schwingen als eine Platte: es zaehlt die Summe der Flaechenmassen
        //    (Massengesetz m'' = Summe rho*d), nicht die Summe der dB. 1 cm Kunststoff auf 15 cm Beton bringt +0,1 dB, nicht +8,9 dB.
        //  - Ineinander liegende Collider belegen denselben Raum: jeder Abschnitt zaehlt einmal, mit dem dichtesten Material.
        //  - Zwei Einzelflaechen einer Schalen-Gruppe (Beton, Erde, ...) aus VERSCHIEDENEN Meshes, naeher als "Solid up to", sind die
        //    beiden Seiten EINER Wand (jedes Raum-Mesh traegt nur seine Innenseite): eine Wand mit Dicke = Abstand (mind. Schalendicke),
        //    nicht zwei Schalen.
        // Getrennte Schichten (Luft dazwischen) werden weiter je Schicht gerechnet und addiert (Doppelwand, Raum dazwischen).
        // Quelle Massengesetz/Verbundplatte: Bies, Hansen & Howard, "Engineering Noise Control" (5. Aufl.), Kap. 7 (einschalige Wand:
        // Gesamtflaechenmasse). Die Kontaktgrenze 3 cm ist eine ANNAHME (Fugen/Kleber; keine Messung).
        internal const float Kontakt = 0.03f;
        const int MaxLagen = 64;
        static readonly WandLage[] lg = new WandLage[MaxLagen];
        static readonly bool[] weg = new bool[MaxLagen];
        static readonly float[] punkte = new float[2 * MaxLagen];
        static readonly float[] masseG = new float[32];
        static readonly float[] letzteEinzel = new float[32];

        /// <summary>
        /// Fasst die Wandstuecke einer Linie zu Schichten zusammen (siehe oben). dichte/einzel/massiv je Gruppe (kg/m3, m, m);
        /// schalenGruppe[g] = eine Einzelflaeche steht fuer eine Wandschale (nicht fuer ein duennes Teil). Ergebnis: Schichten mit
        /// massgebender Gruppe (groesste Flaechenmasse) und gleichwertiger Dicke = Gesamtmasse / Dichte dieser Gruppe.
        /// </summary>
        internal static int Schichten(WandLage[] ein, int n, float[] dichte, float[] einzel, float[] massiv, bool[] schalenGruppe, WandSchicht[] aus)
        {
            if (n > MaxLagen) n = MaxLagen;
            for (int i = 0; i < n; i++) { lg[i] = ein[i]; weg[i] = false; }
            for (int i = 1; i < n; i++)
            {
                var x = lg[i]; int j = i - 1;
                while (j >= 0 && lg[j].A > x.A) { lg[j + 1] = lg[j]; j--; }
                lg[j + 1] = x;
            }
            // 1) Zwei Seiten einer Wand: Einzelflaechen derselben Schalen-Gruppe, verschiedene Meshes, Abstand <= massiv
            for (int i = 0; i < n; i++)
            {
                if (weg[i] || !lg[i].Einzeln) continue;
                int g = lg[i].G;
                if (g < 0 || g >= dichte.Length || !schalenGruppe[g]) continue;
                for (int j = i + 1; j < n; j++)
                {
                    if (lg[j].A - lg[i].A > massiv[g]) break;
                    if (weg[j] || !lg[j].Einzeln || lg[j].G != g || lg[j].Id == lg[i].Id) continue;
                    lg[i].B = Math.Max(lg[j].A, lg[i].A + einzel[g]);
                    lg[i].Einzeln = false;
                    weg[j] = true;
                    break;
                }
            }
            // 2) Cluster aus sich beruehrenden Stuecken
            int k = 0, s = 0;
            while (s < n)
            {
                if (weg[s]) { s++; continue; }
                float ende = lg[s].B;
                int e = s + 1;
                while (e < n && (weg[e] || lg[e].A <= ende + Kontakt)) { if (!weg[e]) ende = Math.Max(ende, lg[e].B); e++; }
                if (k < aus.Length && Buendeln(s, e, dichte, einzel, out var sch)) aus[k++] = sch;
                s = e;
            }
            return k;
        }

        // Flaechenmasse eines Clusters lg[s..e): Spannen ueber Abschnitte (dichtestes Material je Abschnitt), Einzelflaechen dazu,
        // ausser sie liegen in einer Spanne oder auf einer Spanne derselben Gruppe bzw. doppeln eine schon gezaehlte Flaeche.
        static bool Buendeln(int s, int e, float[] dichte, float[] einzel, out WandSchicht sch)
        {
            sch = default;
            int ng = Math.Min(dichte.Length, masseG.Length);
            for (int g = 0; g < ng; g++) { masseG[g] = 0f; letzteEinzel[g] = float.NegativeInfinity; }
            int np = 0, teile = 0;
            for (int i = s; i < e; i++)
            {
                if (weg[i]) continue;
                teile++;
                if (!lg[i].Einzeln) { punkte[np++] = lg[i].A; punkte[np++] = lg[i].B; }
            }
            Array.Sort(punkte, 0, np);
            for (int p = 0; p + 1 < np; p++)
            {
                float x0 = punkte[p], x1 = punkte[p + 1];
                if (x1 - x0 <= 1e-5f) continue;
                float mitte = 0.5f * (x0 + x1), rho = 0f; int gBest = -1;
                for (int i = s; i < e; i++)
                {
                    if (weg[i] || lg[i].Einzeln || lg[i].A > mitte || lg[i].B < mitte) continue;
                    int g = lg[i].G;
                    if (g < 0 || g >= ng) continue;
                    if (gBest < 0 || dichte[g] > rho) { rho = dichte[g]; gBest = g; }
                }
                if (gBest >= 0) masseG[gBest] += rho * (x1 - x0);
            }
            for (int i = s; i < e; i++)
            {
                if (weg[i] || !lg[i].Einzeln) continue;
                int g = lg[i].G;
                if (g < 0 || g >= ng) continue;
                float t = lg[i].A;
                bool drin = false;
                for (int j = s; j < e && !drin; j++)
                {
                    if (weg[j] || lg[j].Einzeln) continue;
                    if (t > lg[j].A && t < lg[j].B) drin = true;                                              // innere Grenzflaeche
                    else if (lg[j].G == g && t >= lg[j].A - Kontakt && t <= lg[j].B + Kontakt) drin = true;     // Oberflaeche desselben Stuecks
                }
                if (drin || t - letzteEinzel[g] <= Kontakt) continue;                                          // doppelt modellierte Flaeche
                letzteEinzel[g] = t;
                masseG[g] += dichte[g] * einzel[g];
            }
            int gd = -1; float summe = 0f;
            for (int g = 0; g < ng; g++) { summe += masseG[g]; if (masseG[g] > 0f && (gd < 0 || masseG[g] > masseG[gd])) gd = g; }
            if (gd < 0 || summe <= 0f || dichte[gd] <= 0f) return false;
            int id = 0; float best = -1f;
            for (int i = s; i < e; i++)
            {
                if (weg[i] || lg[i].G != gd) continue;
                float d = lg[i].Einzeln ? einzel[gd] : lg[i].B - lg[i].A;
                if (d > best) { best = d; id = lg[i].Id; }
            }
            sch = new WandSchicht { G = gd, Dicke = summe / dichte[gd], Id = id, Teile = teile };
            return true;
        }

        /// <summary>Gewichtetes Mittel der Restenergien; Linien mit energie &lt; 0 (keine Aussage) fallen heraus.
        /// -1, wenn keine Linie etwas aussagt.</summary>
        internal static float Mitteln(float[] energie, float[] gewicht, int n)
        {
            double s = 0, g = 0;
            for (int i = 0; i < n; i++) if (energie[i] >= 0f) { s += gewicht[i] * energie[i]; g += gewicht[i]; }
            return g > 0 ? (float)(s / g) : -1f;
        }
    }
}
