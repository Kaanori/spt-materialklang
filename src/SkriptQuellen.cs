using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using Audio.Vehicles.BTR;
using BepInEx.Configuration;
using CommonAssets.Scripts.ArtilleryShelling.Client.Audio;
using Comfort.Common;
using EFT;
using EFT.Airdrop;
using EFT.Interactive;
using EFT.MovingPlatforms;
using HarmonyLib;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Materialklang
{
    // ---------------------------------------------------------------------------------------------
    //  18. Scripted sources (0.8.5): Quellen, die erst per Code/Prefab/Skript entstehen, nach derselben Regel wie Abschnitt 17
    //  (Spielkurve bis r0, danach 1/r, weicher Auslauf, Reichweite = alt x Faktor, gedeckelt je Art; SkriptQuellenKern).
    //
    //  Beleg (EFT 4.1.6, Assembly-CSharp per ilspycmd; Inventur klangquellen-inventur-skript.md 09.11.2026):
    //   - Lok (EFT.MovingPlatforms.Locomotive, Reserve/Lighthouse): Szenen-AudioSources Sources[] + HummingSource, keine Okklusion.
    //     FadeIn(SoundEvent) setzt bei JEDEM Ereignis freeSource.maxDistance = sound.Rolloff (Standard 300) und spielt Einmal-Ereignisse
    //     ohne Einblendung ueber BetterAudio.PlayAtPoint(Environment, sound.Rolloff). Init setzt HummingSource.maxDistance =
    //     _hummingSourceRolloff (Prefab 50). Weg hier: FadeIn-Ergebnis (Coroutine) wird umhuellt - der erste Schritt laeuft wie im Spiel,
    //     danach bekommt genau die gewaehlte Quelle Reichweite + Kurve aus dem unveraenderten sound.Rolloff; der PlayAtPoint-Fall laeuft
    //     im Kontext "Lok". SoundEvent.Rolloff/_hummingSourceRolloff werden NICHT beschrieben (geteilte Prefab-Daten).
    //   - Airdrop: ClientAirPlane.PlaySound (je Bild!) setzt nur beim Start SetRolloff(soundClip.Falloff) + Kurve EFTHardSettings.SoundRolloff,
    //     Lautstaerke = PlaneVolumeCurve(InverseLerp(Falloff, 300, d)) - bleibt wie im Spiel. Postfix: nur wenn die Quelle nicht schon den
    //     Sollwert hat (eine float-Abfrage je Bild). ClientAirDrop.PlaySound(TaggedClip) fuer Landung (Falloff = LandingSoundBank.Rolloff,
    //     50 m), Fallschirm-Quietschen und Kistenfackel (Falloff im Odin-Prefab, nicht gelesen -> Klang-/BepInEx-Log). Mit Okklusion
    //     (ProcessSourceOcclusion), das Flugzeug ohne. TaggedClip/SoundBank bleiben unveraendert.
    //   - BTR: Turm VehicleRotationSoundPlayer.SetupSource (Environment, _maxDistance 60), Lufthorn BtrSoundController._airHornSource
    //     (Pool-Quelle ohne eigenes SetRolloff -> Reichweite zufaellig vom Vorbenutzer; hier Gruppen-Standard DefaultMaxDistance -> 500 m).
    //     Motor: Abschnitt 17 (Klasse vehicle, eigene Grenze 600 m). Keine Okklusion.
    //   - Leuchtpatrone (FlarePatronSound.InitSource, Environment, _rolloff 25, ContinuousPropagated-Okklusion).
    //     Ausgangsfackeln (Flare_Audio unter MultiFlareLightHelper, rohe Szenenquelle): Abschnitt 17 mit Grenze von hier.
    //   - Moerser-Pfeifen (ArtilleryShellingSoundControllerClient.PlayWhistleSound: eigene Kurve whistleRolloffCurve, 115 m, keine Okklusion).
    //   - Tueren/Tore/Schalter: alle 12 Aufrufer der Gruppe InteractiveObjects gehen durch BetterAudio.PlayAtPoint(..., int rolloff, ...)
    //     (Door 35/35/50, Treffer 40, Aufbruch 60, DoorHandle 14, AutoDoorSound/HandlerPlaySound 15, BufferGates 50). Die steile
    //     Gruppenkurve (SEB: 1 -> 0,54 bei 5 %, dann fast linear auf 0) ist der Engpass. Das Gruppen-Preset selbst anzufassen waere
    //     unsauber: es gilt fuer alle Quellen der Gruppe, Pool-Quellen kopieren die Kurve beim Anlegen, SEB setzt sie beim Laden. Deshalb
    //     bekommt jede abspielende Quelle ihre eigene Kurve (aus der Preset-Kurve, Cache je alt/neu) und beim Release (Prefix) wieder den
    //     Stand des Spiels. Schritte/andere Gruppen bleiben unberuehrt. Okklusion der Tueren (ProcessInteractiveObjectOcclusion) bleibt.
    //     Ausgangs-Tor (ExfiltrationDoor, Environment, Rolloff 50, OHNE Okklusion): Kontext waehrend OnStatusChangedHandler.
    //   - SoundBank.Rolloff: keine Bank wird beschrieben (Landung liest die Bank-Reichweite bei jedem Abspielen, die Reichweite wird auf
    //     die Quelle gesetzt). Explosions-/Waffen-Banks bleiben bei SEB.
    //  Kosten: nur bestehende Pfade (Abspielen, Flugzeug-PlaySound je Bild = eine float-Abfrage), Kurven gecacht.
    // ---------------------------------------------------------------------------------------------
    internal static class SkriptQuellen
    {
        internal const string Sek = "18. Scripted sources";
        internal static ConfigEntry<bool> Aktiv, Zuege, Abwurf, Btr, Fackeln, Moerser, Tueren;
        internal static ConfigEntry<float> TuerFaktor;
        static readonly ConfigEntry<float>[] grenzen = new ConfigEntry<float>[SkriptQuellenKern.Vorgaben.Length];
        static int stand;                       // steigt bei jeder Einstellungsaenderung
        static readonly bool[] gestoert = new bool[SkriptQuellenKern.Vorgaben.Length];

        internal static bool Wirksam(SkriptArt a)
        {
            if (Aktiv == null || !Aktiv.Value || !Physik.An || gestoert[(int)a]) return false;
            var s = Schalter(a);
            return s != null && s.Value;
        }

        static ConfigEntry<bool> Schalter(SkriptArt a)
        {
            switch (a)
            {
                case SkriptArt.LokFahrt: case SkriptArt.LokLeerlauf: return Zuege;
                case SkriptArt.Flugzeug: case SkriptArt.Landung: case SkriptArt.Abwurf: return Abwurf;
                case SkriptArt.BtrTurm: case SkriptArt.BtrHorn: return Btr;
                case SkriptArt.Ausgangsfackel: case SkriptArt.Leuchtpatrone: return Fackeln;
                case SkriptArt.Pfeifen: return Moerser;
                default: return Tueren;
            }
        }

        internal static double Grenze(SkriptArt a)
        {
            var e = grenzen[a == SkriptArt.Ausgangstor ? (int)SkriptArt.Tuer : (int)a];
            return e != null ? e.Value : SkriptQuellenKern.Von(a).Grenze;
        }

        static double Faktor(SkriptArt a)
            => (a == SkriptArt.Tuer || a == SkriptArt.Ausgangstor) && TuerFaktor != null ? TuerFaktor.Value : SkriptQuellenKern.Von(a).Faktor;

        static double Soll(SkriptArt a, double alt) => SkriptQuellenKern.Reichweite(a, alt, Grenze(a), Faktor(a));

        static ConfigEntry<float> GrenzeBinden(ConfigFile c, SkriptArt a, string key, string text)
        {
            var v = SkriptQuellenKern.Von(a);
            var e = c.Bind(Sek, key, (float)v.Grenze, new ConfigDescription(text, new AcceptableValueRange<float>((float)v.GrenzeMin, (float)v.GrenzeMax)));
            grenzen[(int)a] = e;
            return e;
        }

        internal static void Binden(ConfigFile c)
        {
            const string ohne = " No occlusion in the game; '17. All fixed sources / Wall damping for non-occluded sources' muffles it through walls.";
            const string mit = " Occlusion as in the game (walls, doors, rooms of True Acoustics apply).";
            Aktiv = c.Bind(Sek, "Enabled", true,
                "Sounds the game creates from scripts (trains, airdrop plane and crate, BTR turret and horn, flares, mortar whistle, doors and gates) carry " +
                "further by the rule of section 17: exactly the game's curve near the source (as loud as before), beyond it -6 dB per doubling of distance " +
                "instead of a hard end, range = game range x factor, capped per kind. Shared game data (clips, sound banks, prefab fields) is never changed - " +
                "only the playing source. Works only while Physics Mode is on. Applies live (one-shot sounds from the next time they play).");
            Zuege = c.Bind(Sek, "Trains: extend range", true,
                "Trains on Reserve/Lighthouse: horn, departure and driving (game 300 m) and the idling engine (game 50 m)." + ohne);
            GrenzeBinden(c, SkriptArt.LokFahrt, "Trains: horn/travel max. range (m)",
                "Horn/driving: game range x 5, at most this (a train horn is 96-110 dB(A) at 30 m and heard for kilometres).");
            GrenzeBinden(c, SkriptArt.LokLeerlauf, "Trains: idle max. range (m)",
                "Idling diesel: game range x 10, at most this (70 dB(A) at 30 m).");
            Abwurf = c.Bind(Sek, "Airdrop: extend range", true,
                "Airdrop plane (range from its prefab, written to the sound log and BepInEx log when it flies; volume curve of the game unchanged; no game occlusion - wall damping of section 17), " +
                "crate landing (game 50 m) and the crate's parachute/flare sounds (with occlusion as in the game).");
            GrenzeBinden(c, SkriptArt.Flugzeug, "Airdrop: plane max. range (m)", "Plane: game range x 20, at most this (an overflying transport plane is 85-100 dB(A) on the ground).");
            GrenzeBinden(c, SkriptArt.Landung, "Airdrop: landing max. range (m)", "Crate landing: game range x 4, at most this.");
            GrenzeBinden(c, SkriptArt.Abwurf, "Airdrop: parachute/flare max. range (m)", "Parachute squeak and crate flare hiss: game range x 4, at most this.");
            Btr = c.Bind(Sek, "BTR: turret and air horn", true,
                "BTR turret rotation (game 60 m) and the air horn before departure (the game gives it no range of its own - a pooled source keeps whatever " +
                "range its last user set; here: the group default, 1/r up to the range below). The BTR engine is in section 17 (Vehicles)." + ohne);
            GrenzeBinden(c, SkriptArt.BtrTurm, "BTR: turret max. range (m)", "Turret motor: game range x 2.5, at most this.");
            GrenzeBinden(c, SkriptArt.BtrHorn, "BTR: air horn range (m)", "Air horn range (a truck air horn is 105-118 dB(A) at 2 m).");
            Fackeln = c.Bind(Sek, "Flares: extend range", true,
                "Signal flares burning at extracts (scene sources, game 10 m linear, volume 0.18 unchanged; no game occlusion - wall damping of section 17) and the burning sound of fired flare " +
                "rounds (game 25 m; occlusion as in the game).");
            GrenzeBinden(c, SkriptArt.Ausgangsfackel, "Flares: exit flare max. range (m)", "Extract flares: game range x 5, at most this. Sound log line '[Fixed] exit flare ...'.");
            GrenzeBinden(c, SkriptArt.Leuchtpatrone, "Flares: flare round max. range (m)", "Flare round: game range x 4, at most this.");
            Moerser = c.Bind(Sek, "Mortar whistle: extend range", true,
                "Whistle of incoming mortar shells (game 115 m with its own curve; volume unchanged)." + ohne);
            GrenzeBinden(c, SkriptArt.Pfeifen, "Mortar whistle: max. range (m)", "Whistle: game range x 4, at most this.");
            Tueren = c.Bind(Sek, "Doors: extend range", true,
                "Doors, gates, handles and switches (group InteractiveObjects: handles 14-15 m, doors 35/50 m, breach 60 m, gates 50 m) and the extract " +
                "gates (ExfiltrationDoor 50 m, no occlusion in the game - wall damping of section 17). The game's steep group curve is replaced per playing source only - footsteps and " +
                "other groups stay unchanged." + mit);
            TuerFaktor = c.Bind(Sek, "Doors: range factor", (float)SkriptQuellenKern.TuerFaktor,
                new ConfigDescription("New range = game range x this (handle 15 -> 30 m, door 35 -> 70 m, slam/gate 50 -> 100 m).", new AcceptableValueRange<float>(1f, 4f)));
            GrenzeBinden(c, SkriptArt.Tuer, "Doors: max. range (m)", "Largest new range of doors, gates, switches and extract gates.");

            EventHandler neu = (_, __) => Neu();
            Aktiv.SettingChanged += neu; Zuege.SettingChanged += neu; Abwurf.SettingChanged += neu; Btr.SettingChanged += neu;
            Moerser.SettingChanged += neu; Tueren.SettingChanged += neu; TuerFaktor.SettingChanged += neu;
            foreach (var g in grenzen) if (g != null) g.SettingChanged += neu;
            EventHandler fackel = (_, __) => { Neu(); try { FesteQuellen.AllesNeu(); } catch (Exception ex) { Statistik.Fehler(ex); } };
            Fackeln.SettingChanged += fackel; grenzen[(int)SkriptArt.Ausgangsfackel].SettingChanged += fackel;
            Aktiv.SettingChanged += (_, __) => { try { FesteQuellen.AllesNeu(); } catch (Exception ex) { Statistik.Fehler(ex); } };
            if (Physik.Aktiv != null) Physik.Aktiv.SettingChanged += neu;
            if (Plugin.Aktiv != null) Plugin.Aktiv.SettingChanged += neu;
        }

        // =========================================================================================
        //  Anwenden
        // =========================================================================================
        internal static void Anwenden(Harmony h)
        {
            var ok = new List<string>(12);
            void P(Type t, string name, string pre, string post, string fin = null, Func<MethodInfo, bool> wahl = null, params SkriptArt[] arten)
            {
                try
                {
                    MethodInfo m = null;
                    if (t != null)
                        foreach (var x in AccessTools.GetDeclaredMethods(t))
                            if (x.Name == name && (wahl == null || wahl(x))) { m = x; break; }
                    if (m == null)
                    {
                        foreach (var a in arten) gestoert[(int)a] = true;
                        Plugin.Log.LogWarning($"Scripted sources: {(t != null ? t.Name : "?")}.{name} not found (game update?) -> {string.Join(", ", Array.ConvertAll(arten, SkriptQuellenKern.Name))} unchanged.");
                        return;
                    }
                    h.Patch(m, prefix: pre != null ? new HarmonyMethod(typeof(SkriptQuellen), pre) : null,
                               postfix: post != null ? new HarmonyMethod(typeof(SkriptQuellen), post) : null,
                               finalizer: fin != null ? new HarmonyMethod(typeof(SkriptQuellen), fin) : null);
                    ok.Add(t.Name + "." + name);
                }
                catch (Exception e)
                {
                    foreach (var a in arten) gestoert[(int)a] = true;
                    Statistik.Fehler(e);
                    Plugin.Log.LogError($"Scripted sources: patching {name} failed: {e.Message}");
                }
            }
            P(typeof(BetterSource), "Release", nameof(VorFreigabe), null, null, m => m.GetParameters().Length == 0);
            P(typeof(BetterAudio), "PlayAtPoint", nameof(VorPunkt), nameof(NachPunkt), null, m => IstPunkt(m, true), SkriptArt.Tuer, SkriptArt.Ausgangstor);
            P(typeof(BetterAudio), "PlayAtPoint", nameof(VorPunkt), nameof(NachPunkt), null, m => IstPunkt(m, false), SkriptArt.Tuer);
            P(typeof(ExfiltrationDoor), "OnStatusChangedHandler", nameof(VorTor), null, nameof(EndeKontext), m => m.GetParameters().Length == 3, SkriptArt.Ausgangstor);
            P(typeof(Locomotive), "Init", null, nameof(NachLokInit), null, null, SkriptArt.LokFahrt, SkriptArt.LokLeerlauf);
            P(typeof(Locomotive), "FadeIn", null, nameof(NachLokEreignis), null, null, SkriptArt.LokFahrt);
            P(typeof(ClientAirPlane), "PlaySound", null, nameof(NachFlugzeug), null, null, SkriptArt.Flugzeug);
            P(typeof(ClientAirDrop), "PlaySound", nameof(VorAbwurf), nameof(NachAbwurf), null, null, SkriptArt.Landung, SkriptArt.Abwurf);
            P(typeof(VehicleRotationSoundPlayer), "SetupSource", null, nameof(NachTurm), null, null, SkriptArt.BtrTurm);
            P(typeof(BtrSoundController), "OnReadyToDeparture", nameof(VorHorn), null, null, null, SkriptArt.BtrHorn);
            P(typeof(FlarePatronSound), "InitSource", null, nameof(NachPatrone), null, null, SkriptArt.Leuchtpatrone);
            P(typeof(ArtilleryShellingSoundControllerClient), "PlayWhistleSound", null, nameof(NachPfeifen), null, null, SkriptArt.Pfeifen);
            if (!ok.Contains("BetterSource.Release"))
                for (int i = 0; i < gestoert.Length; i++) if (i != (int)SkriptArt.LokFahrt && i != (int)SkriptArt.LokLeerlauf && i != (int)SkriptArt.Ausgangsfackel) gestoert[i] = true;
            SceneManager.activeSceneChanged += (_, __) => SzeneGewechselt();
            Plugin.Log.LogInfo($"Scripted sources: ready - patched {string.Join(", ", ok)}. Section 18 is {(Aktiv.Value ? "on" : "off")}" +
                               $"{(Physik.An ? "" : " (needs Physics Mode)")}.");
        }

        /// <summary>BetterAudio.PlayAtPoint mit AudioClip und int rolloff: mitAbstand = Ueberladung mit float distance (Lok, Ausgangs-Tor).</summary>
        static bool IstPunkt(MethodInfo m, bool mitAbstand)
        {
            var p = m.GetParameters();
            if (p.Length < 5 || p[1].ParameterType != typeof(AudioClip)) return false;
            bool abstand = p[2].ParameterType == typeof(float);
            int g = abstand ? 3 : 2;
            return abstand == mitAbstand && p[g].ParameterType == typeof(BetterAudio.AudioSourceGroupType) && p[g].Name == "sourceGroup"
                   && p[g + 1].ParameterType == typeof(int) && p[g + 1].Name == "rolloff";
        }

        // =========================================================================================
        //  Gemeinsam: Kurven (Cache), Marken (Release stellt den Spielstand her), laufende Quellen, Log
        // =========================================================================================
        internal sealed class Fertig { public AnimationCurve Kurve; public Func<double, double> AltF; public float R0, Alt, Neu; }
        static readonly ConditionalWeakTable<AnimationCurve, Dictionary<long, Fertig>> kurven = new ConditionalWeakTable<AnimationCurve, Dictionary<long, Fertig>>();

        static Fertig Holen(AnimationCurve basis, double alt, double neu)
        {
            if (basis == null || basis.length < 2 || !(alt > 0) || !(neu > 0)) return null;
            var d = kurven.GetValue(basis, _ => new Dictionary<long, Fertig>());
            long k = SkriptQuellenKern.Schluessel(alt, neu);
            if (d.TryGetValue(k, out var f)) return f;
            f = Bauen(FesteQuellen.AltFunktion(AudioRolloffMode.Custom, basis, 0f, (float)alt), alt, neu);
            d[k] = f;
            return f;
        }

        static Fertig Bauen(Func<double, double> altF, double alt, double neu)
        {
            if (!(altF(0.0) > 0)) return null;
            var kk = UmgebungKern.Bauen(altF, alt, neu, 0.0, out double r0);
            var keys = new Keyframe[kk.Length];
            for (int i = 0; i < kk.Length; i++) keys[i] = new Keyframe(kk[i].T, kk[i].V, kk[i].In, kk[i].Out);
            return new Fertig { Kurve = new AnimationCurve(keys), AltF = altF, R0 = (float)r0, Alt = (float)alt, Neu = (float)neu };
        }

        sealed class Marke { public bool An; public AnimationCurve AltKurve; public float Alt; public int Stand = -1; public float Soll; }
        static readonly ConditionalWeakTable<BetterSource, Marke> marken = new ConditionalWeakTable<BetterSource, Marke>();

        static void KurveSetzen(BetterSource q, AnimationCurve k)
        {
            if (k == null) return;
            if (q.source1 != null) { q.source1.rolloffMode = AudioRolloffMode.Custom; q.source1.SetCustomCurve(AudioSourceCurveType.CustomRolloff, k); }
            if (q is SuperSource ss && ss.source2 != null) { ss.source2.rolloffMode = AudioRolloffMode.Custom; ss.source2.SetCustomCurve(AudioSourceCurveType.CustomRolloff, k); }
        }

        /// <summary>0.8.5 Wanddaempfung: Collider, die zur Quelle selbst gehoeren (Lok im Kontext, Ausgangs-Tor, BTR), zaehlen nicht als Wand.</summary>
        static Transform kontextEigen;
        static Transform EigenWurzel(SkriptArt art, BetterSource q)
        {
            if (kontextEigen != null && (art == SkriptArt.LokFahrt || art == SkriptArt.Ausgangstor)) return kontextEigen;
            if (art == SkriptArt.BtrTurm || art == SkriptArt.BtrHorn)
            {
                var b = q.GetComponentInParent<BtrSoundController>();
                return b != null ? b.transform.root : null;
            }
            return null;
        }

        static void Zurueck(BetterSource q, Marke m)
        {
            m.An = false;
            KurveSetzen(q, m.AltKurve ?? (q.Preset != null ? q.Preset.SoundRolloff : null));
            q.SetRolloff(m.Alt);
        }

        // ---- BetterSource.Release (Prefix): Stand des Spiels zurueck, bevor die Quelle in den Pool geht ----
        static void VorFreigabe(BetterSource __instance)
        {
            try
            {
                if (__instance == null) return;
                WandDaempfung.Abmelden(__instance.source1);   // 0.8.5: Filter weg, bevor die Quelle in den Pool geht
                if (!marken.TryGetValue(__instance, out var m)) return;
                if (m.An) Zurueck(__instance, m);
                m.Stand = -1;
                laufend.Remove(__instance.GetInstanceID());
            }
            catch (Exception ex) { Statistik.Fehler(ex); }
        }

        /// <summary>Kern fuer BetterSources: Reichweite aus dem unveraenderten Spielwert alt, Kurve aus altKurve (Spielkurve dieser Quelle).
        /// Setzt oder nimmt zurueck, zaehlt, schreibt Log. laufend = fortlaufende Quelle (Zeilen bei Bewegung, live nachgestellt).</summary>
        static Fertig Stellen(BetterSource q, SkriptArt art, float alt, AnimationCurve altKurve, string name, bool lauf, Action neustellen, string info = null, float sollAlt = -1f)
        {
            if (q == null) return null;
            double neu = Soll(art, alt);
            Fertig f = null;
            bool an = Wirksam(art) && SkriptQuellenKern.Lohnt(art, alt, neu);
            if (an) { f = Holen(altKurve, alt, neu); an = f != null; }
            marken.TryGetValue(q, out var m);
            if (an)
            {
                if (m == null) m = marken.GetValue(q, _ => new Marke());
                m.An = true; m.AltKurve = altKurve; m.Alt = sollAlt >= 0 ? sollAlt : alt;
                q.SetRolloff(f.Neu);
                KurveSetzen(q, f.Kurve);
            }
            else if (m != null && m.An) Zurueck(q, m);
            Zaehlen(art, alt, an ? neu : alt, an);
            // 0.8.5: Arten ohne Okklusion im Spiel -> eigene Wanddaempfung (in der Luft: nur das Stueck vor dem Ohr; Einmal-Klaenge sofort)
            if (!SkriptQuellenKern.Von(art).Okklusion)
                WandDaempfung.Melden(q.source1, an, "scripted", SkriptQuellenKern.Name(art) + " " + name,
                                     himmel: art == SkriptArt.Flugzeug || art == SkriptArt.Pfeifen, sofort: !lauf, eigen: an ? EigenWurzel(art, q) : null);
            if (lauf)
            {
                int id = q.GetInstanceID();
                if (!laufend.TryGetValue(id, out var l)) laufend[id] = l = new Lauf();
                l.B = q; l.A = null; l.Art = art; l.Name = name; l.Info = info; l.Alt = alt; l.F = an ? f : null; l.Neustellen = neustellen;
                l.LetztD = -1;
            }
            else if (KlangLog.An) Einmal(art, name, q.source1, q.Position, alt, an ? f : null, q.source1 != null ? q.source1.volume : 1f, info);
            return an ? f : null;
        }

        // ---- laufende Quellen: [Scripted]-Zeilen + Live-Nachstellen ----
        sealed class Lauf
        {
            public BetterSource B; public AudioSource A;
            public SkriptArt Art; public string Name, Info; public float Alt; public Fertig F;
            public Action Neustellen;
            public double LetztD = -1; public float LetztT;
        }
        static readonly Dictionary<int, Lauf> laufend = new Dictionary<int, Lauf>(32);
        static readonly Dictionary<string, float> einmalZeit = new Dictionary<string, float>(64);
        static float tickNaechste;

        static void Neu()
        {
            stand++;
            var l = new List<Lauf>(laufend.Values);
            foreach (var e in l)
            {
                if (e.Neustellen == null) continue;
                if ((e.B == null && e.A == null) || (e.B != null && e.B.Equals(null)) || (e.A != null && e.A.Equals(null))) continue;
                try { e.Neustellen(); } catch (Exception ex) { Statistik.Fehler(ex); }
            }
            foreach (var q in new List<LokQ>(lokQ.Values)) { try { LokStellen(q); } catch (Exception ex) { Statistik.Fehler(ex); } }
        }

        internal static void Tick()
        {
            float jetzt = Time.realtimeSinceStartup;
            try { ZusammenfassungTick(jetzt); } catch (Exception ex) { Statistik.Fehler(ex); }
            if (laufend.Count == 0 || !KlangLog.An || jetzt < tickNaechste) return;
            tickNaechste = jetzt + 0.5f;
            try
            {
                var h = FesteQuellen.Hoerer();
                if (h == null) return;
                List<int> weg = null;
                foreach (var kv in laufend)
                {
                    var e = kv.Value;
                    AudioSource s = e.B != null ? e.B.source1 : e.A;
                    if (s == null || (e.B == null && e.A == null) || (e.B != null && e.B.Equals(null))) { (weg ??= new List<int>()).Add(kv.Key); continue; }
                    if (!s.isActiveAndEnabled || !s.isPlaying) continue;
                    Vector3 pos = e.B != null ? e.B.Position : s.transform.position;
                    double d = (pos - h.Value).magnitude;
                    double reich = Math.Max(e.Alt, e.F != null ? e.F.Neu : 0f);
                    if (!SkriptQuellenKern.Zeile(d, e.LetztD, jetzt - e.LetztT, reich)) continue;
                    e.LetztD = d; e.LetztT = jetzt;
                    float vol = e.B != null ? e.B.BaseVolume : s.volume;
                    KlangLog.Text(Text(e.Art, e.Name, s, d, e.Alt, e.F, vol, e.Info));
                }
                if (weg != null) foreach (var k in weg) laufend.Remove(k);
            }
            catch (Exception ex) { Statistik.Fehler(ex); }
        }

        static void Einmal(SkriptArt art, string name, AudioSource s, Vector3 pos, float alt, Fertig f, float vol, string info)
        {
            float jetzt = Time.realtimeSinceStartup;
            string k = (int)art + "|" + name;
            if (!SkriptQuellenKern.EinmalZeile(jetzt, einmalZeit.TryGetValue(k, out float t) ? t : -1f)) return;
            einmalZeit[k] = jetzt;
            var h = FesteQuellen.Hoerer();
            double d = h != null ? (pos - h.Value).magnitude : -1;
            KlangLog.Text(Text(art, name, s, d, alt, f, vol, info));
        }

        /// <summary>[Scripted] Art 'Name' (clip, mixer) d | range alt -> neu (r0) | level game/new (curve) x vol | Okklusion [| info]</summary>
        static string Text(SkriptArt art, string name, AudioSource s, double d, float alt, Fertig f, float vol, string info)
        {
            var inv = CultureInfo.InvariantCulture;
            var sb = new StringBuilder(240);
            sb.Append("[Scripted] ").Append(SkriptQuellenKern.Name(art)).Append(" '").Append(name ?? "?").Append("' (clip ")
              .Append(s != null && s.clip != null ? s.clip.name : "-").Append(", mixer ")
              .Append(s != null && s.outputAudioMixerGroup != null ? s.outputAudioMixerGroup.name : "-").Append(')');
            sb.Append(" | d=").Append(d >= 0 ? d.ToString("F1", inv) : "?").Append(" m | range ").Append(alt.ToString("0.#", inv));
            if (f != null) sb.Append(" -> ").Append(f.Neu.ToString("0.#", inv)).Append(" m (r0 ").Append(f.R0.ToString("F1", inv)).Append(" m)");
            else sb.Append(" m unchanged").Append(Wirksam(art) ? "" : " (off)");
            if (d >= 0 && f != null)
            {
                double g = d >= f.Alt ? 0 : f.AltF(d / f.Alt);
                double n = d >= f.Neu ? 0 : f.Kurve.Evaluate((float)(d / f.Neu));
                sb.Append(" | level game ").Append(g <= 1e-6 ? "silent" : Db(g, inv) + " dB").Append(", new ").Append(n <= 1e-6 ? "silent" : Db(n, inv) + " dB");
            }
            sb.Append(" (curve) x vol ").Append(vol.ToString("0.###", inv));
            sb.Append(SkriptQuellenKern.Von(art).Okklusion ? " | occlusion as in the game" : FesteQuellen.WandText());
            if (!string.IsNullOrEmpty(info)) sb.Append(" | ").Append(info);
            return sb.ToString();
        }

        static string Db(double g, IFormatProvider inv) => FernschussKern.DbAusGain(g).ToString("F1", inv);

        // ---- Zusammenfassung ins BepInEx-Log (je Karte; neue Eintraege nach 3 s Ruhe, sonst hoechstens jede Minute) ----
        static readonly Dictionary<string, int> zaehler = new Dictionary<string, int>();
        static int zStand, zGemeldet;
        static bool zNeu;
        static float zRuhe, zLetzt = -1000f;
        static readonly HashSet<string> einmalInfo = new HashSet<string>();

        static void Zaehlen(SkriptArt art, double alt, double neu, bool an)
        {
            string k = SkriptQuellenKern.Eintrag(art, alt, neu, an);
            zaehler.TryGetValue(k, out int n);
            if (n == 0) zNeu = true;
            zaehler[k] = n + 1;
            zStand++; zRuhe = Time.realtimeSinceStartup + 3f;
        }

        static void ZusammenfassungTick(float jetzt)
        {
            if (zStand == zGemeldet || jetzt < zRuhe) return;
            if (!zNeu && jetzt - zLetzt < 60f) return;
            zGemeldet = zStand; zNeu = false; zLetzt = jetzt;
            Plugin.Log.LogInfo("Scripted sources [" + Karte() + "]: " + SkriptQuellenKern.Zusammenfassung(zaehler) + (Physik.An ? "" : " (Physics Mode off)"));
        }

        /// <summary>Prefab-Werte, die nur zur Laufzeit bekannt sind (Flugzeug, Kiste): einmal je Wert ins BepInEx-Log.</summary>
        static void Info(string text) { if (einmalInfo.Add(text)) Plugin.Log.LogInfo("Scripted sources: " + text); }

        static string Karte()
        {
            try { if (Singleton<GameWorld>.Instantiated && !string.IsNullOrEmpty(Singleton<GameWorld>.Instance.LocationId)) return Singleton<GameWorld>.Instance.LocationId; }
            catch { }
            return SceneManager.GetActiveScene().name;
        }

        static void SzeneGewechselt()
        {
            var weg = new List<int>();
            foreach (var kv in laufend) if ((kv.Value.B == null && kv.Value.A == null) || (kv.Value.B != null && kv.Value.B.Equals(null)) || (kv.Value.A != null && kv.Value.A.Equals(null))) weg.Add(kv.Key);
            foreach (var k in weg) laufend.Remove(k);
            weg.Clear();
            foreach (var kv in lokQ) if (kv.Value.S == null) weg.Add(kv.Key);
            foreach (var k in weg) lokQ.Remove(k);
            einmalZeit.Clear();
            if (zStand != zGemeldet) { zRuhe = 0; zNeu = true; try { ZusammenfassungTick(Time.realtimeSinceStartup); } catch { } }
            zaehler.Clear(); zStand = zGemeldet = 0; zNeu = false;
            flugQ = null;
        }

        static void Fehler(SkriptArt a, string teil, Exception ex)
        {
            Statistik.Fehler(ex);
            if (ex is MissingFieldException || ex is MissingMethodException || ex is TypeLoadException || ex is InvalidCastException)
            {
                gestoert[(int)a] = true;
                Plugin.Log.LogError($"Scripted sources: {teil}: {ex.GetType().Name}: {ex.Message} - {SkriptQuellenKern.Name(a)} off for this session.");
            }
        }

        // =========================================================================================
        //  BetterAudio.PlayAtPoint (int rolloff): Tueren/Tore/Schalter (Gruppe InteractiveObjects) + Kontext (Ausgangs-Tor, Lok)
        // =========================================================================================
        internal struct Plan { public bool An; public SkriptArt Art; public int Alt, Neu; }
        static SkriptArt? kontext;
        static string kontextName;

        static void VorPunkt(BetterAudio.AudioSourceGroupType sourceGroup, ref int rolloff, out Plan __state)
        {
            __state = default;
            SkriptArt art;
            if (kontext.HasValue && sourceGroup == BetterAudio.AudioSourceGroupType.Environment) art = kontext.Value;
            else if (sourceGroup == BetterAudio.AudioSourceGroupType.InteractiveObjects) art = SkriptArt.Tuer;
            else return;
            if (rolloff <= 0 || !Wirksam(art)) return;
            int neu = SkriptQuellenKern.Ganz(rolloff, Soll(art, rolloff));
            if (neu <= rolloff) return;
            __state = new Plan { An = true, Art = art, Alt = rolloff, Neu = neu };
            rolloff = neu;   // auch die Abstandspruefung des Spiels (distance > rolloff / IsInRange) folgt der neuen Reichweite
        }

        static void NachPunkt(BetterSource __result, Plan __state, AudioClip clip)
        {
            if (!__state.An) return;
            try
            {
                if (__result == null) return;
                var basis = __result.Preset != null ? __result.Preset.SoundRolloff : null;
                string name = __state.Art == SkriptArt.LokFahrt && kontextName != null ? kontextName : clip != null ? clip.name : "?";
                Stellen(__result, __state.Art, __state.Alt, basis, name, false, null);
                if (!marken.TryGetValue(__result, out var m) || !m.An) __result.SetRolloff(__state.Alt);   // keine Kurve -> wie im Spiel
            }
            catch (Exception ex) { Fehler(__state.Art, "PlayAtPoint", ex); }
        }

        // ---- Ausgangs-Tor: Kontext waehrend ExfiltrationDoor.OnStatusChangedHandler ----
        static SkriptArt? torVorher;
        static Transform torEigenVorher;
        static void VorTor(ExfiltrationDoor __instance)
        {
            torVorher = kontext; kontext = SkriptArt.Ausgangstor; kontextName = null;
            torEigenVorher = kontextEigen; kontextEigen = __instance != null ? __instance.transform : null;
        }
        static Exception EndeKontext(Exception __exception) { kontext = torVorher; torVorher = null; kontextEigen = torEigenVorher; torEigenVorher = null; return __exception; }

        // =========================================================================================
        //  Lok (Reserve, Lighthouse)
        // =========================================================================================
        sealed class LokQ
        {
            public AudioSource S; public Locomotive L;
            public AudioRolloffMode Modus; public AnimationCurve Kurve; public float Min, Alt = -1f;
            public SkriptArt Art; public string Rolle;
            public Fertig F; public bool An;
        }
        static readonly Dictionary<int, LokQ> lokQ = new Dictionary<int, LokQ>(16);

        static LokQ LokAufnehmen(Locomotive l, AudioSource s, SkriptArt art, string rolle)
        {
            if (s == null) return null;
            int id = s.GetInstanceID();
            if (lokQ.TryGetValue(id, out var q) && q.S != null) return q;
            q = new LokQ
            {
                S = s, L = l, Art = art, Rolle = rolle, Modus = s.rolloffMode, Min = s.minDistance,
                Kurve = s.rolloffMode == AudioRolloffMode.Custom ? s.GetCustomCurve(AudioSourceCurveType.CustomRolloff) : null
            };
            lokQ[id] = q;
            return q;
        }

        static void NachLokInit(Locomotive __instance)
        {
            if (gestoert[(int)SkriptArt.LokFahrt] && gestoert[(int)SkriptArt.LokLeerlauf]) return;
            try
            {
                var src = __instance.Sources;
                if (src != null) for (int i = 0; i < src.Length; i++) LokAufnehmen(__instance, src[i], SkriptArt.LokFahrt, "travel");
                var hum = LokAufnehmen(__instance, __instance.HummingSource, SkriptArt.LokLeerlauf, "idle");
                if (hum != null) { hum.Alt = __instance._hummingSourceRolloff; LokStellen(hum); }
            }
            catch (Exception ex) { Fehler(SkriptArt.LokLeerlauf, "Locomotive.Init", ex); }
        }

        static void LokStellen(LokQ q)
        {
            var s = q.S;
            if (s == null || !(q.Alt > 0)) return;
            double neu = Soll(q.Art, q.Alt);
            bool an = Wirksam(q.Art) && SkriptQuellenKern.Lohnt(q.Art, q.Alt, neu) && (q.Modus != AudioRolloffMode.Custom || (q.Kurve != null && q.Kurve.length >= 2));
            if (an && (q.F == null || q.F.Alt != q.Alt || Math.Abs(q.F.Neu - neu) > 0.01))
            {
                var altF = FesteQuellen.AltFunktion(q.Modus, q.Kurve, q.Min, q.Alt);
                q.F = QuellRegelKern.Kandidat(s.spatialBlend, q.Alt, (float)altF(0.0)) ? Bauen(altF, q.Alt, neu) : null;
            }
            an = an && q.F != null;
            if (an)
            {
                s.rolloffMode = AudioRolloffMode.Custom;
                s.SetCustomCurve(AudioSourceCurveType.CustomRolloff, q.F.Kurve);
                s.maxDistance = q.F.Neu;
            }
            else if (q.An)
            {
                if (q.Modus == AudioRolloffMode.Custom && q.Kurve != null) s.SetCustomCurve(AudioSourceCurveType.CustomRolloff, q.Kurve);
                s.rolloffMode = q.Modus;
                s.maxDistance = q.Alt;
            }
            q.An = an;
            Zaehlen(q.Art, q.Alt, an ? q.F.Neu : q.Alt, an);
            WandDaempfung.Melden(s, an, "scripted", SkriptQuellenKern.Name(q.Art) + " " + q.Rolle, eigen: q.L != null ? q.L.transform : null);   // 0.8.5
            int id = s.GetInstanceID();
            if (!laufend.TryGetValue(id, out var l)) laufend[id] = l = new Lauf();
            l.A = s; l.B = null; l.Art = q.Art; l.Name = q.Rolle + " " + s.gameObject.name; l.Alt = q.Alt; l.F = an ? q.F : null; l.Neustellen = null; l.LetztD = -1;
        }

        /// <summary>Postfix Locomotive.FadeIn: die Coroutine umhuellen (erster Schritt wie im Spiel, dann die gewaehlte Quelle stellen).</summary>
        static void NachLokEreignis(Locomotive __instance, Locomotive.SoundEvent sound, ref IEnumerator __result)
        {
            if (__result == null || sound == null || gestoert[(int)SkriptArt.LokFahrt]) return;
            __result = new LokFolge { Innen = __result, L = __instance, Ereignis = sound };
        }

        sealed class LokFolge : IEnumerator
        {
            public IEnumerator Innen; public Locomotive L; public Locomotive.SoundEvent Ereignis;
            bool erst = true;
            public object Current => Innen.Current;
            public void Reset() => Innen.Reset();
            public bool MoveNext()
            {
                if (!erst) return Innen.MoveNext();
                erst = false;
                AudioSource frei = null;
                try { frei = L != null ? L.FreeSource : null; } catch (Exception ex) { Statistik.Fehler(ex); }
                var vorher = kontext; var vorherName = kontextName; var vorherEigen = kontextEigen;
                kontext = SkriptArt.LokFahrt;
                kontextName = "event " + (Ereignis.Clip != null ? Ereignis.Clip.name : "?");
                kontextEigen = L != null ? L.transform : null;
                bool r;
                try { r = Innen.MoveNext(); }
                finally { kontext = vorher; kontextName = vorherName; kontextEigen = vorherEigen; }
                try
                {
                    // Quelle mit diesem Ereignis (FadeIn/Schleife); Einmal-Ereignisse ohne Einblendung liefen ueber PlayAtPoint (Kontext oben)
                    if (frei != null && frei.clip == Ereignis.Clip && frei.isPlaying)
                    {
                        var q = LokAufnehmen(L, frei, SkriptArt.LokFahrt, "travel");
                        if (q != null) { q.Alt = Ereignis.Rolloff; LokStellen(q); if (laufend.TryGetValue(frei.GetInstanceID(), out var l)) l.Name = "event " + (Ereignis.Clip != null ? Ereignis.Clip.name : "?"); }
                    }
                }
                catch (Exception ex) { Fehler(SkriptArt.LokFahrt, "Locomotive.FadeIn", ex); }
                return r;
            }
        }

        // =========================================================================================
        //  Airdrop: Flugzeug (je Bild gerufen), Kiste (Landung, Fallschirm, Fackel)
        // =========================================================================================
        static BetterSource flugQ;   // schneller Weg fuer das eine Flugzeug (ohne Tabellen-Suche je Bild)
        static int flugStand = -1;
        static float flugSoll;

        static void NachFlugzeug(ClientAirPlane __instance, TaggedClip clip)
        {
            if (gestoert[(int)SkriptArt.Flugzeug] || clip == null) return;
            var q = __instance._soundSource;
            if (q == null) return;
            if (!SkriptQuellenKern.NeuSetzen(ReferenceEquals(q, flugQ), stand, flugStand, q.MaxDistance, flugSoll)) return;   // Regelfall je Bild
            try
            {
                // zweites Flugzeug gleichzeitig: Stand je Quelle (sonst wuerde jedes Bild neu gestellt und gezaehlt)
                if (marken.TryGetValue(q, out var mk) && !SkriptQuellenKern.NeuSetzen(true, stand, mk.Stand, q.MaxDistance, mk.Soll)) { flugQ = q; flugStand = mk.Stand; flugSoll = mk.Soll; return; }
                var hs = EFTHardSettings.Instance;
                var kurve = hs != null ? hs.SoundRolloff : null;
                float alt = clip.Falloff;
                string vk = "-";
                if (hs != null && hs.PlaneVolumeCurve != null)
                    vk = hs.PlaneVolumeCurve.Evaluate(0f).ToString("0.###", CultureInfo.InvariantCulture) + " (near) .. " + hs.PlaneVolumeCurve.Evaluate(1f).ToString("0.###", CultureInfo.InvariantCulture) + " (from 300 m)";
                string info = $"prefab falloff {alt:0} m, volume curve of the game {vk} unchanged";
                // live: Neu() erhoeht 'stand', das naechste Bild stellt neu
                Stellen(q, SkriptArt.Flugzeug, alt, kurve, clip.Clip != null ? clip.Clip.name : "plane", true, null, info);
                Info($"airdrop plane - {info} -> range {q.MaxDistance:0} m{(Wirksam(SkriptArt.Flugzeug) ? "" : " (off)")}.");
                flugQ = q; flugStand = stand; flugSoll = q.MaxDistance;
                var mm = marken.GetValue(q, _ => new Marke());
                mm.Stand = stand; mm.Soll = q.MaxDistance;
            }
            catch (Exception ex) { Fehler(SkriptArt.Flugzeug, "ClientAirPlane.PlaySound", ex); }
        }

        static FieldInfo schirmFeld, fackelFeld;

        static void VorAbwurf(ClientAirDrop __instance, out bool __state)
        {
            var q = __instance._loopSoundSource;
            __state = q == null || q.PlayBackState != BetterSource.EPlayBackState.Playing;   // das Spiel spielt nur dann
        }

        static void NachAbwurf(ClientAirDrop __instance, TaggedClip clip, bool looped, bool __state)
        {
            if (!__state || clip == null) return;
            SkriptArt art = SkriptArt.Landung;
            try
            {
                var q = __instance._loopSoundSource;
                if (q == null) return;
                // AirdropSynchronizableObject erbt von Sirenix SerializedMonoBehaviour (Odin) -> Felder per Reflexion, ohne Sirenix-Verweis
                object so = __instance._syncObject;
                string rolle = "landing";
                if (so != null)
                {
                    if (schirmFeld == null || schirmFeld.DeclaringType != so.GetType() && !schirmFeld.DeclaringType.IsInstanceOfType(so))
                    { schirmFeld = AccessTools.Field(so.GetType(), "SqueakClip"); fackelFeld = AccessTools.Field(so.GetType(), "FlareSound"); }
                    if (schirmFeld != null && ReferenceEquals(clip, schirmFeld.GetValue(so))) { art = SkriptArt.Abwurf; rolle = "parachute"; }
                    else if (fackelFeld != null && ReferenceEquals(clip, fackelFeld.GetValue(so))) { art = SkriptArt.Abwurf; rolle = "crate flare"; }
                }
                if (gestoert[(int)art]) return;
                string name = rolle + " " + (clip.Clip != null ? clip.Clip.name : "?");
                string info = $"falloff {(art == SkriptArt.Landung ? "(LandingSoundBank)" : "(prefab)")} {clip.Falloff} m";
                Info($"airdrop {rolle} - {info} -> range {Soll(art, clip.Falloff):0} m{(Wirksam(art) ? "" : " (off)")}.");
                var basis = q.Preset != null ? q.Preset.SoundRolloff : null;
                var inst = __instance; var c = clip;
                Stellen(q, art, clip.Falloff, basis, name, looped,
                    looped ? (Action)(() => { var qq = inst._loopSoundSource; if (qq != null) Stellen(qq, art, c.Falloff, qq.Preset != null ? qq.Preset.SoundRolloff : null, name, true, null, info); }) : null, info);
            }
            catch (Exception ex) { Fehler(art, "ClientAirDrop.PlaySound", ex); }
        }

        // =========================================================================================
        //  BTR: Turm + Lufthorn (Motor: Abschnitt 17)
        // =========================================================================================
        static void NachTurm(VehicleRotationSoundPlayer __instance)
        {
            if (gestoert[(int)SkriptArt.BtrTurm]) return;
            try
            {
                var q = __instance._source;
                if (q == null) return;
                var inst = __instance;
                Stellen(q, SkriptArt.BtrTurm, inst._maxDistance, q.Preset != null ? q.Preset.SoundRolloff : null, "turret " + inst.gameObject.name, true,
                        () => { var qq = inst != null ? inst._source : null; if (qq != null) Stellen(qq, SkriptArt.BtrTurm, inst._maxDistance, qq.Preset != null ? qq.Preset.SoundRolloff : null, "turret " + inst.gameObject.name, true, null); });
            }
            catch (Exception ex) { Fehler(SkriptArt.BtrTurm, "VehicleRotationSoundPlayer.SetupSource", ex); }
        }

        static void VorHorn(BtrSoundController __instance)
        {
            if (gestoert[(int)SkriptArt.BtrHorn]) return;
            try
            {
                var q = __instance._airHornSource;
                if (q == null) return;
                float alt = q.Preset != null && q.Preset.DefaultMaxDistance > 0 ? q.Preset.DefaultMaxDistance : 500f;
                float vorher = marken.TryGetValue(q, out var m) && m.An ? m.Alt : q.MaxDistance;   // Stand vor dem ersten Eingriff
                string info = $"pooled source, range before {vorher:0.#} m (not set by the game), group default {alt:0.#} m";
                var inst = __instance;
                Stellen(q, SkriptArt.BtrHorn, alt, q.Preset != null ? q.Preset.SoundRolloff : null, "air horn", true,
                        () => { var qq = inst != null ? inst._airHornSource : null; if (qq != null) Stellen(qq, SkriptArt.BtrHorn, alt, qq.Preset != null ? qq.Preset.SoundRolloff : null, "air horn", true, null, info, vorher); },
                        info, vorher);
            }
            catch (Exception ex) { Fehler(SkriptArt.BtrHorn, "BtrSoundController.OnReadyToDeparture", ex); }
        }

        // =========================================================================================
        //  Leuchtpatrone, Moerser-Pfeifen
        // =========================================================================================
        static void NachPatrone(FlarePatronSound __instance)
        {
            if (gestoert[(int)SkriptArt.Leuchtpatrone]) return;
            try
            {
                var q = __instance._source;
                if (q == null) return;
                var inst = __instance;
                Stellen(q, SkriptArt.Leuchtpatrone, inst._rolloff, q.Preset != null ? q.Preset.SoundRolloff : null, "flare round", true,
                        () => { var qq = inst != null ? inst._source : null; if (qq != null) Stellen(qq, SkriptArt.Leuchtpatrone, inst._rolloff, qq.Preset != null ? qq.Preset.SoundRolloff : null, "flare round", true, null); });
            }
            catch (Exception ex) { Fehler(SkriptArt.Leuchtpatrone, "FlarePatronSound.InitSource", ex); }
        }

        static void NachPfeifen(ArtilleryShellingSoundControllerClient __instance, BetterSource source, AudioClip clip, float rolloff)
        {
            if (gestoert[(int)SkriptArt.Pfeifen] || source == null) return;
            try
            {
                var so = __instance._artilleryShellingSounds;
                var kurve = so != null ? so.whistleRolloffCurve : null;
                Stellen(source, SkriptArt.Pfeifen, rolloff, kurve, clip != null ? clip.name : "whistle", true, null);
            }
            catch (Exception ex) { Fehler(SkriptArt.Pfeifen, "PlayWhistleSound", ex); }
        }
    }
}
