// ---------------------------------------------------------------
//  Kern des Klang-Logs und der Live-Einstellung: reine Logik OHNE Unity-,
//  BepInEx- oder Spiel-Abhaengigkeit. Dadurch laeuft sie auch im Offline-Test
//  (Materialklang-KlangTest) ohne Spiel.
//   - MiniJson          : liest ein flaches Objekt { "Sektion/Schluessel": Wert, ... }
//   - LiveSchluessel    : "Sektion/Schluessel" -> (Sektion, Schluessel), auch wenn der Schluessel selbst "/" enthaelt
//   - WertWandler       : JSON-Wert -> Zeichenkette fuer ConfigEntry.SetSerializedValue
//   - RateLimiter       : hoechstens N Zeilen je Quelle und Sekunde, dazu eine Gesamtgrenze
//   - LogEintrag        : eine Zeile (wiederverwendbar, keine Neuanlage je Klang)
//   - TransInfo         : Hindernisse einer Durchdringungsrechnung (feste Felder)
//   - ZeilenFormat      : LogEintrag -> Textzeile
//   - LogRotation       : welche alten Raid-Dateien geloescht werden
//   - KlangSchreiber    : Hintergrund-Faden, der Zeilen formatiert und in die Datei schreibt
// ---------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;

namespace Materialklang
{
    // ------------------------------------------------------------------ JSON

    internal enum JsonArt { Zahl, Text, Bool, Null, Anders }

    internal struct JsonEintrag
    {
        public string Schluessel;
        public JsonArt Art;
        public string Roh;      // Zahl: Originaltext; Text: entschluesselt; Bool: "true"/"false"
    }

    /// <summary>Winziger Leser fuer ein flaches JSON-Objekt (keine Verschachtelung). Fehler -> false + Meldung.</summary>
    internal static class MiniJson
    {
        public static bool Lesen(string text, List<JsonEintrag> aus, out string fehler)
        {
            aus.Clear();
            fehler = null;
            int i = 0;
            try
            {
                Leer(text, ref i);
                if (i >= text.Length || text[i] != '{') { fehler = "expected '{' at the start"; return false; }
                i++;
                Leer(text, ref i);
                if (i < text.Length && text[i] == '}') return true;
                while (true)
                {
                    Leer(text, ref i);
                    if (i >= text.Length || text[i] != '"') { fehler = "expected a quoted key at position " + i; return false; }
                    string key = Zeichenkette(text, ref i);
                    Leer(text, ref i);
                    if (i >= text.Length || text[i] != ':') { fehler = "expected ':' after \"" + key + "\""; return false; }
                    i++;
                    Leer(text, ref i);
                    var e = new JsonEintrag { Schluessel = key };
                    if (i >= text.Length) { fehler = "value missing after \"" + key + "\""; return false; }
                    char c = text[i];
                    if (c == '"') { e.Art = JsonArt.Text; e.Roh = Zeichenkette(text, ref i); }
                    else if (c == '{' || c == '[') { e.Art = JsonArt.Anders; e.Roh = ""; Ueberspringen(text, ref i); }
                    else
                    {
                        int s = i;
                        while (i < text.Length && text[i] != ',' && text[i] != '}' && !char.IsWhiteSpace(text[i])) i++;
                        string tok = text.Substring(s, i - s);
                        if (tok == "true" || tok == "false") { e.Art = JsonArt.Bool; e.Roh = tok; }
                        else if (tok == "null") { e.Art = JsonArt.Null; e.Roh = tok; }
                        else if (double.TryParse(tok, NumberStyles.Float, CultureInfo.InvariantCulture, out _)) { e.Art = JsonArt.Zahl; e.Roh = tok; }
                        else { fehler = "cannot read value '" + tok + "' of \"" + key + "\""; return false; }
                    }
                    aus.Add(e);
                    Leer(text, ref i);
                    if (i >= text.Length) { fehler = "missing '}' at the end"; return false; }
                    if (text[i] == ',') { i++; continue; }
                    if (text[i] == '}') { i++; break; }
                    fehler = "expected ',' or '}' at position " + i;
                    return false;
                }
                Leer(text, ref i);
                if (i < text.Length) { fehler = "unexpected text after the closing '}'"; return false; }
                return true;
            }
            catch (Exception ex) { fehler = ex.Message; return false; }
        }

        static void Leer(string t, ref int i) { while (i < t.Length && char.IsWhiteSpace(t[i])) i++; }

        static string Zeichenkette(string t, ref int i)
        {
            var sb = new StringBuilder();
            i++;   // oeffnendes "
            while (i < t.Length)
            {
                char c = t[i++];
                if (c == '"') return sb.ToString();
                if (c != '\\') { sb.Append(c); continue; }
                if (i >= t.Length) break;
                char n = t[i++];
                switch (n)
                {
                    case 'n': sb.Append('\n'); break;
                    case 't': sb.Append('\t'); break;
                    case 'r': sb.Append('\r'); break;
                    case 'b': sb.Append('\b'); break;
                    case 'f': sb.Append('\f'); break;
                    case 'u':
                        if (i + 4 > t.Length) throw new FormatException("bad \\u escape");
                        sb.Append((char)Convert.ToInt32(t.Substring(i, 4), 16)); i += 4; break;
                    default: sb.Append(n); break;   // \" \\ \/
                }
            }
            throw new FormatException("unterminated string");
        }

        // verschachtelte Werte werden nur uebersprungen (und als "Anders" gemeldet)
        static void Ueberspringen(string t, ref int i)
        {
            int tiefe = 0; bool imText = false;
            for (; i < t.Length; i++)
            {
                char c = t[i];
                if (imText) { if (c == '\\') i++; else if (c == '"') imText = false; continue; }
                if (c == '"') imText = true;
                else if (c == '{' || c == '[') tiefe++;
                else if (c == '}' || c == ']') { tiefe--; if (tiefe == 0) { i++; return; } }
            }
            throw new FormatException("unterminated nested value");
        }

        /// <summary>Datei-Inhalt zurueckschreiben (fuer das Entfernen von "reload").</summary>
        public static string Schreiben(List<JsonEintrag> liste)
        {
            var sb = new StringBuilder("{\n");
            for (int k = 0; k < liste.Count; k++)
            {
                var e = liste[k];
                sb.Append("  ").Append(Quote(e.Schluessel)).Append(": ");
                switch (e.Art)
                {
                    case JsonArt.Text: sb.Append(Quote(e.Roh)); break;
                    case JsonArt.Anders: sb.Append("null"); break;
                    default: sb.Append(e.Roh); break;
                }
                sb.Append(k + 1 < liste.Count ? ",\n" : "\n");
            }
            return sb.Append("}\n").ToString();
        }

        static string Quote(string s)
        {
            var sb = new StringBuilder("\"");
            foreach (char c in s)
            {
                if (c == '"' || c == '\\') sb.Append('\\').Append(c);
                else if (c == '\n') sb.Append("\\n");
                else if (c < 0x20) sb.Append("\\u").Append(((int)c).ToString("x4"));
                else sb.Append(c);
            }
            return sb.Append('"').ToString();
        }
    }

    // ------------------------------------------------------------------ Schluessel + Werte

    internal static class LiveSchluessel
    {
        /// <summary>
        /// Zerlegt "Sektion/Schluessel". Mancher Schluessel enthaelt selbst ein "/" (z.B. "Density 01 Concrete/Stone (kg/m3)"),
        /// darum wird jede Trennstelle probiert und die genommen, die es als Eintrag wirklich gibt.
        /// Vergleich zuerst exakt, dann ohne Gross-/Kleinschreibung.
        /// </summary>
        public static bool Aufloesen(IEnumerable<KeyValuePair<string, string>> vorhandene, string voll, out string sektion, out string schluessel)
        {
            sektion = schluessel = null;
            var liste = vorhandene as IList<KeyValuePair<string, string>> ?? new List<KeyValuePair<string, string>>(vorhandene);
            for (int pass = 0; pass < 2; pass++)
            {
                var cmp = pass == 0 ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
                for (int i = voll.IndexOf('/'); i >= 0; i = voll.IndexOf('/', i + 1))
                {
                    string s = voll.Substring(0, i), k = voll.Substring(i + 1);
                    foreach (var p in liste)
                        if (string.Equals(p.Key, s, cmp) && string.Equals(p.Value, k, cmp))
                        { sektion = p.Key; schluessel = p.Value; return true; }
                }
            }
            return false;
        }
    }

    internal static class WertWandler
    {
        /// <summary>JSON-Wert -> Text fuer ConfigEntryBase.SetSerializedValue. false + Meldung, wenn der Typ nicht passt.</summary>
        public static bool Wandeln(Type ziel, JsonEintrag e, out string text, out string fehler)
        {
            text = null; fehler = null;
            if (e.Art == JsonArt.Null || e.Art == JsonArt.Anders) { fehler = "null/nested values are not supported"; return false; }
            var inv = CultureInfo.InvariantCulture;
            if (ziel == typeof(bool))
            {
                if (e.Art == JsonArt.Bool || (e.Art == JsonArt.Text && (e.Roh == "true" || e.Roh == "false"))) { text = e.Roh; return true; }
                if (e.Art == JsonArt.Zahl && (e.Roh == "0" || e.Roh == "1")) { text = e.Roh == "1" ? "true" : "false"; return true; }
                fehler = "expected true or false"; return false;
            }
            if (ziel == typeof(float) || ziel == typeof(double) || ziel == typeof(decimal))
            {
                if ((e.Art == JsonArt.Zahl || e.Art == JsonArt.Text) && double.TryParse(e.Roh, NumberStyles.Float, inv, out double d))
                { text = d.ToString("R", inv); return true; }
                fehler = "expected a number"; return false;
            }
            if (ziel == typeof(int) || ziel == typeof(long) || ziel == typeof(short) || ziel == typeof(byte))
            {
                if ((e.Art == JsonArt.Zahl || e.Art == JsonArt.Text) && double.TryParse(e.Roh, NumberStyles.Float, inv, out double d))
                {
                    if (Math.Abs(d - Math.Round(d)) > 1e-9) { fehler = "expected a whole number"; return false; }
                    text = ((long)Math.Round(d)).ToString(inv); return true;
                }
                fehler = "expected a whole number"; return false;
            }
            if (e.Art == JsonArt.Bool) { text = e.Roh; return true; }
            text = e.Roh;   // Text, Aufzaehlung, Tastenkuerzel, Zahl-als-Text
            return true;
        }
    }

    // ------------------------------------------------------------------ Begrenzung

    /// <summary>Hoechstens <c>proQuelle</c> Zeilen je Quelle und Sekunde; zusaetzlich <c>gesamt</c> Zeilen je Sekunde.</summary>
    internal sealed class RateLimiter
    {
        struct Fenster { public double Start; public int N; }
        readonly Dictionary<int, Fenster> karte = new Dictionary<int, Fenster>(256);
        double gStart; int gN;
        public int ProQuelle = 4, Gesamt = 400;
        public long Verworfen;

        public bool Erlaubt(int quelle, double jetzt)
        {
            if (jetzt - gStart >= 1.0) { gStart = jetzt; gN = 0; }
            if (gN >= Gesamt) { Verworfen++; return false; }
            Fenster f;
            if (!karte.TryGetValue(quelle, out f) || jetzt - f.Start >= 1.0) { f.Start = jetzt; f.N = 0; }
            if (f.N >= ProQuelle) { karte[quelle] = f; Verworfen++; return false; }
            f.N++;
            karte[quelle] = f;
            gN++;
            return true;
        }

        public void Leeren() { karte.Clear(); }
    }

    // ------------------------------------------------------------------ Eintrag + Zeile

    internal sealed class LogStueck
    {
        public int Gruppe;           // Index der Materialgruppe, -1 = unbekannt
        public float Dicke, Db;      // geschaetzte Dicke (m), Daemmung dieses Stuecks (dB)
        public int Strahlen;         // wie viele Strahlen dieses Stueck trafen
        public int Teile;            // 0.8.4, nur Wandstuecke: so viele Collider-Stuecke bilden diese Schicht (Physik-Modus)
        public string Name;
    }

    internal enum LogArt { Klang, Marke, Text, Oeffnen, Schliessen }

    internal sealed class LogEintrag
    {
        public const int MaxStuecke = 6;

        public LogArt Art;
        public long Uhr;                 // DateTime.Now.Ticks (lokale Zeit)
        public float Spielzeit;
        public int Bild;
        public int MarkNr;
        public float PosX, PosY, PosZ;
        public string Text;              // Text / Dateiname / Kopfzeilen

        public string Typ, Gruppentyp, Klang, Quelle, Besitzer, QuellRaum, HoererRaum;
        public float Entf;
        public int Weg;                  // siehe WegName
        public bool Propagation, Physik;
        public float WertT = float.NaN, WertD = float.NaN, WertR = float.NaN, WertRoute = float.NaN, WertWand = float.NaN;
        public float Wirkung, Lautstaerke, Tiefpass, Hochpass, RohDb, Lautheit;
        public float Knie = float.NaN;                                     // 0.8.5: Hoerbarkeit leiser Quellen (dB), NaN = keins
        public bool HoerBand;                                              // 0.8.5: Hoerbarkeit je Band gerechnet
        public readonly float[] HoerUeber = new float[3], HoerD = new float[3];   // dB ueber der Mithoerschwelle / reale Daemmung je Band
        public float HoerDb;                                               // angewandte Daempfung (lautestes Band)
        public float QX = float.NaN, QY = float.NaN, QZ = float.NaN, Dy = float.NaN;   // 0.8.5: Quellposition, Hoehe Quelle - Hoerer
        public float FarbeH = float.NaN, FarbeL = float.NaN, LuftHz = float.NaN;
        public float WaldM = float.NaN, WaldDb = float.NaN, WaldHz = float.NaN;
        public int Strahlen, FreieStrahlen;

        public int StueckAnzahl; public readonly LogStueck[] Stuecke = NeueStuecke();
        public int WandAnzahl; public readonly LogStueck[] Wand = NeueStuecke();

        public bool HatSeb;
        public float SebW, SebDb, SebDirLp, SebLuftHz, SebLp;
        public float SebWaldM = float.NaN, SebWaldHz = float.NaN;
        public bool SebGedaempft;
        public float SebFernDb = float.NaN;   // 0.8.4: 13. Distance (SEB), Pegelaenderung gegenueber SEB
        public bool SebFernMitte;

        public string HallQuelle, HallHoerer;
        public string RaumwegText;       // 0.8.4: Portalkette des physikalischen Raumwegs (beginnt mit " | room path:")
        public string WegeText;          // 0.8.4 Stufe 1: Beitrag jedes Wegs in dB und die Summe (beginnt mit " | paths")
        public float HallQuelleRt, HallQuelleDb, HallHoererRt, HallHoererDb;

        static LogStueck[] NeueStuecke()
        {
            var a = new LogStueck[MaxStuecke];
            for (int i = 0; i < a.Length; i++) a[i] = new LogStueck();
            return a;
        }

        public void Zuruecksetzen()
        {
            Art = LogArt.Klang; Text = null; MarkNr = 0;
            Typ = Gruppentyp = Klang = Quelle = Besitzer = QuellRaum = HoererRaum = null;
            Weg = 0; Propagation = Physik = false;
            WertT = WertD = WertR = WertRoute = WertWand = float.NaN;
            FarbeH = FarbeL = LuftHz = WaldM = WaldDb = WaldHz = SebWaldM = SebWaldHz = SebFernDb = float.NaN; SebGedaempft = SebFernMitte = false;
            Strahlen = FreieStrahlen = 0; StueckAnzahl = WandAnzahl = 0;
            HatSeb = false; HallQuelle = HallHoerer = null; RaumwegText = null; WegeText = null;
            Knie = QX = QY = QZ = Dy = float.NaN; HoerBand = false;
        }
    }

    internal static class ZeilenFormat
    {
        public static readonly string[] WegName =
        {
            "direct", "through walls", "diffraction", "reflection", "via doors/windows (rooms)", "through wall between rooms", "unknown"
        };

        /// <summary>Welcher Weg bestimmt den Klang? Der mit der kleinsten Teilwertung (NaN = nicht gerechnet).</summary>
        public static int WegWahl(bool propagation, float wirkung, float t, float d, float r, float route, float wand)
        {
            if (wirkung < 0.005f) return 0;
            if (propagation)
            {
                if (!float.IsNaN(wand) && wand >= 0f && (float.IsNaN(route) || wand < route)) return 5;
                return 4;
            }
            int weg = 6; float min = float.MaxValue;
            if (!float.IsNaN(t) && t < min) { min = t; weg = 1; }
            if (!float.IsNaN(d) && d < min) { min = d; weg = 2; }
            if (!float.IsNaN(r) && r < min) { min = r; weg = 3; }
            return weg;
        }

        static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

        static StringBuilder F(StringBuilder sb, float v, string fmt)
        {
            if (float.IsNaN(v)) return sb.Append("n/a");
            if (float.IsInfinity(v)) return sb.Append(v > 0 ? "inf" : "-inf");
            return sb.Append(v.ToString(fmt, Inv));
        }

        /// <summary>0.8.5: " | audible 250/1k/4k: yes +12.3 (D 21.0) / yes +4.1 (D 33.0) / no -6.0 (D 45.0) -> -10.5 dB" -
        /// je Band hoerbar ja/nein, Pegel ueber (+) bzw. unter (-) der Mithoerschwelle und reale Daemmung; "silent" = kein Band.</summary>
        public static void HoerText(StringBuilder sb, LogEintrag e)
        {
            sb.Append(" | audible 250/1k/4k:");
            bool eins = false;
            for (int b = 0; b < 3; b++)
            {
                float u = e.HoerUeber[b];
                eins |= u > 0f;
                sb.Append(b == 0 ? " " : " / ").Append(u > 0f ? (u < 10f ? "fading " : "yes ") : "no ");
                if (u > 0f) sb.Append('+');
                F(sb, u, "F1").Append(" (D "); F(sb, e.HoerD[b], "F1").Append(')');
            }
            sb.Append(eins ? " -> -" : " -> silent -"); F(sb, e.HoerDb, "F1").Append(" dB");
        }

        public static string Uhrzeit(long ticks)
        {
            var d = new DateTime(ticks);
            return d.ToString("HH:mm:ss.fff", Inv);
        }

        /// <summary>Gruppenname je Index (vom Plugin gesetzt; im Test frei).</summary>
        public static Func<int, string> GruppenName = i => "group" + i;

        public static void Formatieren(StringBuilder sb, LogEintrag e)
        {
            switch (e.Art)
            {
                case LogArt.Marke:
                    sb.Append("=== MARK ").Append(e.MarkNr).Append(" === ").Append(Uhrzeit(e.Uhr)).Append(" t=");
                    F(sb, e.Spielzeit, "F2").Append(" f=").Append(e.Bild).Append(" listener=(");
                    F(sb, e.PosX, "F1").Append(", "); F(sb, e.PosY, "F1").Append(", "); F(sb, e.PosZ, "F1").Append(")");
                    if (!string.IsNullOrEmpty(e.Text)) sb.Append(' ').Append(e.Text);
                    sb.Append('\n');
                    return;
                case LogArt.Text:
                    sb.Append(Uhrzeit(e.Uhr)).Append(" t="); F(sb, e.Spielzeit, "F2").Append(" | ").Append(e.Text).Append('\n');
                    return;
            }

            sb.Append(Uhrzeit(e.Uhr)).Append(" t="); F(sb, e.Spielzeit, "F2").Append(" f=").Append(e.Bild).Append(" | ");
            sb.Append(e.Typ ?? "?");
            if (e.Gruppentyp != null && e.Gruppentyp != e.Typ) sb.Append('(').Append(e.Gruppentyp).Append(')');
            sb.Append(" clip=").Append(string.IsNullOrEmpty(e.Klang) ? "-" : e.Klang);
            sb.Append(" src=").Append(e.Quelle ?? "-");
            sb.Append(" owner=").Append(e.Besitzer ?? "-");
            sb.Append(" | d="); F(sb, e.Entf, "F1").Append(" m");
            if (!float.IsNaN(e.QX)) { sb.Append(" at ("); F(sb, e.QX, "F1").Append(", "); F(sb, e.QY, "F1").Append(", "); F(sb, e.QZ, "F1").Append(')'); }
            if (!float.IsNaN(e.Dy)) { sb.Append(" dy="); F(sb, e.Dy, "+0.0;-0.0").Append(" m"); }
            sb.Append(" | path=").Append(WegName[Math.Max(0, Math.Min(WegName.Length - 1, e.Weg))]);
            sb.Append(" [T="); F(sb, e.WertT, "F2"); sb.Append(" D="); F(sb, e.WertD, "F2"); sb.Append(" R="); F(sb, e.WertR, "F2");
            if (e.Propagation) { sb.Append(" route="); F(sb, e.WertRoute, "F2"); sb.Append(" wall="); F(sb, e.WertWand, "F2"); }
            sb.Append(" effect="); F(sb, e.Wirkung, "F2"); sb.Append(']');

            if (e.StueckAnzahl > 0 || e.Strahlen > 0)
            {
                sb.Append(" | hits:");
                if (e.StueckAnzahl == 0) sb.Append(" none");
                for (int i = 0; i < e.StueckAnzahl; i++) Stueck(sb, e.Stuecke[i], i == 0);
                sb.Append(" (free rays ").Append(e.FreieStrahlen).Append('/').Append(e.Strahlen).Append(')');
            }
            if (e.WandAnzahl > 0)
            {
                sb.Append(" | wall pieces:");
                for (int i = 0; i < e.WandAnzahl; i++) Stueck(sb, e.Wand[i], i == 0);
            }

            double lautDb = e.Lautstaerke > 1e-6f ? -20.0 * Math.Log10(e.Lautstaerke) : 120.0;
            sb.Append(" | vol=").Append(e.Lautstaerke.ToString("F3", Inv)).Append(" (-").Append(lautDb.ToString("F1", Inv)).Append(" dB)");
            sb.Append(" lp="); F(sb, e.Tiefpass, "F0").Append(" Hz hp="); F(sb, e.Hochpass, "F0").Append(" Hz");
            sb.Append(" | mode=").Append(e.Physik ? "physics" : "sliders");
            if (e.Physik)
            {
                sb.Append(" (insulation "); F(sb, e.RohDb, "F1").Append(" dB x loudness "); F(sb, e.Lautheit, "F2");
                if (!float.IsNaN(e.Knie)) { sb.Append(" up to "); F(sb, e.Knie, "F1").Append(" dB above background, beyond 1:1, gone 10 dB below"); }
                sb.Append(')');
                if (e.HoerBand) HoerText(sb, e);
            }
            if (!float.IsNaN(e.FarbeH)) { sb.Append(" timbre dH="); F(sb, e.FarbeH, "F1"); sb.Append(" dL="); F(sb, e.FarbeL, "F1"); sb.Append(" dB"); }
            if (!float.IsNaN(e.LuftHz)) { sb.Append(" air="); F(sb, e.LuftHz, "F0").Append(" Hz"); }
            if (!float.IsNaN(e.WaldM)) { sb.Append(" forest="); F(sb, e.WaldM, "F0"); sb.Append(" m "); F(sb, e.WaldDb, "F1"); sb.Append(" dB lp="); F(sb, e.WaldHz / 1000f, "F1").Append(" kHz"); }
            if (e.HatSeb)
            {
                sb.Append(e.SebGedaempft ? " | SUPPRESSED shot" : " | open shot");
                sb.Append(" | SEB directivity w="); F(sb, e.SebW, "F2"); sb.Append(' '); F(sb, e.SebDb, "F1"); sb.Append(" dB rear-lp=");
                F(sb, e.SebDirLp, "F0"); sb.Append(" Hz, air="); F(sb, e.SebLuftHz, "F0"); sb.Append(" Hz, final lp="); F(sb, e.SebLp, "F0").Append(" Hz");
                if (!float.IsNaN(e.SebWaldM)) { sb.Append(", forest="); F(sb, e.SebWaldM, "F0"); sb.Append(" m lp="); F(sb, e.SebWaldHz / 1000f, "F1").Append(" kHz"); }
                if (!float.IsNaN(e.SebFernDb))
                {
                    if (e.SebFernDb >= 199f) sb.Append(" | distance boost (SEB silent here)");
                    else { sb.Append(" | distance boost "); Vorz(sb, e.SebFernDb, "F1"); sb.Append(" dB"); }
                }
                if (e.SebFernMitte) sb.Append(" | mid clip kept (no far clip)");
            }
            if (e.RaumwegText != null && e.Weg == 4) sb.Append(e.RaumwegText);
            if (e.WegeText != null) sb.Append(e.WegeText);
            if (e.QuellRaum != null || e.HoererRaum != null)
                sb.Append(" | rooms src=").Append(e.QuellRaum ?? "-").Append(" ear=").Append(e.HoererRaum ?? "-");
            if (e.HallQuelle != null || e.HallHoerer != null)
            {
                sb.Append(" | reverb");
                if (e.HallQuelle != null) { sb.Append(" src[").Append(e.HallQuelle).Append(' '); Vorz(sb, e.HallQuelleRt, "F2"); sb.Append(" s, "); Vorz(sb, e.HallQuelleDb, "F1"); sb.Append(" dB]"); }
                if (e.HallHoerer != null) { sb.Append(" ear[").Append(e.HallHoerer).Append(' '); Vorz(sb, e.HallHoererRt, "F2"); sb.Append(" s, "); Vorz(sb, e.HallHoererDb, "F1"); sb.Append(" dB]"); }
            }
            sb.Append('\n');
        }

        static void Vorz(StringBuilder sb, float v, string fmt)
        {
            if (v >= 0f) sb.Append('+');
            F(sb, v, fmt);
        }

        static void Stueck(StringBuilder sb, LogStueck s, bool erstes)
        {
            sb.Append(erstes ? " " : "; ");
            sb.Append(s.Gruppe >= 0 ? GruppenName(s.Gruppe) : "?").Append(' ');
            F(sb, s.Dicke, "F2").Append(" m ");
            F(sb, s.Db, "F1").Append(" dB");
            if (s.Strahlen > 1) sb.Append(" x").Append(s.Strahlen);
            if (!string.IsNullOrEmpty(s.Name)) sb.Append(" \"").Append(s.Name).Append('"');
            if (s.Teile > 1) sb.Append(" (").Append(s.Teile).Append(" parts as one layer)");
        }
    }

    // ------------------------------------------------------------------ Treffer einer Durchdringungsrechnung

    /// <summary>
    /// Merkt sich je TransmissionCalculator die Treffer der letzten Rechnung in festen Feldern
    /// (wird nur gefuellt, solange das Klang-Log an ist; waechst nur, wenn mehr Strahlen kommen als je zuvor).
    /// </summary>
    internal sealed class TransInfo
    {
        public int N, Frei, Anzahl;
        public int[] Id = new int[64];
        public int[] Gr = new int[64];
        public float[] Dicke = new float[64];
        public float[] Db = new float[64];

        public void Beginn(int n)
        {
            N = n; Frei = 0; Anzahl = 0;
            if (Id.Length < n)
            {
                int g = Math.Max(n, Id.Length * 2);
                Id = new int[g]; Gr = new int[g]; Dicke = new float[g]; Db = new float[g];
            }
        }

        public void FreierStrahl() { Frei++; }

        public void Treffer(int collider, int gruppe, float dicke, float db)
        {
            if (Anzahl >= Id.Length) return;
            Id[Anzahl] = collider; Gr[Anzahl] = gruppe; Dicke[Anzahl] = dicke; Db[Anzahl] = db; Anzahl++;
        }

        bool[] besucht = new bool[64];

        /// <summary>Gleiche Collider zusammenfassen, die staerksten (nach dB) in den Eintrag schreiben.</summary>
        public void Fuellen(LogEintrag e, Func<int, string> name)
        {
            e.Strahlen = N; e.FreieStrahlen = Frei; e.StueckAnzahl = 0;
            int k = Anzahl;
            if (besucht.Length < k) besucht = new bool[Math.Max(k, besucht.Length * 2)];
            Array.Clear(besucht, 0, k);
            for (int i = 0; i < k; i++)
            {
                if (besucht[i]) continue;
                int id = Id[i];
                int n = 1; float dSum = Dicke[i], bSum = Db[i];
                for (int j = i + 1; j < k; j++)
                    if (!besucht[j] && Id[j] == id) { n++; dSum += Dicke[j]; bSum += Db[j]; besucht[j] = true; }
                float d = dSum / n, b = bSum / n;
                // einsortieren nach dB (absteigend), hoechstens MaxStuecke
                int pos = e.StueckAnzahl;
                while (pos > 0 && e.Stuecke[pos - 1].Db < b) pos--;
                if (pos >= LogEintrag.MaxStuecke) continue;
                int ende = Math.Min(e.StueckAnzahl, LogEintrag.MaxStuecke - 1);
                var frei = e.Stuecke[ende];                 // faellt heraus (oder ist ein unbenutzter Platz)
                for (int s = ende; s > pos; s--) e.Stuecke[s] = e.Stuecke[s - 1];
                e.Stuecke[pos] = frei;
                frei.Gruppe = Gr[i]; frei.Dicke = d; frei.Db = b; frei.Strahlen = n; frei.Teile = 0; frei.Name = name != null ? name(id) : null;
                if (e.StueckAnzahl < LogEintrag.MaxStuecke) e.StueckAnzahl++;
            }
        }
    }

    // ------------------------------------------------------------------ Aufraeumen alter Dateien

    internal static class LogRotation
    {
        /// <summary>Von den vorhandenen Logdateien alle bis auf die neuesten <c>behalten</c> (die neue Datei zaehlt mit).</summary>
        public static List<string> ZuLoeschen(List<KeyValuePair<string, DateTime>> dateien, int behalten)
        {
            var sortiert = new List<KeyValuePair<string, DateTime>>(dateien);
            sortiert.Sort((a, b) => b.Value.CompareTo(a.Value));
            var weg = new List<string>();
            int erlaubt = Math.Max(0, behalten - 1);        // eine Stelle ist fuer die neue Datei
            for (int i = erlaubt; i < sortiert.Count; i++) weg.Add(sortiert[i].Key);
            return weg;
        }

        public static string Dateiname(DateTime jetzt, string karte)
        {
            var sb = new StringBuilder();
            foreach (char c in karte ?? "map") sb.Append(char.IsLetterOrDigit(c) || c == '_' || c == '-' ? c : '_');
            return jetzt.ToString("yyyy-MM-dd_HH-mm-ss", CultureInfo.InvariantCulture) + "-" + sb + ".log";
        }
    }

    // ------------------------------------------------------------------ Schreiber (Hintergrund-Faden)

    /// <summary>
    /// Der Hauptfaden holt sich einen Eintrag aus dem Vorrat, fuellt ihn und reiht ihn ein - mehr nicht.
    /// Formatieren, Dateizugriff, Aufraeumen und Schreiben passieren im eigenen Faden.
    /// </summary>
    internal sealed class KlangSchreiber
    {
        readonly object sperre = new object();
        readonly Queue<LogEintrag> schlange = new Queue<LogEintrag>(1024);
        readonly Stack<LogEintrag> vorrat = new Stack<LogEintrag>(1024);
        readonly AutoResetEvent signal = new AutoResetEvent(false);
        Thread faden;
        volatile bool beenden;
        public int MaxSchlange = 20000;
        public long Verloren;                       // wegen voller Schlange verworfen
        public Action<string> Meldung = s => { };    // Fehler des Fadens (Hauptfaden gibt sie aus)
        readonly Queue<string> meldungen = new Queue<string>();

        // nur vom Faden benutzt
        StreamWriter datei; long bytes, grenze = 50L * 1024 * 1024; string pfad; bool gedeckelt;
        public string AktuellerPfad { get { lock (sperre) return pfad; } }
        public long Geschrieben { get { lock (sperre) return bytes; } }

        public LogEintrag Holen()
        {
            lock (sperre) { if (vorrat.Count > 0) { var e = vorrat.Pop(); e.Zuruecksetzen(); return e; } }
            return new LogEintrag();
        }

        public bool Einreihen(LogEintrag e)
        {
            lock (sperre)
            {
                if (schlange.Count >= MaxSchlange && e.Art == LogArt.Klang)
                { Verloren++; e.Zuruecksetzen(); vorrat.Push(e); return false; }
                schlange.Enqueue(e);
            }
            Start();
            signal.Set();
            return true;
        }

        void Start()
        {
            if (faden != null) return;
            lock (sperre)
            {
                if (faden != null) return;
                faden = new Thread(Lauf) { IsBackground = true, Name = "Materialklang-Log" };
                faden.Start();
            }
        }

        /// <summary>Datei fuer einen neuen Raid oeffnen (im Faden). Aufraeumen alter Dateien inklusive.</summary>
        public void Oeffnen(string verzeichnis, string dateiname, int behalten, long maxBytes, string kopf)
        {
            var e = Holen();
            e.Art = LogArt.Oeffnen;
            e.Text = verzeichnis + "\n" + dateiname + "\n" + behalten + "\n" + maxBytes + "\n" + kopf;
            Einreihen(e);
        }

        public void Schliessen()
        {
            var e = Holen(); e.Art = LogArt.Schliessen; Einreihen(e);
        }

        /// <summary>Fehlermeldungen des Fadens (vom Hauptfaden regelmaessig abholen).</summary>
        public string MeldungHolen()
        {
            lock (sperre) return meldungen.Count > 0 ? meldungen.Dequeue() : null;
        }

        /// <summary>Beim Beenden: alles Eingereihte schreiben und Datei schliessen.</summary>
        public void Beenden(int wartenMs = 2000)
        {
            if (faden == null) return;
            Schliessen();
            beenden = true;
            signal.Set();
            faden.Join(wartenMs);
        }

        void Melden(string s) { lock (sperre) { if (meldungen.Count < 50) meldungen.Enqueue(s); } }

        void Lauf()
        {
            var sb = new StringBuilder(4096);
            var stapel = new List<LogEintrag>(256);
            long letzteVerloren = 0;
            while (true)
            {
                signal.WaitOne(300);
                while (true)
                {
                    stapel.Clear();
                    lock (sperre) { while (schlange.Count > 0 && stapel.Count < 512) stapel.Add(schlange.Dequeue()); }
                    if (stapel.Count == 0) break;
                    sb.Length = 0;
                    try
                    {
                        foreach (var e in stapel)
                        {
                            if (e.Art == LogArt.Oeffnen) { Schreiben(sb); OeffnenIntern(e); }
                            else if (e.Art == LogArt.Schliessen) { Schreiben(sb); SchliessenIntern(); }
                            else if (datei != null && !(gedeckelt && e.Art == LogArt.Klang)) ZeilenFormat.Formatieren(sb, e);
                        }
                        Schreiben(sb);
                        long vl = Verloren;
                        if (vl != letzteVerloren && datei != null)
                        {
                            datei.Write("=== " + (vl - letzteVerloren) + " lines dropped (writer queue full) ===\n"); datei.Flush();
                            letzteVerloren = vl;
                        }
                    }
                    catch (Exception ex)
                    {
                        Melden("Sound log writer: " + ex.Message);
                        try { datei?.Dispose(); } catch { }
                        datei = null;
                    }
                    lock (sperre)
                    {
                        foreach (var e in stapel) { e.Zuruecksetzen(); vorrat.Push(e); }
                    }
                }
                if (beenden)
                {
                    lock (sperre) { if (schlange.Count > 0) continue; }
                    try { datei?.Dispose(); } catch { }
                    datei = null;
                    return;
                }
            }
        }

        void OeffnenIntern(LogEintrag e)
        {
            SchliessenIntern();
            var t = e.Text.Split(new[] { '\n' }, 5);
            string dir = t[0], name = t[1]; int behalten = int.Parse(t[2], CultureInfo.InvariantCulture); long max = long.Parse(t[3], CultureInfo.InvariantCulture);
            string kopf = t.Length > 4 ? t[4] : "";
            Directory.CreateDirectory(dir);
            try
            {
                var alle = new List<KeyValuePair<string, DateTime>>();
                foreach (var f in Directory.GetFiles(dir, "*.log")) alle.Add(new KeyValuePair<string, DateTime>(f, File.GetLastWriteTime(f)));
                foreach (var f in LogRotation.ZuLoeschen(alle, behalten)) { try { File.Delete(f); } catch { } }
            }
            catch (Exception ex) { Melden("Sound log cleanup: " + ex.Message); }
            string p = Path.Combine(dir, name);
            datei = new StreamWriter(new FileStream(p, FileMode.Create, FileAccess.Write, FileShare.ReadWrite), new UTF8Encoding(false));
            lock (sperre) { pfad = p; bytes = 0; }
            grenze = max; gedeckelt = false;
            string k = kopf.Length > 0 ? kopf.TrimEnd('\n') + "\n" : "";
            datei.Write(k); bytes += k.Length;
            datei.Flush();
        }

        // sb in die Datei schreiben; Groessengrenze beachten
        void Schreiben(StringBuilder sb)
        {
            if (sb.Length == 0) return;
            string text = sb.ToString();
            sb.Length = 0;
            if (datei == null) return;
            long neu = Encoding.UTF8.GetByteCount(text);
            if (!gedeckelt && bytes + neu > grenze)
            {
                gedeckelt = true;
                string hinweis = "=== SIZE CAP " + (grenze / (1024 * 1024)) + " MB reached - further sound lines are dropped, MARK and text lines are still written ===\n";
                datei.Write(hinweis);
                lock (sperre) bytes += hinweis.Length;
                // vom Stapel bleiben nur Marken/Texte: die Zeilen stehen schon im Text -> hier einfach ueberspringen
                return;
            }
            datei.Write(text);
            datei.Flush();
            lock (sperre) bytes += neu;
        }

        void SchliessenIntern()
        {
            if (datei == null) return;
            try { datei.Write("=== end of log ===\n"); datei.Flush(); datei.Dispose(); }
            catch { }
            datei = null;
        }

        /// <summary>Zeitstempel in Sekunden (fuer die Begrenzung), ohne Unity.</summary>
        public static double Jetzt() => (double)Stopwatch.GetTimestamp() / Stopwatch.Frequency;
    }
}
