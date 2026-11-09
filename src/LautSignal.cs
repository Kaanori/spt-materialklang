using System;
using System.Collections.Generic;
using System.Globalization;
using Audio.AudioCulling;
using Audio.RadioSystem;
using Audio.SpatialSystem;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;

namespace Materialklang
{
    // ---------------------------------------------------------------------------------------------
    //  16. Loud signals (0.8.5): Alarme/Sirenen der Karten mit physikalischer Reichweite statt hartem Ende nach 40-120 m.
    //
    //  Beleg (EFT 4.1.6, ilspycmd + Szene level520 "Shopping_Mall_Scripts", Klang-Log 08.11.2026 23:04, Interchange):
    //   - Ladenalarm: New_mechanics/Toggleable/Alarm_ON/AlarmSound/Alarm traegt SyncLoopSoundPlayer (PlayOneShot des Alarm-Clips im
    //     Takt auf 6 AudioSources Alarm_loop_1 ... Alarm_loop_2 (4)) und SourceOccluder (_groupType PropagatedExclusive,
    //     ContinuousPropagated). Szene: maxDistance 40 m (Alarm_loop_1, Alarm_loop_2) bzw. 50 m, Custom-Kurve
    //     (0.025|1) (0.357|0.34) (1|0) -> bei 14 m -9 dB, dann linear auf 0 bei 40 m.
    //   - SourceOccluder.CreateSourceWrappers -> BetterAudio.CreateBetterSourceWithParentSettings: SimpleSource auf derselben
    //     AudioSource, SetRolloff(maxDistance), Kurve der Szene bleibt; das SEB-Gruppenpreset (SoundRolloff) gilt fuer SimpleSource nicht.
    //   - Ende bei 40 m: AudioSourceContainer prueft sqrt(MaxDistance^2 + 25) = 40.3 m (Okklusion), AudioSourceCulling schaltet die
    //     Quelle bei sqrt(maxDistance^2 + 25) = 40.3 m ab - im Log ist 40.3 m der groesste Abstand (262 Zeilen, 7.2-40.3 m).
    //   - Weitere: KIBA-Alarm (Alarm_Kiba_Subscriber_loop, 50 m, gleiche Kurve), Labyrinth-Boss-Alarm (level552, 7 Quellen,
    //     120 m, 1/r-Kurve bis 0 bei 120 m). Nicht hier: die 29 Laden-Radios im Alarm (ClientBroadcastPlayer, 15-70 m, stellen
    //     Kurve/Reichweite selbst), Labs-Durchsagen (InteractiveSubscriber/QueuePlayer, Stereo).
    //  Umsetzung: Postfix auf SourceOccluder.CreateSourceWrappers; fuer Quellen, deren Name (Quelle, Eltern, Grosseltern, Clip)
    //  ein Stichwort trifft: neue Kurve (UmgebungKern.Bauen), BetterSource.SetRolloff(neu) und Reichweite des Containers.
    //  Okklusion/Raumwege/Luftdaempfung/Hoerbarkeit von True Acoustics wirken weiter (die Quelle bleibt im Occluder).
    // ---------------------------------------------------------------------------------------------
    internal static class LautSignal
    {
        internal const string Sek = "16. Loud signals";
        internal static ConfigEntry<bool> Aktiv;
        internal static ConfigEntry<float> Reichweite, TrimDb, Faktor;
        internal static ConfigEntry<string> Namen;

        sealed class Eintrag
        {
            public AudioSource S;
            public BetterSource W;
            public SyncLoopSoundPlayer Sync;
            public int ContId = -1;
            public AnimationCurve Alt, Neu;
            public AudioRolloffMode ModusAlt;
            public float MaxAlt, MinAlt, MaxNeu, R0;
            public string Name, Klang, Gruppe, Wort;
            public string[] Namen;
            public bool Radio, Umgestellt;
            public QuellKlasse Klasse;
            public Vector3 Pos;
            public float LogZeit = -999f, LogD = -999f;
        }

        static readonly Dictionary<int, Eintrag> quellen = new Dictionary<int, Eintrag>(64);
        static readonly List<int> weg = new List<int>();
        static string[] liste = new string[0];
        static float naechsterTick;
        static bool gestoert;

        internal static bool Wirksam => Aktiv != null && Aktiv.Value && Physik.An;
        internal static string[] Liste => liste;
        internal static bool Kennt(int audioSourceId) => quellen.ContainsKey(audioSourceId);

        internal static void Binden(ConfigFile c)
        {
            Aktiv = c.Bind(Sek, "Enabled", true,
                "Alarms, sirens and similar loud signals of the map (Interchange shop alarm and KIBA alarm, Labyrinth boss alarm ...) carry as far as " +
                "a real alarm bell (about 100-120 dB at 1 m): -6 dB per doubling of distance instead of ending after 40-120 m. Right at the source they stay " +
                "as loud as in the game; walls, rooms, doors and air absorption of True Acoustics still apply. Works only while Physics Mode (section 0) is on. Applies live.");
            Reichweite = c.Bind(Sek, "Range (m)", (float)LautSignalKern.Standard,
                new ConfigDescription("Largest audible range of these signals (game range x 'Range factor', at most this), with a soft fade over its last 25 %. Never shorter than the game's range (40-120 m).",
                    new AcceptableValueRange<float>(50f, (float)LautSignalKern.Grenze)));
            Faktor = c.Bind(Sek, "Range factor", (float)QuellRegelKern.LautFaktor,
                new ConfigDescription("New range = game range x this factor, at most 'Range (m)' (default 15: the 40-120 m alarms reach 'Range (m)'). Same rule as all fixed sources (section 17).",
                    new AcceptableValueRange<float>(1f, 20f)));
            TrimDb = c.Bind(Sek, "Volume trim (dB)", 0f,
                new ConfigDescription("Level of these signals overall. Above 0 the level right at the source cannot exceed the game's maximum.",
                    new AcceptableValueRange<float>(-20f, 10f)));
            Namen = c.Bind(Sek, "Name keywords", LautSignalKern.StandardListe,
                "Comma-separated keywords. A sound counts as a loud signal when one of them starts a word in the name of its audio source, its parent " +
                "or grandparent object or its clip (case does not matter: 'alarm' matches Alarm_loop_1 and KibaAlarm, 'horn' does not match Thorn). " +
                "Only sounds the game routes through its occlusion system (SourceOccluder) are checked; radios are never changed.");

            liste = LautSignalKern.ListeLesen(Namen.Value);
            EventHandler neu = (_, __) => AllesNeu();
            Aktiv.SettingChanged += neu; Reichweite.SettingChanged += neu; TrimDb.SettingChanged += neu; Faktor.SettingChanged += neu;
            Namen.SettingChanged += (_, __) => { liste = LautSignalKern.ListeLesen(Namen.Value); AllesNeu(); };
            if (Physik.Aktiv != null) Physik.Aktiv.SettingChanged += neu;
            if (Plugin.Aktiv != null) Plugin.Aktiv.SettingChanged += neu;
        }

        internal static void Anwenden(Harmony h)
        {
            try
            {
                var ziel = AccessTools.DeclaredMethod(typeof(SourceOccluder), "CreateSourceWrappers");
                if (ziel == null)
                {
                    Plugin.Log.LogWarning("Loud signals: target not found (game update?) -> section 16 off.");
                    return;
                }
                h.Patch(ziel, postfix: new HarmonyMethod(typeof(LautSignal), nameof(NachWrappers)));
            }
            catch (Exception e) { Statistik.Fehler(e); Plugin.Log.LogError($"Loud signals: applying failed: {e.Message}"); }
        }

        internal static void SzeneGewechselt()
        {
            weg.Clear();
            foreach (var kv in quellen) if (kv.Value.S == null) weg.Add(kv.Key);
            foreach (var k in weg) quellen.Remove(k);

        }

        // ---- Haken ----
        static void NachWrappers(SourceOccluder __instance)
        {
            if (gestoert) return;
            try
            {
                var src = __instance._sources;
                var wr = __instance._betterSourceWrappers;
                if (src == null || wr == null) return;
                var sync = __instance.GetComponentInParent<SyncLoopSoundPlayer>();
                for (int i = 0; i < src.Length && i < wr.Length; i++)
                {
                    var s = src[i];
                    var w = wr[i];
                    if (s == null || w == null) continue;
                    FesteQuellen.Abgeben(s);   // 0.8.5 (17): falls der Szenen-Durchlauf sie schon als rohe Quelle umgestellt hat
                    var t = s.transform;
                    var klang = sync != null && sync._clip != null ? sync._clip.name : (s.clip != null ? s.clip.name : null);
                    var namen = new List<string>(4) { s.gameObject.name };
                    if (t.parent != null) { namen.Add(t.parent.name); if (t.parent.parent != null) namen.Add(t.parent.parent.name); }
                    if (klang != null) namen.Add(klang);
                    var e = new Eintrag
                    {
                        S = s, W = w, Sync = sync, Name = s.gameObject.name, Klang = klang, Namen = namen.ToArray(),
                        ModusAlt = s.rolloffMode, Alt = s.GetCustomCurve(AudioSourceCurveType.CustomRolloff),
                        MaxAlt = w.MaxDistance > 0f ? w.MaxDistance : s.maxDistance, MinAlt = s.minDistance,
                        Gruppe = w.Preset != null ? w.Preset.Type.ToString() : "?",
                        Radio = s.GetComponentInParent<ClientBroadcastPlayer>() != null, Pos = t.position
                    };
                    if (__instance._containersIDBySourceInstanceID.TryGetValue(s.GetInstanceID(), out int id)) e.ContId = id;
                    quellen[s.GetInstanceID()] = e;
                    Stellen(e);
                }
            }
            catch (Exception ex) { Fehler(ex); }
        }

        static void Fehler(Exception ex)
        {
            Statistik.Fehler(ex);
            if (ex is MissingFieldException || ex is MissingMethodException || ex is TypeLoadException)
            {
                gestoert = true;
                Plugin.Log.LogError($"Loud signals: {ex.GetType().Name}: {ex.Message} - game changed? Section 16 off for this session.");
            }
        }

        internal static void Neu() => AllesNeu();

        static void AllesNeu()
        {
            weg.Clear();
            foreach (var kv in quellen)
            {
                if (kv.Value.S == null || kv.Value.W == null) { weg.Add(kv.Key); continue; }
                try { Stellen(kv.Value); } catch (Exception ex) { Fehler(ex); }
            }
            foreach (var k in weg) quellen.Remove(k);

        }

        /// <summary>Spielkurve als Funktion von x = d / MaxAlt (nach CreateBetterSourceWithParentSettings ist der Modus Custom).</summary>
        static Func<double, double> AltFunktion(Eintrag e) => FesteQuellen.AltFunktion(e.ModusAlt, e.Alt, e.MinAlt, e.MaxAlt);

        // 0.8.5 (17): alle Quellen des SourceOccluder nach der gemeinsamen Regel - Signale (Stichwort) wie bisher mit Abschnitt 16,
        // alle anderen (Maschinen, Generatoren, Server ...) mit Abschnitt 17. Ohne Abschnitt 17 genau wie bisher: nur Signale.
        static void Stellen(Eintrag e)
        {
            e.Wort = LautSignalKern.Treffer(e.Namen, liste);
            var altF = AltFunktion(e);
            float null0 = (float)altF(0.0);
            string grund = null;
            QuellKlasse k = QuellKlasse.Laut;
            if (e.Radio) grund = "radio";
            else if (e.ModusAlt == AudioRolloffMode.Custom && (e.Alt == null || e.Alt.length < 2)) grund = "no curve";
            else if (e.S.spatialBlend < 0.5f) grund = "2D";
            else if (!QuellRegelKern.Kandidat(e.S.spatialBlend, e.MaxAlt, null0)) grund = "silent/invalid";
            else if (e.Wort != null)
            {
                if (!Wirksam) grund = "section 16 off";
                else if (e.MaxAlt >= LautSignalKern.Grenze) grund = "already long";
            }
            else if (!FesteQuellen.Wirksam) grund = "section 17 off";
            else if (!QuellRegelKern.Einordnen(e.Namen, liste, FesteQuellen.Mittel, FesteQuellen.Aus, out k, out string wort)) grund = "excluded '" + wort + "'";
            double maxNeu = 0;
            if (grund == null)
            {
                FesteQuellen.Werte(k, out double f, out double g);
                maxNeu = QuellRegelKern.Reichweite(e.MaxAlt, f, g);
                if (k != QuellKlasse.Laut && !QuellRegelKern.Lohnt(e.MaxAlt, maxNeu)) grund = "already long";
            }
            var m = new FesteQuellen.Meldung { Quelle = e.S, Weg = "occluded", Art = QuellRegelKern.Art(e.Name), Klang = e.Klang, MaxAlt = e.MaxAlt, Grund = grund };
            if (grund != null)
            {
                if (e.Umgestellt)
                {
                    if (e.ModusAlt == AudioRolloffMode.Custom) e.S.SetCustomCurve(AudioSourceCurveType.CustomRolloff, e.Alt);
                    else e.S.rolloffMode = e.ModusAlt;
                    e.W.SetRolloff(e.MaxAlt);
                    ContainerReichweite(e);
                    FesteQuellen.TaktVergessen(e.ContId);
                    e.Umgestellt = false;
                }
                FesteQuellen.Melden(e.S.GetInstanceID(), m);
                return;
            }
            var kk = UmgebungKern.Bauen(altF, e.MaxAlt, maxNeu, k == QuellKlasse.Laut ? TrimDb.Value : FesteQuellen.TrimDb.Value, out double r0);
            var keys = new Keyframe[kk.Length];
            for (int i = 0; i < kk.Length; i++) keys[i] = new Keyframe(kk[i].T, kk[i].V, kk[i].In, kk[i].Out);
            e.Neu = new AnimationCurve(keys);
            e.MaxNeu = (float)maxNeu; e.R0 = (float)r0; e.Klasse = k;
            e.S.rolloffMode = AudioRolloffMode.Custom;
            e.S.SetCustomCurve(AudioSourceCurveType.CustomRolloff, e.Neu);
            e.W.SetRolloff(e.MaxNeu);   // BetterSource.MaxDistance + AudioSource.maxDistance (x OcclusionRolloffScale); das Culling liest nach
            ContainerReichweite(e);
            FesteQuellen.TaktMerken(e.ContId, e.W, e.Pos, e.MaxAlt);
            e.Umgestellt = true;
            m.An = true; m.Klasse = k; m.MaxNeu = e.MaxNeu; m.R0 = e.R0; m.AltF = altF; m.Neu = e.Neu;
            FesteQuellen.Melden(e.S.GetInstanceID(), m);
        }

        /// <summary>Reichweite des Okklusions-Containers neu (wie AudioSourceContainer nach AddSource: groesste MaxDistance seiner Quellen),
        /// sonst rechnet das Spiel die Okklusion ab 40.3 m nicht mehr nach.</summary>
        static void ContainerReichweite(Eintrag e)
        {
            if (e.ContId < 0 || !MonoBehaviourSingleton<SpatialAudioSystem>.Instantiated) return;
            if (!MonoBehaviourSingleton<SpatialAudioSystem>.Instance._allRegisteredSoundContainers.TryGetValue(e.ContId, out var c) || c == null) return;
            float m = 0f;
            var qs = c._sources;
            for (int i = 0; i < qs.Count; i++) if (qs[i] != null && qs[i].MaxDistance > m) m = qs[i].MaxDistance;
            if (m <= 0f) return;
            c.MaxDistance = m;
            c._sqrMaxDistance = m * m;
        }

        // ---- aus Plugin.Update: Zusammenfassung ins BepInEx-Log, Zeilen ins Klang-Log (gedrosselt) ----
        internal static void Tick()
        {
            if (gestoert || quellen.Count == 0) return;
            float jetzt = Time.time;
            if (jetzt < naechsterTick) return;
            naechsterTick = jetzt + 0.25f;
            try
            {
                // 0.8.5 (17): Zusammenfassung je Karte macht FesteQuellen fuer alle Wege
                if (!KlangLog.An) return;
                var ba = MonoBehaviourSingleton<BetterAudio>.Instance;
                var lt = ba != null ? ba.ListenerTransform : null;
                if (lt == null) return;
                Vector3 h = lt.position;
                float radius = KlangLog.Radius.Value;
                var inv = CultureInfo.InvariantCulture;
                foreach (var e in quellen.Values)
                {
                    if (!e.Umgestellt || e.S == null || !e.S.isActiveAndEnabled) continue;
                    if (e.Klasse != QuellKlasse.Laut) continue;   // Maschinen & Co.: eine Zeile je Art (FesteQuellen), sonst zu viele Zeilen
                    if (!e.S.isPlaying && (e.Sync == null || !e.Sync._isPlaying)) continue;
                    float d = Vector3.Distance(h, e.S.transform.position);
                    if (d > radius || d >= e.MaxNeu) continue;
                    if (jetzt - e.LogZeit < 2f) continue;
                    if (Math.Abs(d - e.LogD) < 0.5f && jetzt - e.LogZeit < 10f) continue;
                    double neu = e.Neu.Evaluate(d / e.MaxNeu);
                    if (neu < 0.001) continue;
                    double alt = d >= e.MaxAlt ? 0 : AltFunktion(e)(d / e.MaxAlt);
                    e.LogZeit = jetzt; e.LogD = d;
                    KlangLog.Text((e.Klasse == QuellKlasse.Laut ? "[Signal] " : "[Fixed] ") + e.Name + " ('" + (e.Klasse == QuellKlasse.Laut ? e.Wort : QuellRegelKern.Name(e.Klasse)) + "', " + e.Gruppe + ") clip=" + (e.Klang ?? "-") +
                                  " | d=" + d.ToString("F1", inv) + " m | range " + e.MaxAlt.ToString("0.#", inv) + " -> " + e.MaxNeu.ToString("0.#", inv) +
                                  " m, r0 " + e.R0.ToString("F1", inv) + " m | curve " + Db(neu, inv) + " (game " + (alt <= 1e-6 ? "silent" : Db(alt, inv)) + "), before occlusion");
                }
            }
            catch (Exception ex) { Fehler(ex); }
        }

        static string Db(double g, IFormatProvider inv) => FernschussKern.DbAusGain(g).ToString("F1", inv) + " dB";
    }
}
