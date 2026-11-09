using System;
using System.Collections.Generic;
using System.Globalization;
using Audio.AmbientSubsystem;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;

namespace Materialklang
{
    // ---------------------------------------------------------------------------------------------
    //  15. Ambient emitters (0.8.5): kleine ortsfeste Umgebungsquellen mit physikalischem Abfall (1/r) statt hartem Ende.
    //
    //  Beleg (EFT 4.1.6, ilspycmd + Szene level511 "Custom_Sound"):
    //   - Audio.AmbientSubsystem.BaseAmbientSoundPlayer.Awake: Source.maxDistance = _maxDistance, rolloffMode = Custom,
    //     SetCustomCurve(CustomRolloff, _rolloffCurve). Rohes AudioSource, KEIN BetterSource -> laeuft weder ueber den
    //     SpatialAudioOccluder noch durch das Klang-Log.
    //   - Customs, AmbientPlayers/3DRP/AmbientOutCommonGroupBypass/ElectricSign (LoopAmbientSoundPlayer) bei (409.9, 5.5, 17.6):
    //     _maxDistance 13 m, Kurve (0|1) (0.40|0.503) (0.724|0.18) (1|0) -> bei 9.9 m (MARK 1) -17 dB, bei 12.8 m (MARK 2) -58 dB, ab 13 m stumm.
    //     Dazu AudioSourceCulling (AmbientAudioSystem): Quelle aus ab sqrt(max^2 + 25) = 13.9 m.
    //   - Weitere Kandidaten auf Customs: 13 x Waterdrops (SeasonAmbientSoundPlayer, 3.5-10 m), Bonfire (Loop, 15 m).
    //     Vogel-/Wind-/Wetter-Teppiche haben 25-300 m und bleiben unberuehrt (Schwelle "Max. original range").
    //  Umsetzung: Postfix auf BaseAmbientSoundPlayer.Awake setzt Kurve + Reichweite (UmgebungKern.Bauen); das Culling liest maxDistance
    //  laufend nach. Spline-/Zonen-Emitter (ScaleMaxDistance) wandern mit dem Hoerer und werden wieder auf das Original gesetzt.
    //  Keine Okklusion im Spiel (kein Occluder): verlaengerte Quellen daempft WandDaempfung.cs (17 / Wall damping for non-occluded sources).
    // ---------------------------------------------------------------------------------------------
    internal static class Umgebung
    {
        internal const string Sek = "15. Ambient emitters";
        internal static ConfigEntry<bool> Aktiv;
        internal static ConfigEntry<float> Faktor, MaxOriginal, TrimDb;

        sealed class Eintrag
        {
            public BaseAmbientSoundPlayer P;
            public AudioSource S;
            public AnimationCurve Alt;
            public float MaxAlt, MaxNeu, R0;
            public string Typ, Name;
            public string[] Namen;
            public QuellKlasse Klasse;
            public bool Umgestellt, Ausgeschlossen;
            public AnimationCurve Neu;
            public float LogZeit = -999f, LogD = -999f;
        }

        static readonly Dictionary<int, Eintrag> quellen = new Dictionary<int, Eintrag>(64);
        static readonly List<int> weg = new List<int>();
        static float naechsterTick;
        static bool gestoert;

        internal static bool Wirksam => Aktiv != null && Aktiv.Value && Physik.An;

        internal static void Binden(ConfigFile c)
        {
            Aktiv = c.Bind(Sek, "Enabled", true,
                "Small fixed ambient sources of the map (neon sign hum, campfire, dripping water ...) fall off like a real point source (-6 dB per doubling of distance) " +
                "instead of being cut off after a few metres. Right at the source they stay as loud as in the game. Works only while Physics Mode (section 0) is on. " +
                "Music, radio, room tone, weather and wind are not touched (room tone has its own 'Indoor room tone volume (%)'). Applies live.");
            Faktor = c.Bind(Sek, "Range factor", 4f,
                new ConfigDescription("New audible range = game range x this factor (at most 30 m, see '17. All fixed sources / Small sources: max. range'), with a soft fade at the end. " +
                    "1 = game range, only the curve changes. Also the factor of all small sources in section 17.",
                    new AcceptableValueRange<float>(1f, 10f)));
            MaxOriginal = c.Bind(Sek, "Max. original range (m)", 15f,
                new ConfigDescription("Only sources whose game range is at most this are changed (Customs: dripping water 3.5-10 m, neon sign 13 m, campfire 15 m; birds/wind 25-300 m).",
                    new AcceptableValueRange<float>(1f, 30f)));
            TrimDb = c.Bind(Sek, "Volume trim (dB)", 0f,
                new ConfigDescription("Level of these sources overall. Above 0 the level right at the source cannot exceed the game's maximum.",
                    new AcceptableValueRange<float>(-20f, 10f)));

            EventHandler neu = (_, __) => AllesNeu();
            Aktiv.SettingChanged += neu; Faktor.SettingChanged += neu; MaxOriginal.SettingChanged += neu; TrimDb.SettingChanged += neu;
            if (Physik.Aktiv != null) Physik.Aktiv.SettingChanged += neu;
            if (Plugin.Aktiv != null) Plugin.Aktiv.SettingChanged += neu;
        }

        internal static void Anwenden(Harmony h)
        {
            try
            {
                var awake = AccessTools.DeclaredMethod(typeof(BaseAmbientSoundPlayer), "Awake");
                var scale = AccessTools.DeclaredMethod(typeof(BaseAmbientSoundPlayer), "ScaleMaxDistance", new[] { typeof(float) });
                if (awake == null || scale == null)
                {
                    Plugin.Log.LogWarning("Ambient emitters: targets not found (game update?) -> section 15 off.");
                    return;
                }
                h.Patch(awake, postfix: new HarmonyMethod(typeof(Umgebung), nameof(NachAwake)));
                h.Patch(scale, postfix: new HarmonyMethod(typeof(Umgebung), nameof(NachScale)));
            }
            catch (Exception e) { Statistik.Fehler(e); Plugin.Log.LogError($"Ambient emitters: applying failed: {e.Message}"); }
        }

        internal static void SzeneGewechselt() { weg.Clear(); foreach (var kv in quellen) if (kv.Value.P == null) weg.Add(kv.Key); foreach (var k in weg) quellen.Remove(k); }

        // ---- Haken ----
        static void NachAwake(BaseAmbientSoundPlayer __instance)
        {
            if (gestoert) return;
            try
            {
                var s = __instance.Source;
                if (s == null) return;
                var t = __instance.transform;
                var namen = new List<string>(3) { __instance.gameObject.name };
                if (t.parent != null) { namen.Add(t.parent.name); if (t.parent.parent != null) namen.Add(t.parent.parent.name); }
                var e = new Eintrag
                {
                    P = __instance, S = s, Alt = __instance._rolloffCurve, MaxAlt = __instance._maxDistance,
                    Typ = __instance.GetType().Name, Name = __instance.gameObject.name, Namen = namen.ToArray()
                };
                quellen[__instance.GetInstanceID()] = e;
                Stellen(e);
            }
            catch (Exception ex) { Fehler(ex); }
        }

        static void NachScale(BaseAmbientSoundPlayer __instance)
        {
            // Zonen-/Spline-Emitter: Reichweite wird vom Spiel gefuehrt, Quelle wandert -> keine Punktquelle, Original behalten
            if (!quellen.TryGetValue(__instance.GetInstanceID(), out var e) || e.Ausgeschlossen) return;
            try
            {
                e.Ausgeschlossen = true;
                if (e.Umgestellt) { e.S.SetCustomCurve(AudioSourceCurveType.CustomRolloff, e.Alt); e.Umgestellt = false; }
                WandDaempfung.Melden(e.S, false, "ambient", e.Name);
                FesteQuellen.Melden(__instance.GetInstanceID(), new FesteQuellen.Meldung { Quelle = e.P, Weg = "ambient", Art = QuellRegelKern.Art(e.Name), MaxAlt = e.MaxAlt, Grund = "zone/spline emitter" });
            }
            catch (Exception ex) { Fehler(ex); }
        }

        static void Fehler(Exception ex)
        {
            Statistik.Fehler(ex);
            if (ex is MissingFieldException || ex is MissingMethodException || ex is TypeLoadException)
            {
                gestoert = true;
                Plugin.Log.LogError($"Ambient emitters: {ex.GetType().Name}: {ex.Message} - game changed? Section 15 off for this session.");
            }
        }

        internal static void Neu() => AllesNeu();

        static void AllesNeu()
        {
            weg.Clear();
            foreach (var kv in quellen)
            {
                if (kv.Value.P == null || kv.Value.S == null) { weg.Add(kv.Key); continue; }
                try { Stellen(kv.Value); } catch (Exception ex) { Fehler(ex); }
            }
            foreach (var k in weg) quellen.Remove(k);

        }

        static void Stellen(Eintrag e)
        {
            if (e.Ausgeschlossen) return;
            float null0 = e.Alt != null && e.Alt.length > 0 ? e.Alt.Evaluate(0f) : 0f;
            string grund = null;
            if (e.Alt == null || e.Alt.length < 2) grund = "no curve";
            else if (!UmgebungKern.Kandidat(e.Typ, e.P._spatialBlend, e.MaxAlt, MaxOriginal.Value, null0))
                grund = e.P._spatialBlend < 0.5f ? "2D" : null0 <= 0f ? "silent/invalid"
                      : UmgebungKern.Kandidat(e.Typ, 1f, 1f, 2f, 1f) ? "ambient > max. original range" : "event/weather player";
            else if (!Wirksam) grund = "section 15 off";
            // 0.8.5 (17): Klasse nach Namen; ohne Abschnitt 17 wie bisher: alles klein, Faktor 15, hoechstens 30 m
            QuellKlasse k = QuellKlasse.Klein;
            double faktor = Faktor.Value, grenze = UmgebungKern.Obergrenze;
            if (grund == null && FesteQuellen.Wirksam)
            {
                if (!QuellRegelKern.Einordnen(e.Namen, LautSignal.Liste, FesteQuellen.Mittel, FesteQuellen.Aus, out k, out string wort)) grund = "excluded '" + wort + "'";
                else
                {
                    if (k == QuellKlasse.Laut && !LautSignal.Wirksam) k = QuellKlasse.Klein;
                    FesteQuellen.Werte(k, out faktor, out grenze);
                }
            }
            double maxNeu = QuellRegelKern.Reichweite(e.MaxAlt, faktor, grenze);
            // ohne Lohnt-Pruefung wie bisher: Faktor 1 = nur die Kurve aendert sich
            var m = new FesteQuellen.Meldung { Quelle = e.P, Weg = "ambient", Art = QuellRegelKern.Art(e.Name), MaxAlt = e.MaxAlt, Grund = grund };
            if (grund != null)
            {
                if (e.Umgestellt)
                {
                    e.S.SetCustomCurve(AudioSourceCurveType.CustomRolloff, e.Alt);
                    e.S.maxDistance = e.MaxAlt;
                    e.Umgestellt = false;
                }
                FesteQuellen.Melden(e.P.GetInstanceID(), m);
                WandDaempfung.Melden(e.S, false, "ambient", e.Name);
                return;
            }
            var alt = e.Alt;
            Func<double, double> f = x => alt.Evaluate((float)x);
            var kk = UmgebungKern.Bauen(f, e.MaxAlt, maxNeu, TrimDb.Value, out double r0);
            var keys = new Keyframe[kk.Length];
            for (int i = 0; i < kk.Length; i++) keys[i] = new Keyframe(kk[i].T, kk[i].V, kk[i].In, kk[i].Out);
            e.Neu = new AnimationCurve(keys);
            e.MaxNeu = (float)maxNeu; e.R0 = (float)r0; e.Klasse = k;
            e.S.rolloffMode = AudioRolloffMode.Custom;
            e.S.SetCustomCurve(AudioSourceCurveType.CustomRolloff, e.Neu);
            e.S.maxDistance = e.MaxNeu;
            e.Umgestellt = true;
            m.An = true; m.Klasse = k; m.MaxNeu = e.MaxNeu; m.R0 = e.R0; m.Neu = e.Neu; m.Klang = e.S.clip != null ? e.S.clip.name : null;
            m.AltF = x => x >= 1.0 ? 0.0 : alt.Evaluate((float)x);
            FesteQuellen.Melden(e.P.GetInstanceID(), m);
            WandDaempfung.Melden(e.S, true, "ambient", e.Name);   // 0.8.5: keine Okklusion im Spiel -> eigene Wanddaempfung
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
                foreach (var e in quellen.Values)
                {
                    if (!e.Umgestellt || e.P == null || e.S == null || !e.S.isActiveAndEnabled || !e.S.isPlaying) continue;
                    float d = Vector3.Distance(h, e.S.transform.position);
                    if (d > radius || d >= e.MaxNeu) continue;
                    if (jetzt - e.LogZeit < 2f) continue;
                    if (Math.Abs(d - e.LogD) < 0.5f && jetzt - e.LogZeit < 10f) continue;
                    float vol = e.S.volume;
                    double neu = e.Neu.Evaluate(d / e.MaxNeu) * vol;
                    if (neu < 0.001) continue;   // unter -60 dB: unhoerbar
                    double alt = d >= e.MaxAlt ? 0 : e.Alt.Evaluate(d / e.MaxAlt) * vol;
                    e.LogZeit = jetzt; e.LogD = d;
                    var inv = CultureInfo.InvariantCulture;
                    KlangLog.Text("[Ambient] " + e.Name + " (" + (e.Typ == "AmbientSoundPlayer" ? "Random" : e.Typ.Replace("AmbientSoundPlayer", "")) + ", " + QuellRegelKern.Name(e.Klasse) + ") clip=" + (e.S.clip != null ? e.S.clip.name : "-") +
                                  " | d=" + d.ToString("F1", inv) + " m | range " + e.MaxAlt.ToString("0.#", inv) + " -> " + e.MaxNeu.ToString("0.#", inv) +
                                  " m, r0 " + e.R0.ToString("F1", inv) + " m | level " + Db(neu, inv) + " (game " + (alt <= 1e-6 ? "silent" : Db(alt, inv)) + ")");
                }
            }
            catch (Exception ex) { Fehler(ex); }
        }

        static string Db(double g, IFormatProvider inv) => FernschussKern.DbAusGain(g).ToString("F1", inv) + " dB";
    }
}
