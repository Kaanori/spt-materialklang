using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
using BepInEx.Bootstrap;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;

namespace Materialklang
{
    public enum BotModus { Off, QuieterOnly, Replace }

    // Punkt 6: Bots (SAIN) hoeren materialabhaengig.
    // SAIN entscheidet in HearingAnalysis.CalcOcclusionMod nur "Sichtlinie ja -> 1, sonst fester Wert (Schuss 0.8)".
    // Wir ersetzen den festen Wert durch die Restenergie entlang der geraden Linie Klang -> Bot-Kopf.
    internal static class BotHoeren
    {
        internal static ConfigEntry<BotModus> Modus;
        internal static ConfigEntry<float> Staerke, Exponent, BodenSchuss, BodenLeise, BodenSchritt, BodenSonst,
            NahDistanz, NahBoden, MaxDicke, StandardDicke, CacheZeit, Toleranz;
        internal static ConfigEntry<int> MaxFlaechen, Budget;

        // Kontext des gerade geprueften Klangs (alles im Hauptthread, synchron)
        static bool hatKontext;
        static Vector3 kQuelle, kOhr;
        static int kBot, kQuellId, kTyp;   // kTyp: 0 Schuss, 1 Schalldaempfer, 2 Schritt, 3 Sonstiges
        static float kDist;
        internal static int Ersetzt, Fallback, CacheTreffer, Strahlen;

        internal static void Binden(ConfigFile c)
        {
            const string s = "3. Bot Hearing (SAIN)";
            Modus = c.Bind(s, "Mode", BotModus.Off,
                "Off = like SAIN. QuieterOnly = bots never hear through walls better than before, but concrete muffles more than wood. Replace = only the material counts. Needs SAIN (tested with 4.5.1). Takes effect after a game restart.");
            Staerke = c.Bind(s, "Strength", 1f, new ConfigDescription("Multiplies the material attenuation for bots only.", new AcceptableValueRange<float>(0f, 3f)));
            Exponent = c.Bind(s, "Exponent", 0.5f, new ConfigDescription("Remaining energy -> hearing range (0.5 = square root).", new AcceptableValueRange<float>(0.1f, 2f)));
            BodenSchuss = c.Bind(s, "Floor: gunshot", 0.35f, new ConfigDescription("Lowest hearing-range factor for gunshots.", new AcceptableValueRange<float>(0f, 1f)));
            BodenLeise = c.Bind(s, "Floor: suppressed shot", 0.25f, new ConfigDescription("Lowest hearing-range factor for suppressed shots.", new AcceptableValueRange<float>(0f, 1f)));
            BodenSchritt = c.Bind(s, "Floor: footsteps", 0.15f, new ConfigDescription("Lowest hearing-range factor for footsteps and sprinting.", new AcceptableValueRange<float>(0f, 1f)));
            BodenSonst = c.Bind(s, "Floor: other sounds", 0.2f, new ConfigDescription("Lowest hearing-range factor for all other sounds.", new AcceptableValueRange<float>(0f, 1f)));
            NahDistanz = c.Bind(s, "Close range (m)", 8f, new ConfigDescription("Within this distance the close-range floor applies.", new AcceptableValueRange<float>(0f, 50f)));
            NahBoden = c.Bind(s, "Close-range floor", 0.5f, new ConfigDescription("Lowest hearing-range factor inside the close range.", new AcceptableValueRange<float>(0f, 1f)));
            MaxDicke = c.Bind(s, "Max. thickness (m)", 2f, new ConfigDescription("Thicker (hills, solid bodies) -> floor value.", new AcceptableValueRange<float>(0.2f, 10f)));
            StandardDicke = c.Bind(s, "Default thickness (m)", 0.1f, new ConfigDescription("For one-sided surfaces without a back face.", new AcceptableValueRange<float>(0f, 1f)));
            MaxFlaechen = c.Bind(s, "Max. surfaces", 8, new ConfigDescription("More surfaces in the way -> floor value.", new AcceptableValueRange<int>(1, 16)));
            Budget = c.Bind(s, "Calculations per frame", 6, new ConfigDescription("CPU limit. Once reached, the old value / SAIN's value is used.", new AcceptableValueRange<int>(1, 50)));
            CacheZeit = c.Bind(s, "Cache time (s)", 0.75f, new ConfigDescription("How long a result is reused for the same bot and sound source.", new AcceptableValueRange<float>(0f, 5f)));
            Toleranz = c.Bind(s, "Cache tolerance (m)", 1f, new ConfigDescription("A cached result is only reused while source and bot moved less than this.", new AcceptableValueRange<float>(0.1f, 5f)));
        }

        internal static void Anwenden(Harmony h)
        {
            if (Modus.Value == BotModus.Off) { Plugin.Log.LogInfo("Bot hearing: off."); return; }
            if (!Chainloader.PluginInfos.TryGetValue("me.sol.sain", out var info))
            { Plugin.Log.LogInfo("Bot hearing: SAIN not found -> off."); return; }
            if (info.Metadata.Version.ToString() != "4.5.1")
                Plugin.Log.LogWarning($"Bot hearing: tested with SAIN 4.5.1, found {info.Metadata.Version}.");

            var check = Ziel("SAIN.Classes.Bot.Sense.Hearing.HearingAnalysis", "CheckIfSoundHeard", null);
            var occl = Ziel("SAIN.Classes.Bot.Sense.Hearing.HearingAnalysis", "CalcOcclusionMod", typeof(float));
            if (check == null || occl == null) return;
            h.Patch(check, prefix: new HarmonyMethod(typeof(BotHoeren), nameof(KontextPrefix)),
                    finalizer: new HarmonyMethod(typeof(BotHoeren), nameof(KontextFinalizer)));
            h.Patch(occl, postfix: new HarmonyMethod(typeof(BotHoeren), nameof(OcclusionPostfix)));
            Plugin.Log.LogInfo($"Bot hearing: active ({Modus.Value}).");
        }

        static MethodInfo Ziel(string typ, string name, Type ret)
        {
            var t = AccessTools.TypeByName(typ);
            var m = t == null ? null : AccessTools.Method(t, name);
            if (m == null || (ret != null && m.ReturnType != ret))
            {
                Plugin.Log.LogWarning($"Bot hearing: SAIN target {typ}.{name} not found (SAIN update?) -> bots hear like SAIN.");
                return null;
            }
            return m;
        }

        static void KontextPrefix(object[] __args)
        {
            hatKontext = false;
            try { KontextSetzen(__args[0]); } catch (Exception e) { Statistik.Fehler(e); }
        }

        static Exception KontextFinalizer(Exception __exception) { hatKontext = false; return __exception; }

        // SAIN-Zugriffe gekapselt: fehlt nach einem SAIN-Update ein Feld, landet der Fehler hier im catch.
        [MethodImpl(MethodImplOptions.NoInlining)]
        static void KontextSetzen(object boxed)
        {
            var s = (SAIN.Components.PlayerComponentSpace.AISoundData)boxed;
            var typ = s.SoundType;
            kTyp = typ == SAIN.Preset.Shared.Enums.SAINSoundType.Shot ? 0
                 : typ == SAIN.Preset.Shared.Enums.SAINSoundType.SuppressedShot ? 1
                 : (typ == SAIN.Preset.Shared.Enums.SAINSoundType.FootStep || typ == SAIN.Preset.Shared.Enums.SAINSoundType.Sprint) ? 2 : 3;
            kQuelle = s.Sound.Position + Vector3.up * (s.IsGunShot ? 0f : 0.3f);   // Schritte: Fusspunkt anheben
            kOhr = s.Bot.Transform.HeadData.HeadPosition;
            kBot = s.Bot.GetInstanceID();
            kQuellId = s.Sound.PlayerComponent != null ? s.Sound.PlayerComponent.GetInstanceID() : 0;
            kDist = s.PlayerDistance;
            hatKontext = true;
        }

        static void OcclusionPostfix(ref float __result)
        {
            if (!hatKontext || __result >= 0.999f || !Plugin.Aktiv.Value) return;   // Sichtlinie frei -> unveraendert
            try
            {
                if (!Faktor(out float mat)) { Fallback++; return; }
                float neu = Modus.Value == BotModus.QuieterOnly ? Mathf.Min(__result, mat) : mat;
                Ersetzt++;
                __result = neu;
            }
            catch (Exception e) { Statistik.Fehler(e); }
        }

        struct Eintrag { public Vector3 q, o; public float zeit, faktor; }
        static readonly Dictionary<long, Eintrag> cache = new Dictionary<long, Eintrag>(512);
        static int budgetFrame, budgetRest;
        static readonly float[] ein = new float[16], aus = new float[16];
        static readonly int[] einId = new int[16], ausId = new int[16];

        internal static void Leeren() => cache.Clear();

        static bool Faktor(out float f)
        {
            long key = ((long)kBot << 32) ^ (uint)kQuellId;
            float jetzt = Time.time, tol2 = Toleranz.Value * Toleranz.Value;
            bool da = cache.TryGetValue(key, out var e);
            if (da && jetzt - e.zeit < CacheZeit.Value && (e.q - kQuelle).sqrMagnitude < tol2 && (e.o - kOhr).sqrMagnitude < tol2)
            { CacheTreffer++; f = Grenzen(e.faktor); return true; }

            if (Time.frameCount != budgetFrame) { budgetFrame = Time.frameCount; budgetRest = Budget.Value; }
            if (budgetRest <= 0) { f = da ? Grenzen(e.faktor) : 1f; return da; }
            budgetRest--;

            // Exponent 0.5: Energie -> Amplitude; bei kugelfoermiger Ausbreitung (p ~ 1/r) skaliert die Hoerweite mit
            // dem Amplitudenfaktor (Abstandsgesetz, -6 dB je Abstandsverdopplung; z.B. Bies & Hansen).
            float roh = Mathf.Pow(Durchgang(kQuelle, kOhr), Exponent.Value);
            cache[key] = new Eintrag { q = kQuelle, o = kOhr, zeit = jetzt, faktor = roh };
            f = Grenzen(roh);
            return true;
        }

        static float Grenzen(float f)
        {
            float boden = kTyp == 0 ? BodenSchuss.Value : kTyp == 1 ? BodenLeise.Value : kTyp == 2 ? BodenSchritt.Value : BodenSonst.Value;
            if (kDist <= NahDistanz.Value) boden = Mathf.Max(boden, NahBoden.Value);
            return Mathf.Clamp(f, boden, 1f);
        }

        // Vorwaerts alle Eintrittsflaechen, rueckwaerts alle Austrittsflaechen, je Collider paaren -> Dicke je Wand.
        static float Durchgang(Vector3 a, Vector3 b)
        {
            Vector3 d = b - a; float len = d.magnitude;
            if (len < 0.05f) return 1f;
            d /= len;
            int mask = LayersMaskController.HighPolyWithTerrainMask, max = MaxFlaechen.Value;
            int ne = Marsch(a, d, len, mask, max, ein, einId, false);
            int na = Marsch(b, -d, len, mask, max, aus, ausId, true);
            if (ne >= max) return 0f;
            float x = 0f; ulong benutzt = 0;
            for (int i = 0; i < ne; i++)
            {
                float dicke = -1f;
                for (int j = na - 1; j >= 0; j--)
                    if ((benutzt & (1UL << j)) == 0 && ausId[j] == einId[i] && aus[j] >= ein[i])
                    { dicke = aus[j] - ein[i]; benutzt |= 1UL << j; break; }
                if (dicke < 0f) dicke = StandardDicke.Value;
                if (dicke > MaxDicke.Value) return 0f;
                int m = MaterialCache.Get(einId[i]);
                x += Physik.An ? Physik.Daemmung(m, dicke) : Plugin.Oberflaeche[m].Value + Plugin.Dicke[m].Value * dicke;
            }
            if (Physik.An) return Physik.EnergieAusDb(Mathf.Min(x, Physik.Dynamik.Value) * Physik.Lautheit.Value);   // Exponent 0.5 -> Amplitude = Hoerweite
            return Mathf.Exp(-x * Plugin.Gesamtstaerke.Value * Staerke.Value);
        }

        static int Marsch(Vector3 von, Vector3 dir, float len, int mask, int max, float[] t, int[] id, bool rueck)
        {
            int n = 0; float weg = 0f;
            while (n < max && weg < len && Physics.Raycast(von, dir, out RaycastHit h, len - weg, mask, QueryTriggerInteraction.Ignore))
            {
                weg += h.distance;
                t[n] = rueck ? len - weg : weg;
                id[n] = h.colliderInstanceID;
                n++; Strahlen++;
                weg += 0.01f;
                von = h.point + dir * 0.01f;
            }
            return n;
        }
    }
}
