namespace Materialklang
{
    // 0.8.4 - die Reflexionsrechnung meldet "Direktweg frei", obwohl er verdeckt ist (Streets-Log 08.11.2026 16:50, MARK 1, Bot5).
    // Reine Rechnung ohne Unity, damit der Offline-Test (Materialklang-KlangTest) sie mituebersetzen kann. Spiel-Teil: ReflexionDirektweg.cs.
    //
    // Ursache (Spielcode, ReflectionCalculator + FindAndAggregateNextBatchJob):
    //   Stufe 1 schickt als Befehl 0 einen Direkt-Strahl Quelle -> Hoerer (_directPathCommandIndex = 0). Der Job jeder Stufe liest
    //   foundDirectPathClearThisBatch = previousStepSphereHits[directSpherecastCommandIndex].colliderInstanceID == 0.
    //   Den Index gibt ScheduleReflectionLevel aber in JEDER Stufe mit. Ab Stufe 2 liegen in _spherecastHits die Treffer der
    //   zusammengeschobenen Folge-Strahlen (_compactedSpherecastCommands): Index 0 ist der naechste Abschnitt irgendeines Reflexions-
    //   strahls oder - wenn keiner mehr laeuft - ein leerer Befehl. Fliegt dieser Strahl ins Freie (draussen fast immer), meldet das Spiel
    //   "Direktweg frei", IsDirectPathClear = true, Wertung R = 0. ProcessFinalBatchResults wertet nur die LETZTE Stufe aus.
    //   Wie viele Stufen bis zum Abschluss laufen, haengt am Bildtakt (OnUpdate plant die naechste Stufe, OnLateUpdate schliesst ab,
    //   sobald der aktuelle Job fertig ist). Folge: R springt bei derselben Lage zwischen 0 (falsch "frei") und dem echten Wert (0,77).
    //   Vanilla mittelt T/D/R (1+1+0)/3 = 0,67 gegen (1+1+0,77)/3 = 0,92 - kaum hoerbar. Der Physik-Modus nimmt das Minimum:
    //   -1 dB gegen -24 dB, 16 Spruenge >= 6 dB in 15 s im Log.
    // Loesung: das Ergebnis des Direkt-Strahls aus Stufe 1 sichern, bevor Stufe 2 geplant wird, und fuer die weiteren Stufen den
    //   Index auf -1 setzen (der Job meldet dann "nicht frei", der gesicherte Wert bleibt stehen, weil das Spiel den Merker nur auf
    //   true setzt). Damit gilt in jeder Stufe dasselbe, richtige Direktweg-Ergebnis.
    internal static class ReflexionDirekt
    {
        /// <summary>Vor dem Planen der naechsten Stufe: jetzt das Direktweg-Ergebnis der Stufe 1 sichern?
        /// Genau die Bedingungen, unter denen UpdateCalculationStep die Stufe 2 plant.</summary>
        internal static bool StufeEinsSichern(bool inArbeit, bool jobFertig, int stufe, int maxStufen, int direktIndex)
            => inArbeit && jobFertig && direktIndex >= 0 && stufe == 1 && stufe < maxStufen;

        /// <summary>Nachbildung des Spiels: Direktweg-Merker nach Abschluss in Stufe 'bis' (1-basiert).
        /// treffer0[k-1] = colliderInstanceID, die der Job der Stufe k am Direkt-Index liest
        /// (Stufe 1: der Direkt-Strahl; ab Stufe 2: Eintrag 0 der Folge-Strahlen; 0 = nichts getroffen).</summary>
        internal static bool SpielDirektFrei(int[] treffer0, int bis) => treffer0[bis - 1] == 0;

        /// <summary>Mit Korrektur: nur der Direkt-Strahl der Stufe 1 zaehlt, egal in welcher Stufe abgeschlossen wird.</summary>
        internal static bool KorrigiertDirektFrei(int[] treffer0, int bis) => treffer0[0] == 0;

        /// <summary>ReflectionCalculator.GetNormalizedPathScore: frei -> 0, kein Weg -> 1, sonst Weglaenge / Reichweite.</summary>
        internal static float Wertung(bool direktFrei, bool wegGefunden, float weglaenge, float reichweite)
        {
            if (direktFrei) return 0f;
            if (!wegGefunden || reichweite <= 0f) return 1f;
            float w = weglaenge / reichweite;
            return w < 0f ? 0f : (w > 1f ? 1f : w);
        }
    }
}
