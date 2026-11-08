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
    internal struct WandStueck { public int Id; public float Dicke; public bool Einzeln; }
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
                        if (nStuecke < stuecke.Length) stuecke[nStuecke++] = new WandStueck { Id = id, Dicke = ev[j].T - start, Einzeln = false };
                        offen = false;
                    }
                    else { start = ev[j].T; offen = true; }
                }
                if (offen && nStuecke < stuecke.Length) stuecke[nStuecke++] = new WandStueck { Id = id, Dicke = 0f, Einzeln = true };
            }
            return luecke ? LinienBefund.Luecke : LinienBefund.Gemessen;
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
