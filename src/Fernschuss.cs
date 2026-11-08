using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
using BepInEx.Bootstrap;
using BepInEx.Configuration;
using EFT;
using HarmonyLib;
using UnityEngine;
using SEB = SoundEngineBackport.LiveEngine;

namespace Materialklang
{
    // ------------------------------------------------------------------------------------------
    // 13. Distance (SEB) - 0.8.4: ferne Schuesse besser hoerbar (bewusst weniger realistisch), nahe Schuesse unveraendert.
    // Belegte Stellen in SEB 1.0.3 / EFT (ilspycmd):
    //  - LiveWeaponAudioSettings.Get: MaxDistance draussen 600 m, drinnen 200, gedaempft 300/120 (Konstanten)
    //    -> wird im Prefix von GunshotEmitterBase.TryStartShot im ShotPlaybackContext ersetzt (nur draussen, ungedaempft, fremde Schuesse).
    //  - GunshotEmitterBase.TryStartShot: Bank.PickClipsByDistance(...) waehlt nah/mittel/fern nach Bank.BlendOptions.Values,
    //    dann SetRolloff(MaxDistance). Die Kurve (CustomRolloff) bekommt jede Quelle beim Anlegen aus Preset.SoundRolloff, das SEB in
    //    LiveGroupPresets.Apply (Postfix BetterAudio.LoadGroupPreset) ersetzt. Eine Kurve je Gruppe gilt fuer ALLE MaxDistance-Werte
    //    (x = d / MaxDistance) - deshalb setzt der Postfix von TryStartShot die neue Kurve je Schuss auf genau diese Quelle
    //    (draussen/ungedaempft), alle anderen Schuesse bekommen wieder die Preset-Kurve; Prefix BetterSource.Release stellt sie zurueck.
    //  - SoundBank.PickClipsByDistance: Stufe 3 = Mitte*p + Fern*(1-p), Stufe 4 = nur Fern. Ist die Fernaufnahme leer (gedaempfte
    //    Banks), blendet das Spiel ab der 3. Grenze ins Leere und ist ab der 4. stumm ("[shot-silent] no clip").
    //    -> Postfix (nur waehrend TryStartShot): mittlere Aufnahme voll bzw. neu waehlen.
    //  - Explosionen: SEB ersetzt die "Grenades"-Kurve durch eine steilere (-15 dB bei 800 m gegenueber SPT). Erster Postfix
    //    (vor SEB) merkt sich die SPT-Kurve, letzter Postfix (nach SEB) setzt sie wieder ein -> ab dem naechsten Raid.
    // Fern-Ausgleich: die Fernaufnahmen sind 3,7-6,5 dB weniger dicht (RMS 50 ms, AK-74M/AKM/TX-15/STM-9). Das sitzt in der Aufnahme,
    // also wird es je Bank an deren eigener Blend-Stufe ausgeglichen (FernschussKern.Ausgleich) - nicht pauschal in Metern.
    // Die Quellenlautstaerke (source.volume) kann es nicht tragen: SEB setzt ~0,75 und Unity begrenzt auf 1 (+2,5 dB).
    // ------------------------------------------------------------------------------------------
    internal static class Fernschuss
    {
        internal const string Sek = "13. Distance (SEB)";
        internal static ConfigEntry<bool> Aktiv, MitteBehalten, ExplosionOriginal;
        internal static ConfigEntry<float> FernDb, MaxDraussen;

        internal static bool Bereit, Gestoert;

        // Zustand des laufenden TryStartShot (Hauptfaden, synchron)
        internal static SoundBank SchussBank;
        internal static bool SchussMitte;

        // Diagnose (30 s)
        internal static int Verstaerkt, MitteGenutzt, Zurueck;
        internal static readonly HashSet<string> BanksVerstaerkt = new HashSet<string>(), BanksMitte = new HashSet<string>();
        internal static string ExplosionMeldung;

        // Klang-Log: Pegelaenderung je Quelle
        sealed class Merk { public float Db; public bool Mitte; public float Zeit; }
        static readonly Dictionary<int, Merk> JeQuelle = new Dictionary<int, Merk>(64);

        internal static void Binden(ConfigFile c)
        {
            Aktiv = c.Bind(Sek, "Enabled", false,
                "Distant gunshots carry further (deliberately less realistic): own volume curve beyond 25 m, longer audible range outdoors, " +
                "suppressed shots no longer fade out at 55-100 m, flatter explosion curve. Nearby shots (up to 25 m) stay exactly as in Sound Engine Backport. " +
                "Needs Sound Engine Backport 1.0.x. Gunshots: applies to the next shot; explosions: from the next raid.");
            FernDb = c.Bind(Sek, "Far clip compensation (dB)", 4f,
                new ConfigDescription("The far recordings of a weapon are 3.7-6.5 dB less dense than the mid ones (measured: AK-74M, AKM, TX-15, STM-9), " +
                    "so shots got a dip where the game blends to them (usually 55-100 m). This raises the far recording by this much, weighted with its share in the blend. 0 = off.",
                    new AcceptableValueRange<float>(0f, 8f)));
            MaxDraussen = c.Bind(Sek, "Max. distance outdoors (m)", 900f,
                new ConfigDescription("Up to this distance unsuppressed shots are audible outdoors (Sound Engine Backport: 600 m). Indoors and suppressed stay as in SEB.",
                    new AcceptableValueRange<float>(300f, 1500f)));
            MitteBehalten = c.Bind(Sek, "Suppressed: keep mid clip", true,
                "Suppressed weapons have no far recording, so the game blended them out between about 55 and 100 m and was silent beyond. " +
                "On = the mid recording keeps playing up to the max. distance (SEB: 300 m outdoors, 120 m indoors). Applies to every SEB gunshot bank without a far recording.");
            ExplosionOriginal = c.Bind(Sek, "Explosions: SPT curve", true,
                "Use the flatter SPT volume curve for grenades and explosions instead of Sound Engine Backport's steeper one (SEB: about 15 dB quieter at 800 m). From the next raid.");
        }

        internal static void Anwenden(Harmony h)
        {
            try
            {
                if (!Chainloader.PluginInfos.TryGetValue("red.soundenginebackport", out var pi))
                {
                    Plugin.Log.LogInfo("Distance (SEB): Sound Engine Backport not found - section 13 off.");
                    return;
                }
                if (!SebSchutz.VersionPasst(pi.Metadata.Version))
                {
                    Plugin.Log.LogWarning($"Distance (SEB): Sound Engine Backport {pi.Metadata.Version} found, built for 1.0.x -> section 13 off.");
                    return;
                }
                FernschussSeb.Anwenden(h);
            }
            catch (Exception ex)
            {
                Statistik.Fehler(ex);
                Plugin.Log.LogError($"Distance (SEB): applying failed: {ex.Message}");
            }
        }

        internal static void Abschalten(string teil, Exception ex)
        {
            if (Gestoert) return;
            Gestoert = true;
            Plugin.Log.LogError($"Distance (SEB): {teil} failed ({ex.GetType().Name}: {ex.Message}) - Sound Engine Backport probably changed. " +
                                "Section 13 is off for this session; shots play as SEB intends.");
        }

        internal static void Fehler(string teil, Exception ex)
        {
            if (SebSchutz.Strukturfehler(ex)) Abschalten(teil, ex); else Statistik.Fehler(ex);
        }

        // ---- SoundBank.PickClipsByDistance (Postfix): nur waehrend eines SEB-Schusses, nur fuer dessen Bank ----
        static void ClipNach(SoundBank __instance, ref AudioClip clip1, ref AudioClip clip2, ref float proportions, int environment, float distance)
        {
            if (SchussBank == null || !ReferenceEquals(__instance, SchussBank)) return;
            try
            {
                var opt = __instance.BlendOptions;
                var env = __instance.Environments;
                if (opt == null || env == null) return;
                int ei = __instance.HasEnvironment ? environment : 0;
                if (ei < 0 || ei >= env.Length || env[ei] == null || env[ei].Length < 3) return;
                var mitte = env[ei][1];
                var fern = env[ei][2];
                bool fernLeer = fern == null || fern.Length == 0, mitteLeer = mitte == null || mitte.Length == 0;
                switch (FernschussKern.ClipWahl(opt.Values, distance, fernLeer, mitteLeer))
                {
                    case ClipEntscheid.MitteVoll:
                        clip2 = null; proportions = 1f; SchussMitte = true; break;
                    case ClipEntscheid.MitteNehmen:
                        clip1 = null; __instance.SelectRandomClip(mitte, ref clip1);
                        clip2 = null; proportions = 1f; SchussMitte = clip1 != null; break;
                }
            }
            catch (Exception ex) { Statistik.Fehler(ex); }
        }

        // ---- BetterSource.Release (Prefix): eigene Kurve zuruecknehmen, bevor die Quelle in den Pool des Spiels geht ----
        static void FreigabeVor(BetterSource __instance)
        {
            if (!FernschussSeb.HatMarke(__instance)) return;
            try { FernschussSeb.Zuruecksetzen(__instance); Zurueck++; }
            catch (Exception ex) { Statistik.Fehler(ex); }
        }

        // ---- BetterAudio.LoadGroupPreset: SPT-Kurve fuer Explosionen ----
        static AnimationCurve grenadesSpt;

        static void PresetVorSeb(string groupName, AudioGroupPreset __result)
        {
            if (grenadesSpt != null || groupName != "Grenades" || __result == null || __result.SoundRolloff == null || Gestoert) return;
            try
            {
                if (FernschussSeb.IstSebKurve("Grenades", __result.SoundRolloff)) return;   // schon von SEB ersetzt (zweites Laden)
                grenadesSpt = new AnimationCurve(__result.SoundRolloff.keys);
                Plugin.Log.LogInfo($"Distance (SEB): SPT explosion curve saved ({grenadesSpt.length} keys).");
            }
            catch (Exception ex) { Fehler("explosion curve (save)", ex); }
        }

        static void PresetNachSeb(string groupName, AudioGroupPreset __result)
        {
            if (groupName != "Grenades" || __result == null || Gestoert) return;
            if (!Aktiv.Value || !ExplosionOriginal.Value) { ExplosionMeldung = "SEB curve"; return; }
            if (grenadesSpt == null) { ExplosionMeldung = "SPT curve not available (SEB curve kept)"; return; }
            __result.SoundRolloff = new AnimationCurve(grenadesSpt.keys);
            ExplosionMeldung = "SPT curve";
            Plugin.Log.LogInfo("Distance (SEB): group \"Grenades\" uses the SPT volume curve instead of SEB's (this raid).");
        }

        // ---- Klang-Log ----
        internal static void Merken(BetterSource q, float db, bool mitte)
        {
            if (!KlangLog.An || q == null) return;
            JeQuelle[q.GetInstanceID()] = new Merk { Db = db, Mitte = mitte, Zeit = Time.time };
        }

        /// <summary>Fuer die Zeile im Klang-Log: Pegelaenderung (dB, NaN = keine) und ob die mittlere Aufnahme weiterlief.</summary>
        internal static float LogDb(BetterSource q, out bool mitte)
        {
            mitte = false;
            if (q == null || !JeQuelle.TryGetValue(q.GetInstanceID(), out var m)) return float.NaN;
            if (Time.time - m.Zeit > 10f) { JeQuelle.Remove(q.GetInstanceID()); return float.NaN; }
            mitte = m.Mitte;
            return m.Db;
        }

        internal static void SzeneGewechselt() => JeQuelle.Clear();

        // ---- Diagnose: eine Zeile alle 30 s, wenn etwas passiert ist ----
        internal static void Loggen()
        {
            if (!Bereit) return;
            if (Verstaerkt + MitteGenutzt == 0 && ExplosionMeldung == null) return;
            var t = new List<string>();
            if (Verstaerkt > 0) t.Add($"{Verstaerkt} shots with distance curve (max {MaxDraussen.Value:F0} m, far clip +{FernDb.Value:F1} dB; banks {Liste(BanksVerstaerkt)})");
            if (MitteGenutzt > 0) t.Add($"{MitteGenutzt} shots kept the mid clip (no far clip; banks {Liste(BanksMitte)})");
            if (ExplosionMeldung != null) t.Add($"group Grenades: {ExplosionMeldung}");
            if (Zurueck > 0) t.Add($"{Zurueck} sources reset to the SEB curve");
            Plugin.Log.LogInfo("[Diagnostics] Distance (SEB): " + string.Join(", ", t));
            Verstaerkt = MitteGenutzt = Zurueck = 0; ExplosionMeldung = null;
            BanksVerstaerkt.Clear(); BanksMitte.Clear();
        }

        static string Liste(HashSet<string> s) => s.Count == 0 ? "-" : string.Join(", ", s);

        internal static void BankMerken(HashSet<string> s, SoundBank b)
        {
            if (b != null && s.Count < 12) s.Add(b.name);
        }

        // Harmony-Hilfen fuer FernschussSeb (Ziele werden hier gesucht, damit ein fehlendes Ziel nur eine Warnung ist)
        internal static MethodBase Ziel(Type t, string name, Type[] args = null)
        {
            if (t == null) { Plugin.Log.LogWarning($"Distance (SEB): type for {name} not found."); return null; }
            var m = args == null ? AccessTools.DeclaredMethod(t, name) : AccessTools.DeclaredMethod(t, name, args);
            if (m == null) Plugin.Log.LogWarning($"Distance (SEB): target {t.Name}.{name} not found.");
            return m;
        }

        internal static HarmonyMethod HM(Type host, string name, int prioritaet = -1, string[] vor = null, string[] nach = null)
        {
            var m = AccessTools.DeclaredMethod(host, name);
            if (m == null) throw new MissingMethodException(host.Name, name);
            var hm = new HarmonyMethod(m);
            if (prioritaet >= 0) hm.priority = prioritaet;
            if (vor != null) hm.before = vor;
            if (nach != null) hm.after = nach;
            return hm;
        }

        internal static void ClipZielPatchen(Harmony h)
        {
            var pick = Ziel(typeof(SoundBank), "PickClipsByDistance");
            var rel = Ziel(typeof(BetterSource), "Release");
            var load = Ziel(typeof(BetterAudio), "LoadGroupPreset");
            if (pick == null || rel == null || load == null) throw new MissingMethodException("Distance (SEB): game targets missing");
            h.Patch(pick, postfix: HM(typeof(Fernschuss), "ClipNach"));
            h.Patch(rel, prefix: HM(typeof(Fernschuss), "FreigabeVor"));
            h.Patch(load, postfix: HM(typeof(Fernschuss), "PresetVorSeb", Priority.First, vor: new[] { "red.soundenginebackport" }));
            h.Patch(load, postfix: HM(typeof(Fernschuss), "PresetNachSeb", Priority.Last, nach: new[] { "red.soundenginebackport" }));
        }
    }

    // ---------------------------------------------------------------------------------------
    // Alles, was SEB-Typen nennt. Zugriffe in NoInlining-Methoden, deren Aufruf im try liegt (siehe SebSchutz).
    // ---------------------------------------------------------------------------------------
    internal static class FernschussSeb
    {
        sealed class Marke { public AnimationCurve Kurve; }
        static readonly ConditionalWeakTable<BetterSource, Marke> Marken = new ConditionalWeakTable<BetterSource, Marke>();

        sealed class KurveFertig { public AnimationCurve Kurve; public KurvenPunkt[] Punkte; }
        static readonly Dictionary<string, KurveFertig> Kurven = new Dictionary<string, KurveFertig>();
        static AnimationCurve sebRef;
        static KurvenPunkt[] sebPunkte;
        static float sebMax = -1f;

        [MethodImpl(MethodImplOptions.NoInlining)]
        internal static void Anwenden(Harmony h)
        {
            var tss = Fernschuss.Ziel(AccessTools.TypeByName("SoundEngineBackport.LiveEngine.GunshotEmitterBase"), "TryStartShot");
            if (tss == null) { Plugin.Log.LogWarning("Distance (SEB): TryStartShot not found - section 13 off."); return; }
            Fernschuss.ClipZielPatchen(h);
            h.Patch(tss, prefix: Fernschuss.HM(typeof(FernschussSeb), "SchussVor"),
                         postfix: Fernschuss.HM(typeof(FernschussSeb), "SchussNach"),
                         finalizer: Fernschuss.HM(typeof(FernschussSeb), "SchussEnde"));
            Fernschuss.Bereit = true;
            Plugin.Log.LogInfo("Distance (SEB): ready - patched GunshotEmitterBase.TryStartShot (max. distance, curve per shot), " +
                               "SoundBank.PickClipsByDistance (mid clip without far clip), BetterAudio.LoadGroupPreset (explosion curve), BetterSource.Release (reset). " +
                               $"Section 13 is {(Fernschuss.Aktiv.Value ? "on" : "off")}.");
        }

        static bool Gilt(bool indoor, bool silenced, bool fp) => !indoor && !silenced && !fp;

        // Prefix TryStartShot: MaxDistance draussen ersetzen, Bank fuer die Clip-Wahl merken
        static void SchussVor(ref SEB.ShotPlaybackContext context)
        {
            Fernschuss.SchussBank = null; Fernschuss.SchussMitte = false;
            if (Fernschuss.Gestoert || !Fernschuss.Aktiv.Value) return;
            try { Vor(ref context); }
            catch (Exception ex) { Fernschuss.SchussBank = null; Fernschuss.Fehler("shot preparation", ex); }
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        static void Vor(ref SEB.ShotPlaybackContext context)
        {
            if (Fernschuss.MitteBehalten.Value) Fernschuss.SchussBank = context.Bank;
            if (!Gilt(context.Environment == EnvironmentType.Indoor, context.IsSilenced, context.IsFirstPerson)) return;
            float neu = Fernschuss.MaxDraussen.Value;
            if (Mathf.Abs(neu - context.MaxDistance) < 0.01f) return;
            context = new SEB.ShotPlaybackContext(
                context.Bank, context.DistanceToListener, neu,
                context.BaseVolume, context.Environment, context.IsFirstPerson,
                context.Delay, context.BeatDuration, context.PitchA, context.PitchB, context.IsSilenced);
        }

        // Postfix TryStartShot: Kurve fuer genau diese Quelle
        static void SchussNach(SEB.ShotPlaybackContext context, ref SuperSource source, bool __result)
        {
            bool mitte = Fernschuss.SchussMitte;
            Fernschuss.SchussBank = null; Fernschuss.SchussMitte = false;
            if (Fernschuss.Gestoert || !__result || source == null) return;
            try { Nach(context, source, mitte); }
            catch (Exception ex) { Fernschuss.Fehler("shot curve", ex); }
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        static void Nach(SEB.ShotPlaybackContext context, SuperSource source, bool mitte)
        {
            if (mitte)
            {
                Fernschuss.MitteGenutzt++;
                Fernschuss.BankMerken(Fernschuss.BanksMitte, context.Bank);
            }
            bool an = Fernschuss.Aktiv.Value && Gilt(context.Environment == EnvironmentType.Indoor, context.IsSilenced, context.IsFirstPerson);
            KurveFertig k = an ? Holen(source, context) : null;
            if (k == null)
            {
                if (HatMarke(source)) Zuruecksetzen(source);
                Fernschuss.Merken(source, float.NaN, mitte);
                return;
            }
            var m = Marken.GetValue(source, _ => new Marke());
            if (!ReferenceEquals(m.Kurve, k.Kurve))
            {
                source.source1.SetCustomCurve(AudioSourceCurveType.CustomRolloff, k.Kurve);
                if (source.source2 != null) source.source2.SetCustomCurve(AudioSourceCurveType.CustomRolloff, k.Kurve);
                m.Kurve = k.Kurve;
            }
            Fernschuss.Verstaerkt++;
            Fernschuss.BankMerken(Fernschuss.BanksVerstaerkt, context.Bank);
            if (KlangLog.An)
                Fernschuss.Merken(source, (float)FernschussKern.Unterschied(k.Punkte, context.MaxDistance, sebPunkte, sebMax, context.DistanceToListener), mitte);
        }

        static Exception SchussEnde(Exception __exception)
        {
            Fernschuss.SchussBank = null; Fernschuss.SchussMitte = false;
            return __exception;
        }

        /// <summary>Kurve fuer MaxDistance + Blend-Grenzen + Fern-Ausgleich der Bank (zwischengespeichert, wenige Varianten).</summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        static KurveFertig Holen(BetterSource source, SEB.ShotPlaybackContext context)
        {
            var preset = source.Preset;
            var basis = preset != null ? preset.SoundRolloff : null;
            if (basis == null || basis.length < 2) return null;
            if (!ReferenceEquals(basis, sebRef))
            {
                var keys = basis.keys;
                var p = new KurvenPunkt[keys.Length];
                for (int i = 0; i < keys.Length; i++) p[i] = new KurvenPunkt(keys[i].time, keys[i].value, keys[i].inTangent, keys[i].outTangent);
                sebPunkte = p; sebRef = basis; Kurven.Clear();
            }
            if (sebMax < 0f)
            {
                SEB.LiveWeaponAudioSettings.Get(null, EnvironmentType.Outdoor, false, out float md, out _);
                sebMax = md;
            }
            float b2 = 55f, b3 = 100f;
            bool fernDa = false;
            var bank = context.Bank;
            float[] bv = bank != null && bank.BlendOptions != null ? bank.BlendOptions.Values : null;
            if (bv != null && bv.Length >= 4)
            {
                b2 = bv[2]; b3 = bv[3];
                var env = bank.Environments;
                int ei = bank.HasEnvironment ? (int)context.Environment : 0;
                fernDa = env != null && ei < env.Length && env[ei] != null && env[ei].Length >= 3 && env[ei][2] != null && env[ei][2].Length > 0;
            }
            float fdb = Fernschuss.FernDb.Value, maxD = context.MaxDistance;
            string key = $"{maxD:F0}|{b2:F1}|{b3:F1}|{(fernDa ? fdb : 0f):F2}";
            if (Kurven.TryGetValue(key, out var fertig)) return fertig;
            var punkte = FernschussKern.Bauen(sebPunkte, sebMax, maxD, b2, b3, fdb, fernDa);
            if (punkte == null) { Kurven[key] = null; return null; }
            var kf = new Keyframe[punkte.Length];
            for (int i = 0; i < punkte.Length; i++) kf[i] = new Keyframe(punkte[i].T, punkte[i].V, punkte[i].In, punkte[i].Out);
            fertig = new KurveFertig { Kurve = new AnimationCurve(kf), Punkte = punkte };
            Kurven[key] = fertig;
            if (Plugin.Diagnose.Value)
                Plugin.Log.LogInfo($"Distance (SEB): gunshot curve built for max {maxD:F0} m, blend {b2:F0}/{b3:F0} m, far clip {(fernDa ? $"+{fdb:F1} dB" : "missing (no compensation)")}: " +
                    $"50 m {Db(punkte, 50f, maxD)}, 100 m {Db(punkte, 100f, maxD)}, 200 m {Db(punkte, 200f, maxD)}, 400 m {Db(punkte, 400f, maxD)}, 800 m {Db(punkte, 800f, maxD)} dB re 1 m " +
                    $"(SEB: 50 m {Db(sebPunkte, 50f, sebMax)}, 100 m {Db(sebPunkte, 100f, sebMax)}, 200 m {Db(sebPunkte, 200f, sebMax)}, 400 m {Db(sebPunkte, 400f, sebMax)}).");
            return fertig;
        }

        static string Db(KurvenPunkt[] p, float d, float maxD)
        {
            if (d >= maxD) return "silent";
            double g = FernschussKern.Auswerten(p, d / maxD), g1 = FernschussKern.Auswerten(p, 1.0 / maxD);
            return g <= 1e-9 || g1 <= 1e-9 ? "silent" : FernschussKern.DbAusGain(g / g1).ToString("F1", System.Globalization.CultureInfo.InvariantCulture);
        }

        internal static bool HatMarke(BetterSource q) => q != null && Marken.TryGetValue(q, out var m) && m.Kurve != null;

        internal static void Zuruecksetzen(BetterSource q)
        {
            if (!Marken.TryGetValue(q, out var m)) return;
            m.Kurve = null;
            var basis = q.Preset != null ? q.Preset.SoundRolloff : null;
            if (basis == null) return;
            if (q.source1 != null) q.source1.SetCustomCurve(AudioSourceCurveType.CustomRolloff, basis);
            if (q is SuperSource s && s.source2 != null) s.source2.SetCustomCurve(AudioSourceCurveType.CustomRolloff, basis);
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        internal static bool IstSebKurve(string gruppe, AnimationCurve c) =>
            SEB.LiveGroupPresetTable.Groups.TryGetValue(gruppe, out var e) && ReferenceEquals(e.Rolloff, c);
    }
}
