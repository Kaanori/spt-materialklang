using System;
using System.Collections.Generic;

namespace Materialklang
{
    /// <summary>Skript-/Code-erzeugte Quellart (Abschnitt 18).</summary>
    internal enum SkriptArt
    {
        LokFahrt = 0, LokLeerlauf, Flugzeug, Landung, Abwurf, BtrTurm, BtrHorn, Ausgangsfackel, Leuchtpatrone, Pfeifen, Tuer, Ausgangstor
    }

    // 0.8.5 - 18. Scripted sources: reine Rechnung (ohne Unity, im Offline-Test).
    //
    // Gleiche Regel wie Abschnitt 17 (QuellRegelKern + UmgebungKern.Bauen): bis r0 die Kurve des Spiels (an der Quelle gleich laut),
    // danach 1/r (-6 dB je Verdopplung), weicher cos^2-Auslauf auf den letzten 25 %, Reichweite = alt x Faktor, gedeckelt je Art,
    // nie kuerzer als im Spiel. Werte je Art (Beleg: klangquellen-inventur-skript.md, Top-10, 09.11.2026; reale Pegel = Schaetzung):
    //   Lok Horn/Fahrt   300 m x 5  -> 1500 m  (Horn 96-110 dB(A) in 30 m, 49 CFR 229.129; fahrende Lok 90 dB(A) in 30 m)
    //   Lok Leerlauf      50 m x 10 ->  500 m  (Diesel-Leerlauf 70 dB(A) in 30 m, EPA 40 CFR 201)
    //   Flugzeug  Falloff(Prefab) x 20 -> 2000 m (Ueberflug 85-100 dB(A) am Boden; Falloff erst zur Laufzeit bekannt)
    //   Abwurf-Landung    50 m x 4  ->  200 m  (Aufprall ~85-95 dB @ 1 m)
    //   Fallschirm/Fackel der Kiste  x 4 -> 100 m
    //   BTR-Turm          60 m x 2,5 -> 150 m  (Elektro-/Hydraulikmotor)
    //   BTR-Lufthorn  Gruppen-Standard (Preset.DefaultMaxDistance) -> 500 m (Druckluft-Horn 105-118 dB(A) in 2 m)
    //   Ausgangsfackel    10 m x 5  ->   50 m  (Strassenfackel 65-75 dB @ 1 m)
    //   Leuchtpatrone     25 m x 4  ->  100 m  (~80 dB @ 1 m)
    //   Moerser-Pfeifen  115 m x 4  ->  350 m  (Pfeifen 70-90 dB am Flugweg)
    //   Tueren/Tore/Schalter x 2, hoechstens 100 m (Griffe 15 -> 30, Tueren 35 -> 70, Zuschlagen/Tore 50 -> 100, Aufbruch 60 -> 100)
    //   Ausgangs-Tor (ExfiltrationDoor) 50 m x 2 -> 100 m
    // Geteilte Daten des Spiels (TaggedClip.Falloff, SoundBank.Rolloff, SoundEvent.Rolloff, Prefab-Felder) werden NIE beschrieben: die
    // neue Reichweite wird bei jedem Abspielen aus dem unveraenderten Spielwert gerechnet und nur auf die abspielende Quelle gesetzt.
    // Damit kann ein geteilter Clip/eine geteilte Bank nicht zweimal skaliert werden (auch nicht, wenn das Spiel je Bild neu ruft).
    internal static class SkriptQuellenKern
    {
        internal struct Vorgabe
        {
            public string Name;          // fuer Log/Zusammenfassung (englisch)
            public double Faktor;        // Reichweite = alt x Faktor ...
            public double Grenze;        // ... hoechstens Grenze (Standard der Einstellung)
            public double GrenzeMin, GrenzeMax; // Bereich der Einstellung
            public bool Okklusion;       // hat im Spiel Okklusion (Waende daempfen)
            public bool KurveImmer;      // auch ohne laengere Reichweite die 1/r-Form setzen (Lufthorn: Reichweite = Gruppen-Standard)

            public Vorgabe(string name, double faktor, double grenze, double min, double max, bool okkl, bool kurveImmer = false)
            { Name = name; Faktor = faktor; Grenze = grenze; GrenzeMin = min; GrenzeMax = max; Okklusion = okkl; KurveImmer = kurveImmer; }
        }

        public static readonly Vorgabe[] Vorgaben =
        {
            new Vorgabe("train horn/travel", 5.0, 1500, 300, 3000, false),
            new Vorgabe("train idle", 10.0, 500, 50, 1500, false),
            new Vorgabe("airdrop plane", 20.0, 2000, 300, 4000, false),
            new Vorgabe("airdrop landing", 4.0, 200, 50, 500, true),
            new Vorgabe("airdrop parachute/flare", 4.0, 100, 20, 300, true),
            new Vorgabe("BTR turret", 2.5, 150, 60, 300, false),
            new Vorgabe("BTR air horn", 1.0, 500, 100, 1000, false, true),
            new Vorgabe("exit flare", 5.0, 50, 10, 150, false),
            new Vorgabe("flare round", 4.0, 100, 25, 300, true),
            new Vorgabe("mortar whistle", 4.0, 350, 115, 800, false),
            new Vorgabe("door/gate/switch", 2.0, 100, 30, 200, true),
            new Vorgabe("exit gate", 2.0, 100, 30, 200, false),
        };

        public const double TuerFaktor = 2.0;

        public static Vorgabe Von(SkriptArt a) => Vorgaben[(int)a];
        public static string Name(SkriptArt a) => Vorgaben[(int)a].Name;

        /// <summary>Neue Reichweite aus dem UNVERAENDERTEN Spielwert: alt x Faktor, hoechstens Grenze, nie kuerzer als alt.
        /// Lufthorn (Faktor 1): max(alt, Grenze) - die Einstellung ist die Reichweite.</summary>
        public static double Reichweite(SkriptArt a, double alt, double grenze, double faktor = double.NaN)
        {
            if (!(alt > 0) || double.IsInfinity(alt)) return alt;
            double f = double.IsNaN(faktor) ? Vorgaben[(int)a].Faktor : faktor;
            if (Vorgaben[(int)a].KurveImmer) return Math.Max(alt, grenze);
            return QuellRegelKern.Reichweite(alt, f, grenze);
        }

        /// <summary>Ganze Meter fuer die int-Reichweite von BetterAudio.PlayAtPoint (nie kuerzer als alt).</summary>
        public static int Ganz(int alt, double neu) => Math.Max(alt, (int)Math.Round(neu));

        /// <summary>Lohnt die Umstellung? Laenger, oder (Lufthorn) gleiche Reichweite mit 1/r-Form.</summary>
        public static bool Lohnt(SkriptArt a, double alt, double neu) => QuellRegelKern.Lohnt(alt, neu) || (Vorgaben[(int)a].KurveImmer && neu >= alt && alt > 0);

        /// <summary>Muss eine je Bild gerufene Stelle (Flugzeug) neu setzen? Nur wenn die Quelle nicht schon den Sollwert hat
        /// oder sich Stand/Quelle geaendert haben - sonst keine Arbeit.</summary>
        public static bool NeuSetzen(bool gleicheQuelle, int stand, int standGesetzt, double ist, double soll)
            => !gleicheQuelle || stand != standGesetzt || Math.Abs(ist - soll) > 0.01;

        /// <summary>[Scripted]-Zeile einer laufenden Quelle faellig? Nur in Hoerweite (bis 1,25 x Reichweite); erste sofort, dann nach
        /// max(2 m, 10 %) Bewegung oder alle 10 s (das Flugzeug bewegt sich schnell - 2 m allein waere jede halbe Sekunde).</summary>
        public static bool Zeile(double d, double dLetzt, double sekSeit, double reichweite)
            => d <= reichweite * 1.25 && (dLetzt < 0 || Math.Abs(d - dLetzt) >= Math.Max(2.0, 0.1 * d) || sekSeit >= 10.0);

        /// <summary>Einmal-Klaenge (Tueren, Landung, Pfeifen): hoechstens eine Zeile je Art+Clip alle 'abstand' Sekunden.</summary>
        public static bool EinmalZeile(double jetzt, double letzt, double abstand = 2.0) => letzt < 0 || jetzt - letzt >= abstand;

        /// <summary>Schluessel einer gebauten Kurve (alt/neu auf 0,1 m).</summary>
        public static long Schluessel(double alt, double neu) => ((long)Math.Round(alt * 10) << 32) | (uint)Math.Round(neu * 10);

        /// <summary>Zaehlschluessel der Zusammenfassung: "Name alt -> neu".</summary>
        public static string Eintrag(SkriptArt a, double alt, double neu, bool an)
        {
            var inv = System.Globalization.CultureInfo.InvariantCulture;
            return an ? Name(a) + " " + alt.ToString("0.#", inv) + " -> " + neu.ToString("0.#", inv) + " m"
                      : Name(a) + " " + alt.ToString("0.#", inv) + " m unchanged";
        }

        /// <summary>Zusammenfassung: Eintraege sortiert, je "Eintrag xAnzahl".</summary>
        public static string Zusammenfassung(IDictionary<string, int> zaehler)
        {
            var l = new List<KeyValuePair<string, int>>(zaehler);
            l.Sort((x, y) => string.CompareOrdinal(x.Key, y.Key));
            var sb = new System.Text.StringBuilder(256);
            for (int i = 0; i < l.Count; i++) sb.Append(i > 0 ? ", " : "").Append(l[i].Key).Append(" x").Append(l[i].Value);
            return sb.ToString();
        }
    }
}
