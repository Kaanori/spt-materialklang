using System;
using System.Globalization;
using Audio.AmbientSubsystem;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine.Audio;

namespace Materialklang
{
    // ---------------------------------------------------------------------------------------------
    //  15. Ambient emitters / Indoor room tone volume (%) (0.8.5): Grundrauschen in Gebaeuden leiser/lauter.
    //
    //  Beleg (EFT 4.1.6 Assembly-CSharp + SEB 1.0.3 live_mastermixer.bundle, UnityPy):
    //   - Abgespielt wird der Raumton von Audio.AmbientSubsystem.RoomToneController (in AmbientAudioSystem.Init angelegt,
    //     folgt MyPlayerRoomChangedEvent) -> SpatialAudioRoom.PlayRoomToneSound -> RoomAmbientSoundPlayer.Play:
    //     BetterAudio.GetSource(Nonspatial), SetMixerGroup(BetterAudio.AmbientInMixer), 2D, Loop,
    //     Play(RoomAmbientData.RoomTone, volume = RoomAmbientData.RoomToneVolume), FadeIn. Nur EINE Quelle zur Zeit (Raum des Spielers).
    //   - AmbientInMixer = FindMixerGroup("AmbientIn") (BetterAudio.method_4, nach SEBs Mixer-Tausch in CreateSoundManagers)
    //     -> SEB-Gruppe Master/InGame/World/Main/Ambient/AmbientIn - dort laeuft auch die uebrige Innen-Ambience (Innen-Regen u. a.).
    //   - Der SEB-Mixer hat zwar die Untergruppe AmbientIn/AmbientInRoomTone mit exponiertem Parameter "RoomToneVolume"
    //     (Gruppe 40, Volume-Index 80, alle Snapshots +4 dB), aber nichts wird dorthin geroutet: SPTs MasterMixer hat keine
    //     solche Gruppe (resources.assets), SEB leitet nur per gleichem Gruppennamen um, und weder Spiel noch SEB setzen den Parameter.
    //  Umsetzung: Postfix auf RoomAmbientSoundPlayer.Play setzt die Lautstaerke der Quelle (PreOcclusionVolume = BaseVolume x
    //  RoomToneVolume x Faktor, wie SimpleSource.Play rechnet); der Fader des Spiels arbeitet darauf weiter. Landet der Raumton
    //  einmal wirklich in AmbientInRoomTone (z. B. SEB-Update), wird stattdessen der Gruppenpegel per SetFloat gestellt.
    //  Ueber 100 % geht nur bis AudioSource.volume 1 (Unity klemmt).
    // ---------------------------------------------------------------------------------------------
    internal static class Raumton
    {
        internal static ConfigEntry<float> Prozent;

        static RoomAmbientSoundPlayer aktuell;
        static int raeume, mitTon;
        static bool gestoert, ersterGemeldet;
        static AudioMixer basisMixer;
        static float basisDb = RaumtonKern.SnapshotDb;
        static bool mixerGestellt;

        static float Faktor => Plugin.Aktiv != null && !Plugin.Aktiv.Value ? 1f : RaumtonKern.Faktor(Prozent.Value);
        static float ProzentWirksam => Plugin.Aktiv != null && !Plugin.Aktiv.Value ? 100f : Prozent.Value;

        internal static void Binden(ConfigFile c)
        {
            Prozent = c.Bind(Umgebung.Sek, "Indoor room tone volume (%)", 100f,
                new ConfigDescription("Volume of the constant background hum inside buildings (the game's room tone of the room you are in). " +
                    "100 = as in the game, 50 = half as loud (-6 dB), 0 = off. Outdoor ambience, rain, wind and SEB's ambient volume stay unchanged. " +
                    "Works without Physics Mode. Above 100 % it can only rise until the sound reaches full volume. Applies live.",
                    new AcceptableValueRange<float>(RaumtonKern.MinProzent, RaumtonKern.MaxProzent)));
            EventHandler neu = (_, __) => Neu();
            Prozent.SettingChanged += neu;
            if (Plugin.Aktiv != null) Plugin.Aktiv.SettingChanged += neu;
        }

        internal static void Anwenden(Harmony h)
        {
            try
            {
                var ctor = AccessTools.Constructor(typeof(RoomAmbientSoundPlayer), new[] { typeof(RoomAmbientData) });
                var play = AccessTools.DeclaredMethod(typeof(RoomAmbientSoundPlayer), "Play");
                var init = AccessTools.DeclaredMethod(typeof(AmbientAudioSystem), "Init");
                if (ctor == null || play == null || init == null)
                {
                    Plugin.Log.LogWarning("Room tone: targets not found (game update?) -> 'Indoor room tone volume' off.");
                    return;
                }
                h.Patch(ctor, postfix: new HarmonyMethod(typeof(Raumton), nameof(NachNeu)));
                h.Patch(play, postfix: new HarmonyMethod(typeof(Raumton), nameof(NachPlay)));
                h.Patch(init, postfix: new HarmonyMethod(typeof(Raumton), nameof(NachInit)));
            }
            catch (Exception e) { Statistik.Fehler(e); Plugin.Log.LogError($"Room tone: applying failed: {e.Message}"); }
        }

        // ---- Haken ----
        static void NachNeu(RoomAmbientData ambientData)
        {
            raeume++;
            if (ambientData != null && ambientData.RoomTone != null) mitTon++;
        }

        // Raidstart: AmbientAudioSystem.Init (legt den RoomToneController an, der sofort den ersten Raumton startet)
        static void NachInit()
        {
            if (gestoert) return;
            try
            {
                aktuell = null; ersterGemeldet = false;
                var inv = CultureInfo.InvariantCulture;
                string gruppe = "-"; bool param = false;
                if (MonoBehaviourSingleton<BetterAudio>.Instantiated)
                {
                    var ba = MonoBehaviourSingleton<BetterAudio>.Instance;
                    if (ba.AmbientInMixer != null) gruppe = ba.AmbientInMixer.name;
                    if (ba.Master != null) param = ba.Master.GetFloat(RaumtonKern.Parameter, out _);
                }
                bool mixer = RaumtonKern.MixerWeg(gruppe, param);
                Plugin.Log.LogInfo("Room tone: " + mitTon + " of " + raeume + " audio rooms have an indoor room tone (one plays at a time); way: " +
                    (mixer ? "mixer parameter " + RaumtonKern.Parameter + " (group " + gruppe + ")"
                           : "source volume in RoomAmbientSoundPlayer.Play (mixer group '" + gruppe + "', shared with all indoor ambience; " +
                             RaumtonKern.Gruppe + "/" + RaumtonKern.Parameter + (param ? " exists but gets no sound" : " not in this mixer") + ")") +
                    "; level " + ProzentWirksam.ToString("0", inv) + " % (" + RaumtonKern.Db(ProzentWirksam).ToString("+0.0;-0.0;0.0", inv) + " dB).");
                raeume = 0; mitTon = 0;
            }
            catch (Exception ex) { Fehler(ex); }
        }

        static void NachPlay(RoomAmbientSoundPlayer __instance)
        {
            if (gestoert || __instance._source == null) return;
            aktuell = __instance;
            try { Stellen(__instance, !ersterGemeldet); ersterGemeldet = true; }
            catch (Exception ex) { Fehler(ex); }
        }

        static void Neu()
        {
            var p = aktuell;
            if (gestoert || p == null || p._source == null) return;
            try { Stellen(p, false); } catch (Exception ex) { Fehler(ex); }
        }

        static void Stellen(RoomAmbientSoundPlayer p, bool melden)
        {
            var s = p._source;
            var src = s.source1;
            var gruppe = src != null ? src.outputAudioMixerGroup : null;
            var mixer = gruppe != null ? gruppe.audioMixer : null;
            bool mixerWeg = mixer != null && RaumtonKern.MixerWeg(gruppe.name, mixer.GetFloat(RaumtonKern.Parameter, out _));
            float spiel = RaumtonKern.Soll(s.BaseVolume, p._ambientData.RoomToneVolume, 1f);
            float f = Faktor;
            if (mixerWeg)
            {
                if (basisMixer != mixer)
                {
                    basisMixer = mixer; mixerGestellt = false;
                    basisDb = mixer.GetFloat(RaumtonKern.Parameter, out float v) ? v : RaumtonKern.SnapshotDb;
                }
                if (Math.Abs(f - 1f) < 1e-4f) { if (mixerGestellt) mixer.ClearFloat(RaumtonKern.Parameter); mixerGestellt = false; }
                else { mixer.SetFloat(RaumtonKern.Parameter, Math.Max(-80f, basisDb + RaumtonKern.Db(ProzentWirksam))); mixerGestellt = true; }
                s.PreOcclusionVolume = spiel;
            }
            else s.PreOcclusionVolume = RaumtonKern.Soll(s.BaseVolume, p._ambientData.RoomToneVolume, f);
            s.UpdateSourceVolume(1f);   // der Fade des Spiels (FadeFactor) laeuft auf dem neuen Wert weiter
            if (melden)
            {
                var inv = CultureInfo.InvariantCulture;
                float neu = mixerWeg ? spiel : RaumtonKern.Wirklich(s.PreOcclusionVolume);
                Plugin.Log.LogInfo("Room tone playing: clip " + (p._ambientData.RoomTone != null ? p._ambientData.RoomTone.name : "-") +
                    ", group " + (gruppe != null ? gruppe.name : "-") + ", volume " + spiel.ToString("F3", inv) + " -> " + neu.ToString("F3", inv) +
                    (mixerWeg ? " (mixer " + RaumtonKern.Parameter + " " + (basisDb + RaumtonKern.Db(ProzentWirksam)).ToString("F1", inv) + " dB)" : "") +
                    (!mixerWeg && s.PreOcclusionVolume > 1f ? " (capped at full volume)" : "") + ".");
            }
        }

        static void Fehler(Exception ex)
        {
            Statistik.Fehler(ex);
            if (ex is MissingFieldException || ex is MissingMethodException || ex is TypeLoadException)
            {
                gestoert = true;
                Plugin.Log.LogError($"Room tone: {ex.GetType().Name}: {ex.Message} - game changed? 'Indoor room tone volume' off for this session.");
            }
        }
    }
}
