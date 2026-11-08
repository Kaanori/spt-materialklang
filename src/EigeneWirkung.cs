namespace Materialklang
{
    // 0.8.2 - Einmal-Klaenge (Einschlaege, Granaten, fallende Gegenstaende) erbten die Verdeckung eines ANDEREN Klangs
    // ("dumpfes Puff" einer F1 im Freien, Sandbox 08.11.2026; im Log "path=unknown [T=n/a D=n/a R=n/a effect=1.00]").
    // Reine Rechnung ohne Unity, damit der Offline-Test (Materialklang-KlangTest) sie mituebersetzen kann.
    //
    // Ursache (Spielcode, MultiSourceSpatialAudioOccluder.OnLateUpdate):
    //   EIN Occluder rechnet fuer viele Einmal-Klaenge (je Behaelter ein eigener Rechner aus dem Pool), die Ergebnisse landen
    //   aber in den GEMEINSAMEN Feldern PropagationEffect / ObstructionEffect des Occluders. Ein Behaelter mit Raumweg-Rechner
    //   setzt nur PropagationEffect, einer mit Durchdringungs-Rechner nur ObstructionEffect - das jeweils andere Feld bleibt
    //   vom letzten fremden Klang stehen. CalculateTotalEffect nimmt "PropagationEffect > 0 ? PropagationEffect : ObstructionEffect":
    //   - Raumweg frei (Wertung 0, z. B. zwischen zwei Aussenbereichen) -> es gilt die Durchdringung eines fremden Klangs
    //     (z. B. ein Einschlag hinter einem Huegel = 1,0) -> Physik-Modus: -30 dB und 800 Hz fuer eine Granate in 17 m im Freien.
    //   - Durchdringungs-Behaelter nach einem Raumweg-Behaelter mit Wertung > 0 -> er gilt als "Raumweg" mit fremdem Wert.
    //   Vanilla hat denselben Fehler (seit 0.6.0 in unseren Logs), hoert sich dort nur milder an (Kurve endet bei -4,4 dB).
    // Loesung: unmittelbar bevor das Spiel die Werte eines Behaelters anwendet, wird das Feld, das zu keinem Rechner
    //   DIESES Behaelters gehoert, auf 0 gesetzt. Es gilt also nur noch das eigene Ergebnis.
    internal static class EigeneWirkung
    {
        // Diagnose (alle 30 s ins Log)
        internal static int Geprueft, Korrigiert;

        /// <summary>Setzt den Anteil zurueck, den dieser Behaelter nicht selbst gerechnet hat.
        /// hatWeg / hatHindernis = der Behaelter hat einen Raumweg- bzw. Durchdringungs-Rechner.
        /// true = ein fremder (veralteter) Wert ungleich 0 wurde verworfen.</summary>
        internal static bool Bereinigen(bool hatWeg, bool hatHindernis, ref float weg, ref float hindernis)
        {
            if (hatWeg == hatHindernis) return false;          // keiner oder beide: nichts zuzuordnen, Spielwerte bleiben
            if (hatWeg)
            {
                if (hindernis == 0f) return false;
                hindernis = 0f;
                return true;
            }
            if (weg == 0f) return false;
            weg = 0f;
            return true;
        }

        /// <summary>Nachbildung von SpatialAudioOccluder.CalculateTotalEffect (nur fuer den Offline-Test).</summary>
        internal static float Gesamt(float weg, float hindernis)
        {
            float v = weg > 0f ? weg : hindernis;
            return v < 0f ? 0f : (v > 1f ? 1f : v);
        }
    }
}
