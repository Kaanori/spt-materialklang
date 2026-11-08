using System.Collections.Generic;

namespace Materialklang
{
    // 0.8.3 - jede Quelle eines Behaelters behaelt fuer ihren Filter die Verdeckung, die das Spiel fuer SIE gerechnet hat.
    // Reine Rechnung ohne Unity, damit der Offline-Test (Materialklang-KlangTest) sie mituebersetzen kann. Spiel-Teil: QuellWirkung.cs.
    //
    // Ursache (Spielcode, gilt fuer Combined-, ContinuousPropagated- und Continuous-Occluder; Sandbox-Log 08.11.2026 13:51, Bot22):
    //   Ein Occluder rechnet fuer ALLE Quellen eines Behaelters (Schritte, Ausruestung, Atem, Waffe). In CalculateFinalOcclusionParams
    //   setzt er OcclusionEffect = Gesamtwert * GetEnvironmentFactor(Raum, SpatialSettings DIESER Quelle) - der Faktor haengt an der
    //   Klanggruppe (im Log: Charakter-Klaenge 2,0, Waffen-Klaenge 1,5). OcclusionEffect ist aber EIN Feld des Occluders.
    //   - Voller Weg (Aenderung > 0,05): je Quelle CalculateFinalOcclusionParams + ApplyOcclusionToSource -> jede Quelle ihr eigener Wert.
    //   - Gemerkter Weg (ApplyCachedParamsToContainer, kleine Aenderung): die Lautstaerke kommt je Quelle aus dem Merker, der Tiefpass
    //     wird aber in ApplyOcclusionToSource -> FilterCutoffCalculator.Compute aus dem Feld OcclusionEffect neu gerechnet - und das
    //     haelt den Wert der LETZTEN Quelle des vollen Durchgangs.
    //   Folge im Log: route=0.46 und vol=0.037 konstant, effect fuer die Schritte abwechselnd 0.92 (eigener Wert, 0.46 x 2,0) und 0.69
    //   (Wert der Waffe, 0.46 x 1,5), im Physik-Modus Tiefpass 1043 <-> 2235 Hz alle 2-3 Bilder (eine Oktave). Lautstaerke und Tiefpass
    //   stammten so auch aus verschiedenen Rechnungen (Befund 8 des Reviews). Vanilla hat denselben Fehler, seine Filterkurve ist flacher.
    // Loesung: nach CalculateFinalOcclusionParams die Wirkung je Quelle merken; vor ApplyOcclusionToSource die gemerkte Wirkung
    //   DIESER Quelle wieder einsetzen. Lautstaerke (gemerkt) und Tiefpass gehoeren damit zur selben Rechnung.
    internal sealed class WirkungJeQuelle
    {
        readonly Dictionary<int, float> werte = new Dictionary<int, float>(8);

        public int Anzahl => werte.Count;

        public void Merken(int quelle, float wirkung) { werte[quelle] = wirkung; }

        /// <summary>Wirkung fuer den Filter der Quelle: die gemerkte eigene, sonst 'aktuell' (Feld des Occluders).
        /// korrigiert = die eigene weicht von 'aktuell' ab (das Spiel haette den Wert einer anderen Quelle genommen).</summary>
        public float Fuer(int quelle, float aktuell, out bool bekannt, out bool korrigiert)
        {
            bekannt = werte.TryGetValue(quelle, out float w);
            if (!bekannt) { korrigiert = false; return aktuell; }
            korrigiert = System.Math.Abs(w - aktuell) > 0.005f;
            return w;
        }

        public void Leeren() => werte.Clear();
    }
}
