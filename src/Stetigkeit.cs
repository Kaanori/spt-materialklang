using System;
using BepInEx.Configuration;

namespace Materialklang
{
    // 0.8.1 - gegen Knacken und springende Lautstaerke/Filter ("sprunghaft", Bericht Streets 08.11.2026).
    // Reine Rechnung ohne Unity, damit der Offline-Test (Materialklang-KlangTest) sie mituebersetzen kann.
    //
    // Ursache 1 - die Wertung kippt waehrend jeder Neuberechnung:
    //   SpatialAudioOcclusionCombinedCalculator.StartCalculation setzt die Merker _ranTransmission/_ranDiffraction/
    //   _ranReflection sofort fuer die NEUE Rechnung (erst nur T, dann T+D, dann T+D+R), LastAggregateResult bleibt aber
    //   bis zum Abschluss das ALTE Ergebnis. GetScore liest also ein paar Bilder lang altes Ergebnis + neue Merker.
    //   Vanilla mittelt (der Fehler bleibt dort klein, die Lautstaerkekurve endet bei -4,4 dB). Der Physik-Modus nimmt
    //   das Minimum: war der leichteste Weg die Reflexion (R = 0, freier Weg) und ist sie gerade "nicht gelaufen",
    //   springt die Wertung 0 -> 1, also 0 dB -> -30 dB und 22 kHz -> 800 Hz, und nach Abschluss der Rechnung zurueck.
    //   Die Rechnung startet neu, sobald sich Quelle oder Hoerer 0,25 m bewegen - bei gehenden Bots also staendig.
    //   Loesung: waehrend einer laufenden Rechnung gilt die Wertung der letzten FERTIGEN Rechnung.
    //
    // Ursache 2 - der Tiefpass springt statt zu gleiten:
    //   Aendert sich die Wirkung um weniger als effectChangeThreshold (0,05), wendet das Spiel die gemerkten Werte mit
    //   updateImmediately = true an (ApplyCachedParamsToContainer u. a.). Der Tiefpass springt dann auf den Zielwert,
    //   statt mit der Spiel-eigenen Rampe (AudioEQFilterBase: Lerp mit 5/s je Bild) weiterzugleiten. Mitten in einem
    //   laufenden Klang knackt ein solcher Sprung.
    //   Loesung: "sofort" nur noch, solange die Quelle nicht hoerbar laeuft (still oder gerade erst gestartet) oder das
    //   Spiel ihren Filter nicht jedes Bild nachfuehrt; sonst gleitet der Filter mit der Rampe des Spiels.
    //   Neue Klaenge bekommen ihren ersten Wert also weiter sofort, laufende gleiten.
    internal static class Stetigkeit
    {
        internal static ConfigEntry<bool> AktivCfg;
        internal static bool An => AktivCfg == null || AktivCfg.Value;

        /// <summary>So lange (Sekunden Abspielzeit) gilt ein Klang als "gerade gestartet": sein Filter darf noch springen.</summary>
        internal const float FrischSek = 0.05f;

        // Diagnose (alle 30 s ins Log)
        internal static int Gehalten, Gerechnet, Geglaettet, SofortGelassen;

        internal static void Binden(ConfigFile c)
        {
            AktivCfg = c.Bind("1. General", "Steady occlusion", true,
                "Keeps the occlusion steady while the game recalculates a sound path, and lets filter changes of a playing sound glide instead of jumping. " +
                "Fixes the crackling/clicking and jumping volume of 0.8.0. Off = 0.8.0 behaviour (only for comparison). Applies live.");
        }

        /// <summary>Kleinste Teilwertung der Wege, die gelaufen sind (Physik-Modus: der leichteste Weg zaehlt).
        /// Kein Weg gelaufen -> 0, wie GetScore im Spiel.</summary>
        internal static float Kleinste(bool t, bool d, bool r, float ts, float ds, float rs)
        {
            float m = float.MaxValue;
            if (t) m = Math.Min(m, ts);
            if (d) m = Math.Min(m, ds);
            if (r) m = Math.Min(m, rs);
            if (m == float.MaxValue) return 0f;
            return m < 0f ? 0f : (m > 1f ? 1f : m);
        }

        /// <summary>Welche Wertung gilt jetzt?
        /// fertig = die Merker gehoeren zum Ergebnis (Rechnung abgeschlossen oder noch nie gestartet).
        /// gemerkt/letzte = Wertung der letzten fertigen Rechnung (je Rechner, vom Aufrufer gehalten).
        /// gehalten = true, wenn waehrend einer laufenden Rechnung die letzte fertige Wertung geliefert wurde.</summary>
        internal static float Waehlen(bool fertig, bool t, bool d, bool r, float ts, float ds, float rs,
                                      ref bool gemerkt, ref float letzte, out bool gehalten)
        {
            gehalten = false;
            if (fertig)
            {
                letzte = Kleinste(t, d, r, ts, ds, rs);
                gemerkt = true;
                return letzte;
            }
            if (gemerkt) { gehalten = true; return letzte; }
            // erste Rechnung nach Start/Reset: das Ergebnis ist noch "frei" (Clear) -> wie bisher
            return Kleinste(t, d, r, ts, ds, rs);
        }

        /// <summary>Darf der Filter dieser Quelle sofort (ohne Rampe) gesetzt werden?
        /// jedesBild = das Spiel fuehrt Filter/Lautstaerke der Quelle jedes Bild nach (sonst bliebe ein Zielwert ohne Rampe liegen).
        /// 0.8.3: schleife = Endlos-Klang (AudioSource.loop); dessen Abspielzeit springt nach jedem Durchlauf auf 0 - er gilt nur dann
        /// als "gerade gestartet", wenn er beim letzten Blick noch nicht lief (schonGelaufen = false).</summary>
        internal static bool SofortZulassen(bool jedesBild, bool spielt, float sekImKlang, bool schleife = false, bool schonGelaufen = false)
            => !jedesBild || !spielt || (sekImKlang < FrischSek && !(schleife && schonGelaufen));

        // ---- Gleiten der Grenzfrequenzen (FilterGleiten.cs ersetzt damit die lineare Rampe des Spiels) ----
        // Das Spiel gleitet linear in Hz (Lerp 5/s je Bild). Abwaerts ist das langsam, aufwaerts aus dem Physik-Bereich
        // (800 Hz) springt es im ersten Bild um ~2 Oktaven (800 -> 3400 Hz bei 40 Bildern/s) - auch das knackt.
        // Gehoer und Filter arbeiten in Oktaven: darum hier exponentiell in log2(f) mit Zeitkonstante GleitTau und
        // hoechstens GleitMaxOktJeSek Oktaven je Sekunde (bei 40 Bildern/s 0,4 Oktaven je Bild; 800 Hz -> 22 kHz in ~0,3 s).
        internal const float GleitTau = 0.05f, GleitMaxOktJeSek = 16f;

        /// <summary>Ein Bild Gleiten von aktuell zu ziel (Hz), in Oktaven. Nahe am Ziel (unter 0,01 Oktaven) -> genau das Ziel.</summary>
        internal static float FrequenzSchritt(float aktuell, float ziel, float dt)
        {
            if (dt <= 0f) return aktuell;
            if (aktuell <= 1f || ziel <= 1f) return ziel;
            double o = Math.Log((double)ziel / aktuell, 2.0);
            if (Math.Abs(o) < 0.01) return ziel;
            double schritt = o * (1.0 - Math.Exp(-dt / GleitTau));
            double max = GleitMaxOktJeSek * dt;
            if (schritt > max) schritt = max; else if (schritt < -max) schritt = -max;
            return (float)(aktuell * Math.Pow(2.0, schritt));
        }

        // ---- Nachbildung der Spiel-Rampen (nur fuer den Offline-Test; im Spiel rechnet das Spiel selbst) ----

        /// <summary>AudioEQFilterBase.ManualUpdate: _currentLpf = Lerp(_currentLpf, _targetLpf, deltaTime * lerpSpeed), lerpSpeed 5.</summary>
        internal static float FilterSchritt(float aktuell, float ziel, float dt, float tempo = 5f)
        {
            float k = dt * tempo;
            if (k > 1f) k = 1f; else if (k < 0f) k = 0f;
            return aktuell + (ziel - aktuell) * k;
        }

        /// <summary>CombinedSpatialAudioOccluder: geglaettete Wirkung = Lerp(alt, wertung, smoothingFactor * dt) (smoothingFactor 20);
        /// faellt die Wertung auf 0, MoveTowards(alt, 0, smoothingFactor * dt).</summary>
        internal static float WirkungSchritt(float alt, float wertung, float dt, float glaettung = 20f)
        {
            float n = glaettung * dt;
            if (wertung <= 0f) return alt > n ? alt - n : 0f;
            if (n > 1f) n = 1f;
            float v = alt + (wertung - alt) * n;
            return v < 0f ? 0f : (v > 1f ? 1f : v);
        }
    }
}
