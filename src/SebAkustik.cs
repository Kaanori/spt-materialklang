using System;
using System.Reflection;
using System.Runtime.CompilerServices;
using Audio.SpatialSystem;
using Audio.SpatialSystem.SpatialAudioCalculator;
using Audio.SpatialSystem.Utils;
using BepInEx.Bootstrap;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;
using SEB = SoundEngineBackport.LiveEngine;

namespace Materialklang
{
    // ------------------------------------------------------------------------------------------
    // Drei Zusaetze rund um den Klang:
    //  6. Klangfarbe        - Materialien filtern Hoehen/Tiefen unterschiedlich (Spiel + SEB-Schuesse)
    //  7. Waffen-Richtwirkung - Schuesse klingen von hinten leiser/dumpfer (nur mit Sound Engine Backport)
    //  8. Luftdaempfung     - Hoehen gehen mit der Entfernung verloren (Spiel + SEB)
    // Alles SEB-Typisierte steht in SebTeil und wird nur geladen, wenn red.soundenginebackport da ist.
    // ------------------------------------------------------------------------------------------

    // Summen der Strahlen eines Durchgangs, ein Wert je Frequenzband (und die Bezugskurve).
    internal struct FarbeSumme
    {
        public double L, M, H, Lr, Hr;
        public int N;

        public void Frei() { L += 1; M += 1; H += 1; Lr += 1; Hr += 1; N++; }

        public void Add(int m, float x)
        {
            if (m >= Farbe.WL.Length) m = Farbe.WL.Length - 1;   // neue Gruppen (z.B. Tuer): neutral wie "Unbekannt"
            L += Math.Exp(-x * Farbe.WL[m]);
            M += Math.Exp(-x);
            H += Math.Exp(-x * Farbe.WH[m]);
            Lr += Math.Exp(-x * 0.77);
            Hr += Math.Exp(-x * 1.17);
            N++;
        }
    }

    // Ergebnis je TransmissionCalculator (Pool: der Rechner wird von wechselnden Quellen benutzt).
    internal sealed class Klangfarbe
    {
        public double L, M, H, Lr, Hr;
        public bool Gueltig;
        public int Frame;
    }

    internal sealed class OccZustand
    {
        public bool Init;
        public float ZielH, ZielL, H, L, Zeit;
    }

    internal sealed class SchussInfo
    {
        public bool Gueltig;
        public float DirLp = float.MaxValue;
        public float Abzug;          // Meter, die von der Entfernung fuer die Luftdaempfung abgezogen werden
        public float Faktor = 1f;    // Faktor auf die Entfernung (Ersatz, wenn die Bank keine Entfernungsgrenzen hat)
        public float W, Db;          // Klang-Log: Richtwirkung dieses Schusses (0 = zum Hoerer, 1 = weg) und ihre Lautstaerke in dB
        public bool Innen;           // Schuss faellt in einem Innenraum (kein Wald)
    }

    internal static class Farbe
    {
        // Reihenfolge wie enum Gruppe: Tiefen-/Hoehen-Gewicht je Material (Bezug: 0.77 / 1.17).
        internal static readonly float[] WL = { .6f, .6f, .8f, .5f, .6f, .65f, .75f, .9f, .75f, 1f, .7f, .5f, .77f, .65f, .8f };   // Tuer (wie dickes Holz), Schnee (wie Kies)
        internal static readonly float[] WH = { 1.5f, 1.5f, 1.3f, 1.3f, 1.0f, 1.25f, 1.15f, 1.45f, 1.4f, 1f, 1.4f, 1.6f, 1.17f, 1.25f, 1.3f };

        internal static ConfigEntry<bool> AktivCfg;
        internal static ConfigEntry<float> Staerke;
        internal static bool Bereit;

        public static bool Aktiv => Bereit && AktivCfg.Value && Staerke.Value > 0f;

        static readonly ConditionalWeakTable<TransmissionCalculator, Klangfarbe> Tabelle =
            new ConditionalWeakTable<TransmissionCalculator, Klangfarbe>();
        static readonly ConditionalWeakTable<SpatialAudioOccluder, OccZustand> Zustaende =
            new ConditionalWeakTable<SpatialAudioOccluder, OccZustand>();

        // ---- Aufruf aus TransmissionPatch.Postfix ----
        public static void Setze(TransmissionCalculator c, in FarbeSumme s, bool gueltig)
        {
            var k = Tabelle.GetValue(c, _ => new Klangfarbe());
            k.L = s.L; k.M = s.M; k.H = s.H; k.Lr = s.Lr; k.Hr = s.Hr;
            k.Gueltig = gueltig && s.N > 0;
            k.Frame = Time.frameCount;
            if (k.Gueltig) SebAkustik.FarbeRechnungen++;
        }

        // Prefix TransmissionCalculator.StartCalculation: Pool-Wiederverwendung, altes Ergebnis ist ungueltig.
        static void StartVor(TransmissionCalculator __instance)
        {
            if (Tabelle.TryGetValue(__instance, out var k)) k.Gueltig = false;
        }

        // Postfix CombinedSpatialAudioOccluder.SetAudioSourceContainer: neuer Besitzer -> Glaettung neu starten.
        static void CombinedNeu(CombinedSpatialAudioOccluder __instance)
        {
            if (Zustaende.TryGetValue(__instance, out var z)) { z.Init = false; z.ZielH = z.ZielL = 0f; }
        }

        // Prefix MultiSourceSpatialAudioOccluder.OnUpdateOcclusionEffects
        static void MultiVor(MultiSourceSpatialAudioOccluder __instance, AudioSourceContainer sourceContainer)
        {
            Kontext.MultiCalc = null;
            try
            {
                if (__instance._activeTransmissionCalculatorsByContID.TryGetValue(sourceContainer.ID, out var c))
                    Kontext.MultiCalc = c as TransmissionCalculator;
            }
            catch (Exception ex) { Statistik.Fehler(ex); }
        }

        static Exception MultiEnde(Exception __exception)
        {
            Kontext.MultiCalc = null;
            return __exception;
        }

        // Prefix SpatialAudioOccluder.ApplyOcclusionToSource: wer fragt, und welche Klangfarbe gilt dafuer?
        // 0.8.1: ausserdem "sofort setzen" nur noch fuer Quellen, die noch nicht hoerbar laufen (Stetigkeit.cs, Ursache 2).
        static void KontextVor(SpatialAudioOccluder __instance, BetterSource source, ref bool updateImmediately)
        {
            if (updateImmediately) SofortPruefen(__instance, source, ref updateImmediately);
            Kontext.Quelle = source;
            Kontext.HatFarbe = false;
            Kontext.dH = Kontext.dL = 0f;
            Kontext.WaldDa = false; Kontext.VolGeaendert = false;
            WaldVor(__instance, source);
            if (!Aktiv) return;
            try { FarbeBerechnen(__instance); }
            catch (Exception ex) { Statistik.Fehler(ex); }
        }

        // Das Spiel setzt den Filter bei kleinen Aenderungen (gemerkte Werte, ApplyCachedParamsToContainer u. a.) mit
        // updateImmediately = true - ohne Rampe. Laeuft der Klang schon hoerbar und fuehrt das Spiel seinen Filter jedes Bild
        // nach, gleitet er stattdessen mit der Spiel-eigenen Rampe (AudioEQFilterBase, Lerp 5/s). Neue oder stille Klaenge
        // (und die Einmal-Klaenge des MultiSource-Occluders, die stumm starten) bekommen ihren Wert weiter sofort.
        static void SofortPruefen(SpatialAudioOccluder occ, BetterSource q, ref bool sofort)
        {
            try
            {
                if (q == null || !Plugin.Aktiv.Value || !Stetigkeit.An || occ is MultiSourceSpatialAudioOccluder) return;
                var a = q.source1;
                if (a == null) return;
                bool jedesBild = q._isSubscribed && a.enabled && q.gameObject.activeInHierarchy;
                bool spielt = jedesBild && a.isPlaying;
                if (Stetigkeit.SofortZulassen(jedesBild, spielt, spielt ? a.time : 0f)) { Stetigkeit.SofortGelassen++; return; }
                sofort = false;
                Stetigkeit.Geglaettet++;
            }
            catch (Exception ex) { Statistik.Fehler(ex); }
        }

        static Exception KontextEnde(Exception __exception)
        {
            if (Kontext.VolGeaendert)
            {
                // die Lautstaerke wurde nur fuer diesen Aufruf abgesenkt (die Quelle hat sie schon); die Rechenwerte bleiben wie vom Spiel
                try { Kontext.Occ.CurrentParameters.OcclusionVolumeFactor = Kontext.VolOrig; } catch { }
                Kontext.VolGeaendert = false;
            }
            Kontext.Occ = null;
            Kontext.WaldDa = false;
            Kontext.Quelle = null;
            Kontext.HatFarbe = false;
            return __exception;
        }

        // Laubdaempfung (Wald.cs): Pegel hier (vor dem Setzen der Lautstaerke an der Quelle), Tiefpass in ComputeNach bzw. BlendNach.
        static void WaldVor(SpatialAudioOccluder occ, BetterSource q)
        {
            try
            {
                if (!Wald.Bereit || !Wald.Aktiv.Value) return;
                // nur im Freien: Quelle und Hoerer beide in Innenraeumen -> kein Wald dazwischen
                var qr = occ.SourceContainer != null ? occ.SourceContainer.CurrentAudioRoom : null;
                var hr = occ.AudioSystem != null ? occ.AudioSystem.ListenerCurrentRoom : null;
                if (qr != null && hr != null && !qr.IsOutdoor && !hr.IsOutdoor) return;
                var ba = MonoBehaviourSingleton<BetterAudio>.Instance;
                var lt = ba != null ? ba.ListenerTransform : null;
                if (lt == null) return;
                Vector3 h = lt.position, p = q.Position;
                float dl = SebAkustik.LuftAktiv.Value ? Vector3.Distance(p, h) : 0f;
                if (!Wald.Berechnen(q.GetInstanceID(), p, h, dl, out var r)) return;
                Kontext.WaldE = r; Kontext.WaldDa = true;
                if (r.Db > 0.01f)
                {
                    var cp = occ.CurrentParameters;
                    Kontext.Occ = occ; Kontext.VolOrig = cp.OcclusionVolumeFactor;
                    cp.OcclusionVolumeFactor = Kontext.VolOrig * Wald.Verstaerkung(r.Db);
                    Kontext.VolGeaendert = true;
                }
                if (KlangLog.An && Wald.ImLog.Value) { KlangLog.WaldM = r.Meter; KlangLog.WaldDb = -r.Db; KlangLog.WaldHz = r.LpHz; }
            }
            catch (Exception ex) { Statistik.Fehler(ex); }
        }

        static void Farbwerte(Klangfarbe k, out float dH, out float dL)
        {
            const double eps = 1e-300;
            double h = 10.0 * Math.Log10(Math.Max(k.H, eps) / Math.Max(k.Hr, eps));
            double l = 10.0 * Math.Log10(Math.Max(k.L, eps) / Math.Max(k.Lr, eps));
            dH = (float)Math.Max(-30.0, Math.Min(30.0, h));
            dL = (float)Math.Max(-30.0, Math.Min(30.0, l));
        }

        static void FarbeBerechnen(SpatialAudioOccluder occ)
        {
            if (occ is CombinedSpatialAudioOccluder c)
            {
                var z = Zustaende.GetValue(c, _ => new OccZustand());
                if (c.IsPropagationActive)
                {
                    // Schall kommt durch Tueren/Fenster, nicht durch Material
                    z.Init = false; z.ZielH = z.ZielL = 0f;
                    return;
                }
                var calc = c._obstructionCalculator as SpatialAudioOcclusionCombinedCalculator;
                if (calc != null && calc._transmissionCalculator != null
                    && Tabelle.TryGetValue(calc._transmissionCalculator, out var k) && k.Gueltig)
                {
                    Farbwerte(k, out float dH, out float dL);
                    // Anteil der Durchdringung am Gesamtwert (Mittel aus Durchdringung/Beugung/Reflexion)
                    var r = calc.LastAggregateResult;
                    float summe = r.transmissionScore + r.diffractionScore + r.reflectionScore;
                    float anteil = summe > 1e-4f ? Mathf.Clamp01(r.transmissionScore / summe) : 0f;
                    z.ZielH = dH * anteil;
                    z.ZielL = dL * anteil;
                }
                // sonst (Rechnung laeuft gerade): letztes Ziel behalten, kein Flackern
                float jetzt = Time.time;
                if (!z.Init)
                {
                    z.H = z.ZielH; z.L = z.ZielL; z.Zeit = jetzt; z.Init = true;
                }
                else
                {
                    float dt = jetzt - z.Zeit;
                    if (dt > 0f)
                    {
                        float f = Mathf.Clamp01(20f * dt);
                        z.H = Mathf.Lerp(z.H, z.ZielH, f);
                        z.L = Mathf.Lerp(z.L, z.ZielL, f);
                        z.Zeit = jetzt;
                    }
                }
                Kontext.HatFarbe = true;
                Kontext.dH = z.H;
                Kontext.dL = z.L;
            }
            else if (occ is MultiSourceSpatialAudioOccluder)
            {
                var tc = Kontext.MultiCalc;
                if (tc != null && Tabelle.TryGetValue(tc, out var k) && k.Gueltig)
                {
                    Farbwerte(k, out float dH, out float dL);
                    Kontext.HatFarbe = true;
                    Kontext.dH = dH;
                    Kontext.dL = dL;
                }
            }
        }

        // Postfix FilterCutoffCalculator.Compute: Klangfarbe + Luftdaempfung auf die vom Spiel berechneten Grenzfrequenzen.
        static void ComputeNach(float occlusion, ref float lowPassFreq, ref float highPassFreq)
        {
            var q = Kontext.Quelle;
            if (q == null) return;
            try
            {
                float lp = lowPassFreq, hp = highPassFreq;
                if (Kontext.HatFarbe && occlusion > 0.001f && !Physik.An)   // Physik: Massengesetz-Tilt steckt im Tiefpass
                {
                    float s = Staerke.Value;
                    lp = Mathf.Clamp(lp * Mathf.Pow(2f, s * Kontext.dH / 12f), 150f, 20000f);
                    hp = Mathf.Clamp(hp * Mathf.Pow(2f, -s * Kontext.dL / 12f), 20f, lp);
                    SebAkustik.FarbeAngewandt++;
                    SebAkustik.FarbeSummeH += Math.Abs(Kontext.dH);
                    SebAkustik.FarbeSummeL += Math.Abs(Kontext.dL);
                }
                if (SebAkustik.LuftOk && SebAkustik.LuftAktiv.Value && !SebAkustik.IstSebQuelle(q))
                {
                    float d = Entfernung(q);
                    float fc = Luft.Grenzfrequenz(d);
                    if (KlangLog.An) KlangLog.LuftHz = fc;
                    if (fc < lp)
                    {
                        lp = fc;
                        if (hp > lp) hp = lp;
                        SebAkustik.LuftAndere++;
                        SebAkustik.LuftAndereSummeFc += fc;
                    }
                }
                if (Kontext.WaldDa && !SebAkustik.IstSebQuelle(q) && Kontext.WaldE.LpHz < lp)
                {
                    lp = Kontext.WaldE.LpHz;
                    if (hp > lp) hp = lp;
                }
                lowPassFreq = lp;
                highPassFreq = hp;
            }
            catch (Exception ex) { Statistik.Fehler(ex); }
        }

        internal static float Entfernung(BetterSource q)
        {
            var ba = MonoBehaviourSingleton<BetterAudio>.Instance;
            var lt = ba != null ? ba.ListenerTransform : null;
            if (lt == null) return 0f;
            return Vector3.Distance(q.Position, lt.position);
        }
    }

    // Hier steht, wer den Filter gerade neu rechnen laesst.
    internal static class Kontext
    {
        internal static BetterSource Quelle;
        internal static bool HatFarbe;
        internal static float dH, dL;
        internal static TransmissionCalculator MultiCalc;
        // Wald (Laubdaempfung) fuer die Quelle, die gerade verarbeitet wird
        internal static bool WaldDa;
        internal static WaldErgebnis WaldE;
        internal static SpatialAudioOccluder Occ;
        internal static float VolOrig;
        internal static bool VolGeaendert;
    }

    internal static class Luft
    {
        // fc(d) = 12 kHz bei 19 m, faellt mit d^(-2/3); der "Air clarity factor" (frueher "Humidity") skaliert es (mehr = klarer).
        // Quelle: ISO 9613-1:1993. Bei 20 C / 70 % r.F. erreicht die Luftdaempfung 3 dB auf 19 m bei ~11,7 kHz,
        // auf 100 m bei ~4,7 kHz. Die Kurve d^(-2/3) ist eine Naeherung (faellt etwas steiler), Untergrenze 2,5 kHz
        // und der Faktor sind nach Gehoer. Achtung: TROCKENE Luft daempft die Hoehen STAERKER (ISO 9613-1, 20 C, 8 kHz:
        // ~217 dB/km bei 20 % r.F., ~59 dB/km bei 100 %); der Faktor ist daher keine "Feuchte", sondern ein Klarheitsfaktor.
        // Seit 0.7.0: mit "Use raid weather (ISO 9613-1)" (Standard) kommt die Grenzfrequenz stattdessen aus der ISO-9613-1-Rechnung
        // mit Temperatur/Druck des Raids und geschaetzter Feuchte (Wetter.cs / Luftabsorption.cs): Frequenz, bei der die Luft ueber
        // die Entfernung 3 dB daempft. Klarheitsfaktor und Grenzen 2,5 .. 22 kHz gelten unveraendert.
        public static float Grenzfrequenz(float d)
        {
            d = Mathf.Max(d, 0.5f);
            float fc = Wetter.An
                ? Wetter.Grenzfrequenz(d) * SebAkustik.Feuchte.Value
                : 12000f * Mathf.Pow(19f / d, 2f / 3f) * SebAkustik.Feuchte.Value;
            return Mathf.Clamp(fc, 2500f, 22000f);
        }
    }

    internal static class SebAkustik
    {
        internal static ConfigEntry<bool> RichtAktiv, LuftAktiv;
        internal static ConfigEntry<float> RueckDb, RueckLpf, InnenFaktor, Feuchte, SchussAbzug;   // Feuchte = "Air clarity factor" (frueher "Humidity")

        internal static bool RichtOk, LuftOk, SebLuftOk;

        // Zustand des laufenden Schusses (alles Hauptthread, synchron)
        internal static bool RichtGueltig;
        internal static float RichtW;
        internal static SchussInfo Aktuell = new SchussInfo();

        // Diagnose
        internal static int FarbeRechnungen, FarbeAngewandt, LuftAndere, RichtSchuesse, SebLuft;
        internal static double FarbeSummeH, FarbeSummeL, LuftAndereSummeFc, RichtSummeW, RichtSummeDb, SebLuftSummeFc;

        static Func<BetterSource, bool> sebPruefer;

        internal static bool IstSebQuelle(BetterSource q) => sebPruefer != null && sebPruefer(q);

        internal static void Binden(ConfigFile c)
        {
            const string s6 = "6. Timbre";
            Farbe.AktivCfg = c.Bind(s6, "Enabled", true,
                "Materials filter highs and lows differently: concrete swallows the highs and lets the thump through, wood sounds more hollow. Only for sound through walls (not through a door gap). Has no effect while Physics Mode is on (its low-pass already carries the tilt). Applies live (to new calculations).");
            Farbe.Staerke = c.Bind(s6, "Strength", 1f,
                new ConfigDescription("How strongly the timbre shifts the cut-off frequencies (1 = 12 dB difference = one octave).", new AcceptableValueRange<float>(0f, 3f)));

            const string s7 = "7. Weapon Directivity";
            RichtAktiv = c.Bind(s7, "Enabled", true,
                "Other people's shots sound quieter and duller from behind (muzzle pointing away from you). Needs Sound Engine Backport; only shots of others, not your own.");
            RueckDb = c.Bind(s7, "Rear side (dB)", -4f,
                new ConfigDescription("Loudness when the weapon fires exactly away from you (0 = no change).", new AcceptableValueRange<float>(-12f, 0f)));
            RueckLpf = c.Bind(s7, "Rear low-pass (Hz)", 6000f,
                new ConfigDescription("High-frequency limit when the weapon fires exactly away from you (22000 = no change).", new AcceptableValueRange<float>(1000f, 22000f)));
            InnenFaktor = c.Bind(s7, "Indoor factor", 0.5f,
                new ConfigDescription("Indoors the direction matters less (walls throw the sound back). 1 = as outdoors, 0 = no directivity indoors.", new AcceptableValueRange<float>(0f, 1f)));

            const string s8 = "8. Air Absorption";
            LuftAktiv = c.Bind(s8, "Enabled", true,
                "Highs are lost with distance (low-pass: about 12 kHz at 19 m, falling after that, at least 2.5 kHz). The base game does not have this. With \"Use raid weather\" the curve follows the raid's temperature, pressure and estimated humidity.");
            Feuchte = c.Bind(s8, "Air clarity factor", 1f,
                new ConfigDescription("Factor on the cut-off frequency of the air low-pass (the setting was called \"Humidity\" before 0.6.0; same effect, your value is carried over). 1 = the curve as calculated (with \"Use raid weather\": ISO 9613-1 for the raid weather; otherwise the fixed curve, about 20 C / 70 % humidity). Below 1 = duller (more absorption, as in dry air: at 20 C, 8 kHz is absorbed with ~217 dB/km at 20 % humidity but only ~59 dB/km at 100 %). Above 1 = clearer (damp air).", new AcceptableValueRange<float>(0.3f, 3f)));
            SchussAbzug = c.Bind(s8, "Shot offset", 1f,
                new ConfigDescription("Shots (Sound Engine Backport) already have the air baked into their distant recordings. 1 = air only counts beyond the last distance limit of the recording, 0 = full distance.", new AcceptableValueRange<float>(0f, 1f)));
            Wetter.Binden(c, s8);
        }

        // ---------------------------------------------------------------------------------------
        internal static void Anwenden(Harmony h)
        {
            try { AnwendenIntern(h); }
            catch (Exception ex)
            {
                Statistik.Fehler(ex);
                Plugin.Log.LogError($"SEB add-on: applying failed: {ex.Message}");
            }
        }

        static MethodBase Ziel(Type t, string name, Type[] args = null)
        {
            if (t == null) { Plugin.Log.LogWarning($"SEB add-on: type for {name} not found."); return null; }
            var m = args == null ? AccessTools.DeclaredMethod(t, name) : AccessTools.DeclaredMethod(t, name, args);
            if (m == null) Plugin.Log.LogWarning($"SEB add-on: target {t.Name}.{name} not found.");
            return m;
        }

        static HarmonyMethod HM(Type host, string name)
        {
            var m = AccessTools.DeclaredMethod(host, name);
            if (m == null) throw new MissingMethodException(host.Name, name);
            return new HarmonyMethod(m);
        }

        static void AnwendenIntern(Harmony h)
        {
            bool seb = Chainloader.PluginInfos.ContainsKey("red.soundenginebackport");

            // ---- Spiel-Teil: gemeinsamer Kontext (Klangfarbe + Luftdaempfung) ----
            var apply = Ziel(typeof(SpatialAudioOccluder), "ApplyOcclusionToSource", new[] { typeof(BetterSource), typeof(bool) });
            var compute = Ziel(typeof(FilterCutoffCalculator), "Compute");
            bool gemeinsam = apply != null && compute != null;
            if (gemeinsam)
            {
                h.Patch(apply, prefix: HM(typeof(Farbe), "KontextVor"), finalizer: HM(typeof(Farbe), "KontextEnde"));
                h.Patch(compute, postfix: HM(typeof(Farbe), "ComputeNach"));
            }
            else Plugin.Log.LogWarning("SEB add-on: timbre and air absorption (game sounds) off - context targets not found.");
            LuftOk = gemeinsam;

            // ---- Klangfarbe ----
            var start = Ziel(typeof(TransmissionCalculator), "StartCalculation");
            var multi = Ziel(typeof(MultiSourceSpatialAudioOccluder), "OnUpdateOcclusionEffects");
            var setc = Ziel(typeof(CombinedSpatialAudioOccluder), "SetAudioSourceContainer");
            if (gemeinsam && start != null && multi != null && setc != null)
            {
                h.Patch(start, prefix: HM(typeof(Farbe), "StartVor"));
                h.Patch(multi, prefix: HM(typeof(Farbe), "MultiVor"), finalizer: HM(typeof(Farbe), "MultiEnde"));
                h.Patch(setc, postfix: HM(typeof(Farbe), "CombinedNeu"));
                Farbe.Bereit = true;
            }
            else Plugin.Log.LogWarning("SEB add-on: timbre off - targets not found.");

            // ---- SEB-Teile ----
            if (!seb)
            {
                Plugin.Log.LogInfo("SEB add-on: Sound Engine Backport not found - weapon directivity and gunshot air absorption off.");
            }
            else SebAnwenden(h);

            Plugin.Log.LogInfo($"SEB add-on: timbre {(Farbe.Bereit ? "ready" : "off")}, directivity {(RichtOk ? "ready" : "off")}, " +
                               $"air absorption game {(LuftOk ? "ready" : "off")} / SEB {(SebLuftOk ? "ready" : "off")}.");
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        static void SebAnwenden(Harmony h)
        {
            var fire = Ziel(AccessTools.TypeByName("SoundEngineBackport.LiveEngine.LiveWeaponSoundEngine"), "FireBullet");
            var tss = Ziel(AccessTools.TypeByName("SoundEngineBackport.LiveEngine.GunshotEmitterBase"), "TryStartShot");
            var prep = Ziel(AccessTools.TypeByName("SoundEngineBackport.LiveEngine.LiveFilterBlend"), "PrepareShot");
            var comp = Ziel(AccessTools.TypeByName("SoundEngineBackport.LiveEngine.FilterBlend"), "Compute");

            RichtOk = fire != null && tss != null && prep != null && comp != null;
            SebLuftOk = tss != null && prep != null && comp != null;
            if (!RichtOk) Plugin.Log.LogWarning("SEB add-on: weapon directivity off - targets not found.");
            if (!SebLuftOk) Plugin.Log.LogWarning("SEB add-on: air absorption for SEB sources off - targets not found.");

            if (RichtOk || SebLuftOk)
            {
                h.Patch(tss, prefix: HM(typeof(SebTeil), "SchussVor"), finalizer: HM(typeof(SebTeil), "SchussEnde"));
                h.Patch(prep, prefix: HM(typeof(SebTeil), "VorbereitenVor"));
                h.Patch(comp, postfix: HM(typeof(SebTeil), "BlendNach"));
                SebTeil.PrueferSetzen();
            }
            if (RichtOk)
                h.Patch(fire, prefix: HM(typeof(SebTeil), "FeuerVor"), finalizer: HM(typeof(SebTeil), "FeuerEnde"));
        }

        internal static void SetzePruefer(Func<BetterSource, bool> f) => sebPruefer = f;

        // ---- Diagnose: eine Zeile alle 30 s, wenn etwas passiert ist ----
        internal static void Loggen()
        {
            if (FarbeRechnungen + FarbeAngewandt + LuftAndere + RichtSchuesse + SebLuft == 0) return;
            var teile = new System.Collections.Generic.List<string>();
            if (FarbeRechnungen + FarbeAngewandt > 0)
                teile.Add($"timbre {FarbeRechnungen} calculations, applied {FarbeAngewandt}x" +
                          (FarbeAngewandt > 0 ? $" (mean |dH| {FarbeSummeH / FarbeAngewandt:F1} dB, |dL| {FarbeSummeL / FarbeAngewandt:F1} dB)" : ""));
            if (RichtSchuesse > 0)
                teile.Add($"directivity {RichtSchuesse} shots (mean w {RichtSummeW / RichtSchuesse:F2}, {RichtSummeDb / RichtSchuesse:F1} dB)");
            if (LuftAndere > 0)
                teile.Add($"air game {LuftAndere}x (mean {LuftAndereSummeFc / LuftAndere:F0} Hz)");
            if (SebLuft > 0)
                teile.Add($"air SEB {SebLuft}x (mean {SebLuftSummeFc / SebLuft:F0} Hz)");
            Plugin.Log.LogInfo("[Diagnostics] SEB add-on: " + string.Join(", ", teile));
            FarbeRechnungen = FarbeAngewandt = LuftAndere = RichtSchuesse = SebLuft = 0;
            FarbeSummeH = FarbeSummeL = LuftAndereSummeFc = RichtSummeW = RichtSummeDb = SebLuftSummeFc = 0;
        }
    }

    // ---------------------------------------------------------------------------------------
    // Alles, was Typen aus dem Sound Engine Backport nennt. Wird nur geladen, wenn SEB da ist.
    // ---------------------------------------------------------------------------------------
    internal static class SebTeil
    {
        sealed class BlendInfo
        {
            public BetterSource Quelle;
            public float DirLp = float.MaxValue;
            public float Abzug;
            public float Faktor = 1f;
            public float W, Db;
            public bool Innen;
        }

        static readonly ConditionalWeakTable<SEB.FilterBlend, BlendInfo> Tabelle =
            new ConditionalWeakTable<SEB.FilterBlend, BlendInfo>();

        [MethodImpl(MethodImplOptions.NoInlining)]
        internal static void PrueferSetzen() =>
            SebAkustik.SetzePruefer(q => SEB.LiveFilterBlend.Registry.ContainsKey(q));

        static float DbZuGain(float db) => Mathf.Pow(10f, db / 20f);

        // Prefix LiveWeaponSoundEngine.FireBullet(Ammo ammo, Vector3 shotPosition, Vector3 shotDirection, float pitchMult, bool multiShot, bool burstOf2Start)
        static void FeuerVor(Vector3 shotPosition, Vector3 shotDirection)
        {
            SebAkustik.RichtGueltig = false;
            if (!SebAkustik.RichtAktiv.Value) return;
            try
            {
                var ba = MonoBehaviourSingleton<BetterAudio>.Instance;
                var lt = ba != null ? ba.ListenerTransform : null;
                if (lt == null) return;
                Vector3 zumOhr = lt.position - shotPosition;
                float w = 0f;   // 0 = Muendung zeigt zum Hoerer, 1 = genau weg
                if (zumOhr.sqrMagnitude > 1e-6f && shotDirection.sqrMagnitude > 1e-6f)
                {
                    float dot = Vector3.Dot(shotDirection.normalized, zumOhr.normalized);
                    w = Mathf.Clamp01((1f - dot) * 0.5f);
                }
                SebAkustik.RichtW = w;
                SebAkustik.RichtGueltig = true;
            }
            catch (Exception ex) { Statistik.Fehler(ex); }
        }

        static Exception FeuerEnde(Exception __exception)
        {
            SebAkustik.RichtGueltig = false;
            return __exception;
        }

        // Prefix GunshotEmitterBase.TryStartShot(ShotPlaybackContext context, double scheduled, out SuperSource source, out float length)
        // (der Context kommt als Wert; "ref" im Prefix schreibt die geaenderte Fassung zurueck)
        static void SchussVor(ref SEB.ShotPlaybackContext context)
        {
            var info = SebAkustik.Aktuell = new SchussInfo();
            try
            {
                info.Gueltig = true;
                info.Innen = context.Environment == EnvironmentType.Indoor;
                if (context.IsFirstPerson) return;

                if (SebAkustik.RichtOk && SebAkustik.RichtAktiv.Value && SebAkustik.RichtGueltig)
                {
                    float w = SebAkustik.RichtW;
                    float innen = context.Environment == EnvironmentType.Indoor ? SebAkustik.InnenFaktor.Value : 1f;
                    // Richtwirkung des Muendungsknalls ist gemessen (Beck, Nakasone & Marr, JASA 129, 1748-1759, 2011),
                    // die Kurve (w^1.3, -4 dB, 6 kHz, Innenfaktor) ist aber nach Gehoer eingestellt, nicht daraus abgeleitet.
                    float db = SebAkustik.RueckDb.Value * Mathf.Pow(w, 1.3f) * innen;
                    info.DirLp = 22000f * Mathf.Pow(SebAkustik.RueckLpf.Value / 22000f, w * w * innen);
                    if (db < -0.01f)
                    {
                        context = new SEB.ShotPlaybackContext(
                            context.Bank, context.DistanceToListener, context.MaxDistance,
                            context.BaseVolume * DbZuGain(db), context.Environment, context.IsFirstPerson,
                            context.Delay, context.BeatDuration, context.PitchA, context.PitchB, context.IsSilenced);
                    }
                    info.W = w; info.Db = db;
                    SebAkustik.RichtSchuesse++;
                    SebAkustik.RichtSummeW += w;
                    SebAkustik.RichtSummeDb += db;
                }

                // Luftdaempfung: die Fernaufnahmen der Bank enthalten die Luft bis zur letzten Entfernungsgrenze schon.
                var opt = context.Bank != null ? context.Bank.BlendOptions : null;
                float[] werte = opt != null ? opt.Values : null;
                if (werte != null && werte.Length > 0)
                {
                    float letzte = 0f;
                    for (int i = 0; i < werte.Length; i++) letzte = Mathf.Max(letzte, werte[i]);
                    info.Abzug = letzte * SebAkustik.SchussAbzug.Value;
                    info.Faktor = 1f;
                }
                else
                {
                    info.Abzug = 0f;
                    info.Faktor = 0.5f;   // keine Grenzen bekannt: halbe Entfernung als Naeherung
                }
            }
            catch (Exception ex) { Statistik.Fehler(ex); }
        }

        static Exception SchussEnde(Exception __exception)
        {
            SebAkustik.Aktuell = new SchussInfo();
            return __exception;
        }

        // Prefix LiveFilterBlend.PrepareShot(bool spatialEnabled): merkt sich je Quelle, was fuer den Schuss gilt.
        // Als Prefix, weil PrepareShot selbst schon einmal rechnet.
        static void VorbereitenVor(SEB.LiveFilterBlend __instance, bool spatialEnabled)
        {
            try
            {
                var info = Tabelle.GetValue(__instance._blend, _ => new BlendInfo());
                info.Quelle = __instance._source;
                var a = SebAkustik.Aktuell;
                if (a.Gueltig)
                {
                    info.DirLp = spatialEnabled ? a.DirLp : float.MaxValue;
                    info.Abzug = a.Abzug;
                    info.Faktor = a.Faktor;
                    info.W = a.W; info.Db = a.Db;
                    info.Innen = a.Innen;
                }
                else
                {
                    info.DirLp = float.MaxValue;
                    info.Abzug = 0f;
                    info.Faktor = 1f;
                    info.W = 0f; info.Db = 0f;
                    info.Innen = false;
                }
            }
            catch (Exception ex) { Statistik.Fehler(ex); }
        }

        // Postfix FilterBlend.Compute(): FilterTargets
        static void BlendNach(SEB.FilterBlend __instance, ref SEB.FilterTargets __result)
        {
            try
            {
                if (!Tabelle.TryGetValue(__instance, out var info) || info.Quelle == null) return;
                float alt = __result.LowPass;
                float neu = alt;
                float luftHz = float.NaN;
                if (SebAkustik.RichtOk && SebAkustik.RichtAktiv.Value && info.DirLp < neu) neu = info.DirLp;
                if (SebAkustik.SebLuftOk && SebAkustik.LuftAktiv.Value)
                {
                    float d = Mathf.Max(0f, Farbe.Entfernung(info.Quelle) * info.Faktor - info.Abzug);
                    float fc = Luft.Grenzfrequenz(d);
                    luftHz = fc;
                    if (fc < neu)
                    {
                        neu = fc;
                        SebAkustik.SebLuft++;
                        SebAkustik.SebLuftSummeFc += fc;
                    }
                }
                float waldM = float.NaN, waldHz = float.NaN;
                if (Wald.Bereit && Wald.Aktiv.Value && !info.Innen)
                {
                    var ba = MonoBehaviourSingleton<BetterAudio>.Instance;
                    var lt = ba != null ? ba.ListenerTransform : null;
                    if (lt != null)
                    {
                        Vector3 p = info.Quelle.Position, h = lt.position;
                        float dl = SebAkustik.SebLuftOk && SebAkustik.LuftAktiv.Value ? Mathf.Max(0f, Vector3.Distance(p, h) * info.Faktor - info.Abzug) : 0f;
                        if (Wald.Berechnen(info.Quelle.GetInstanceID() ^ 0x40000000, p, h, dl, out var wr))
                        {
                            waldM = wr.Meter; waldHz = wr.LpHz;
                            if (wr.LpHz < neu) neu = wr.LpHz;
                        }
                    }
                }
                if (neu < alt)
                {
                    __result.LowPass = neu;
                    __result.LowPassQ = 1f;
                    if (__result.HighPass > neu) __result.HighPass = neu;
                }
                if (KlangLog.An)
                    KlangLog.SebMerken(info.Quelle, info.W, info.Db, info.DirLp, luftHz, __result.LowPass, waldM, waldHz);
            }
            catch (Exception ex) { Statistik.Fehler(ex); }
        }
    }
}
