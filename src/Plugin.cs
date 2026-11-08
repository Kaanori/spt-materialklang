using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Audio.SpatialSystem;
using Audio.SpatialSystem.SpatialAudioCalculator;
using Audio.SpatialSystem.SpatialAudioCalculator.Data;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using EFT.Ballistics;
using HarmonyLib;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Materialklang
{
    // Materialgruppen fuer das F12-Menue (39 MaterialType-Werte -> 13 Gruppen).
    public enum Gruppe
    {
        ConcreteStone, Soil, GravelSnow, MetalThick, MetalThin, WoodThick, WoodThin,
        Glass, Soft, GridFenceGrass, Body, Water, Unknown, Door
    }

    [BepInPlugin("kaan.materialklang", "Materialklang", Plugin.Version)]
    [BepInDependency("me.sol.sain", BepInDependency.DependencyFlags.SoftDependency)]
    [BepInDependency("red.soundenginebackport", BepInDependency.DependencyFlags.SoftDependency)]
    public class Plugin : BaseUnityPlugin
    {
        public const string Version = "0.5.0";
        internal static ManualLogSource Log;
        internal static ConfigEntry<bool> Aktiv;
        internal static ConfigEntry<bool> Diagnose;
        internal static ConfigEntry<float> Gesamtstaerke;
        internal static ConfigEntry<float> FreiwegSchwelle;
        internal const int AnzahlGruppen = 14;
        internal static readonly ConfigEntry<float>[] Dicke = new ConfigEntry<float>[AnzahlGruppen];
        internal static readonly ConfigEntry<float>[] Oberflaeche = new ConfigEntry<float>[AnzahlGruppen];

        // Startwerte: Daempfung je Meter Dicke (K) und fester Anteil je Durchgang (S).
        // Ergebnis je Strahl: Restenergie = exp(-(S + K*Dicke) * Gesamtstaerke). Vanilla: exp(-0.3*Dicke).
        internal static readonly (Gruppe g, string name, float k, float s)[] Startwerte =
        {
            (Gruppe.ConcreteStone,     "Concrete, Stone, Tile, Asphalt",      5.0f, 0.30f),
            (Gruppe.Soil,           "Soil, Mud, Terrain",             3.0f, 0.20f),
            (Gruppe.GravelSnow,     "Gravel, Pebbles, Snow",              2.0f, 0.10f),
            (Gruppe.MetalThick,     "Thick Metal (Container, Steel Door)",1.0f, 0.80f),
            (Gruppe.MetalThin,    "Thin Metal (Car, Corrugated Sheet)",    0.8f, 0.35f),
            (Gruppe.WoodThick,       "Thick Wood (Wall, Beam)",          2.0f, 0.25f),
            (Gruppe.WoodThin,      "Thin Wood (Board, Crate)",         1.2f, 0.12f),
            (Gruppe.Glass,           "Glass",                                1.0f, 0.20f),
            (Gruppe.Soft,          "Soft (Cardboard, Fabric, Plastic, Rubber)",0.8f, 0.08f),
            (Gruppe.GridFenceGrass, "Grate, Chain-link Fence, Grass",          0.05f,0.01f),
            (Gruppe.Body,        "Body, Armor, Helmet",             1.5f, 0.10f),
            (Gruppe.Water,         "Water",                              1.0f, 0.10f),
            (Gruppe.Unknown,      "Unknown (like vanilla)",             0.3f, 0.00f),
            (Gruppe.Door,           "Doors (game object, base value)",     0.0f, 0.60f),
        };

        void Awake()
        {
            Log = Logger;
            Einstellungen.Anlegen(Config);
            if (Einstellungen.Migriert > 0)
                Log.LogInfo($"Settings: {Einstellungen.Migriert} values carried over from the German names of version 0.4.0.");
            SceneManager.activeSceneChanged += (_, __) =>
            {
                MaterialCache.Leeren(); BotHoeren.Leeren(); HallZonen.SzeneGewechselt();
                TuerenFenster.Leeren(); WandZwischenRaeumen.Leeren();
            };
            var h = new Harmony("kaan.materialklang");
            h.PatchAll(typeof(Plugin).Assembly);
            BotHoeren.Anwenden(h);
            TuerenFenster.Anwenden(h);
            WandZwischenRaeumen.Anwenden(h);
            SebAkustik.Anwenden(h);
            Physik.Anwenden(h);
            Log.LogInfo($"Materialklang {Version} (beta) loaded.");
        }

        void Update() { HallZonen.Tick(); Statistik.VielleichtLoggen(); }
    }

    internal static class MaterialCache
    {
        static readonly Dictionary<int, byte> Karte = new Dictionary<int, byte>(4096);

        public static void Leeren() => Karte.Clear();

        public static int Get(int colliderId)
        {
            if (Karte.TryGetValue(colliderId, out byte m)) return m;
            int r = (int)Gruppe.Unknown;
            var col = Resources.InstanceIDToObject(colliderId) as Collider;
            if (col is TerrainCollider) r = (int)Gruppe.Soil;
            else if (col != null && TuerenFenster.IstTuer(col)) r = (int)Gruppe.Door;
            else if (col != null)
            {
                BaseBallistic bb;
                if (!col.TryGetComponent(out bb) && col.transform.parent != null)
                    col.transform.parent.TryGetComponent(out bb);
                if (bb is BallisticCollider bc) r = (int)Zuordnen(bc.TypeOfMaterial);
                else if (bb is TerrainBallistic) r = (int)Gruppe.Soil;
                Statistik.MaterialGesehen(bb is BallisticCollider bc2 ? bc2.TypeOfMaterial.ToString() : "no material", col.gameObject.layer);
            }
            Karte[colliderId] = (byte)r;
            return r;
        }

        static Gruppe Zuordnen(MaterialType t)
        {
            switch (t)
            {
                case MaterialType.Concrete: case MaterialType.Stone: case MaterialType.Tile:
                case MaterialType.Asphalt: case MaterialType.GenericHard:
                    return Gruppe.ConcreteStone;
                case MaterialType.Soil: case MaterialType.SoilForest: case MaterialType.Mud: case MaterialType.Swamp:
                    return Gruppe.Soil;
                case MaterialType.Gravel: case MaterialType.Pebbles: case MaterialType.Snow:
                    return Gruppe.GravelSnow;
                case MaterialType.MetalThick: case MaterialType.MetalNoDecal: case MaterialType.GarbageMetal:
                    return Gruppe.MetalThick;
                case MaterialType.MetalThin:
                    return Gruppe.MetalThin;
                case MaterialType.WoodThick:
                    return Gruppe.WoodThick;
                case MaterialType.WoodThin:
                    return Gruppe.WoodThin;
                case MaterialType.Glass: case MaterialType.GlassVisor: case MaterialType.GlassShattered:
                    return Gruppe.Glass;
                case MaterialType.Cardboard: case MaterialType.GarbagePaper: case MaterialType.Fabric:
                case MaterialType.GenericSoft: case MaterialType.Plastic: case MaterialType.Rubber: case MaterialType.Tyre:
                    return Gruppe.Soft;
                case MaterialType.Chainfence: case MaterialType.Grate: case MaterialType.GrassHigh: case MaterialType.GrassLow:
                    return Gruppe.GridFenceGrass;
                case MaterialType.Body: case MaterialType.BodyArmor: case MaterialType.Helmet: case MaterialType.HelmetRicochet:
                    return Gruppe.Body;
                case MaterialType.Water: case MaterialType.WaterPuddle:
                    return Gruppe.Water;
                default:
                    return Gruppe.Unknown;
            }
        }
    }

    // Nach dem Abschluss von Strahlen + Burst-Job ist alles im Hauptthread lesbar.
    // Wir rechnen das Ergebnis aus denselben Treffern neu, nur mit Materialwerten statt eines globalen Werts.
    [HarmonyPatch(typeof(TransmissionCalculator), nameof(TransmissionCalculator.CompleteCurrentCalculation))]
    internal static class TransmissionPatch
    {
        static void Prefix(TransmissionCalculator __instance, out bool __state)
        {
            __state = __instance._buffersAllocated && __instance._isCalculationInProgress
                      && __instance._currentStep == TransmissionCalculator.ECalculationStep.Complete;
        }

        static void Postfix(TransmissionCalculator __instance, bool __state)
        {
            if (!__state || !Plugin.Aktiv.Value) return;
            try
            {
                Statistik.MaskeMerken(__instance._queryParams.layerMask);
                int n = __instance._dynamicTotalRayCount;
                var hits = __instance._allHits;
                if (n <= 0 || !hits.IsCreated || hits.Length < 2 * n || !__instance._resultBuffer.IsCreated) return;

                float minT = __instance.float_7, maxT = __instance.float_8, maxD = __instance._maxDistance;
                float staerke = Plugin.Gesamtstaerke.Value;
                bool physik = Physik.An;
                float sumE = 0f, sumET = 0f;
                bool gueltig = false;
                bool farbe = Farbe.Aktiv;
                var fs = new FarbeSumme();

                for (int i = 0; i < n; i++)
                {
                    RaycastHit a = hits[i], b = hits[i + n];
                    int ia = a.colliderInstanceID, ib = b.colliderInstanceID;
                    bool ha = ia != 0 && a.distance < maxD, hb = ib != 0 && b.distance < maxD;
                    float e, th;
                    if (!ha && !hb) { e = 1f; th = 0f; if (farbe) fs.Frei(); }
                    else if (ha != hb || ia != ib) continue;          // wie Vanilla: kein Durchgang
                    else
                    {
                        th = Vector3.Distance(a.point, b.point);
                        if (th >= maxT) continue;                      // wie Vanilla: zu dick
                        int m = MaterialCache.Get(ia);
                        float x = Plugin.Oberflaeche[m].Value + (th >= minT ? Plugin.Dicke[m].Value * th : 0f);
                        e = physik ? Physik.EnergieAusDb(Physik.Daemmung(m, th)) : Mathf.Clamp01(Mathf.Exp(-x * staerke));
                        if (farbe) fs.Add(m, x * staerke);
                        Statistik.Treffer(m);
                    }
                    gueltig = true;
                    sumE += e;
                    sumET += e * th;
                }

                var alt = __instance._resultBuffer[0];
                var neu = new TransmissionPathResult
                {
                    RemainingEnergy = physik ? 1f - Physik.WertungAusEnergie(sumE / n) : Mathf.Clamp01(sumE / n),
                    TotalThickness = sumE > 0f ? sumET / sumE : float.MaxValue,
                    PathPotentiallyValid = gueltig
                };
                __instance._resultBuffer[0] = neu;
                if (farbe) Farbe.Setze(__instance, fs, gueltig);
                Statistik.Rechnung(alt.RemainingEnergy, neu.RemainingEnergy);
            }
            catch (Exception ex)
            {
                Statistik.Fehler(ex);
            }
        }
    }

    // Optional: Schwelle, ab der ein Weg als "frei" gilt, im Client ueberschreiben.
    [HarmonyPatch]
    internal static class FreiwegPatch
    {
        static IEnumerable<MethodBase> TargetMethods() =>
            AccessTools.GetDeclaredConstructors(typeof(SpatialAudioOcclusionCombinedCalculator)).Cast<MethodBase>();

        static void Postfix(SpatialAudioOcclusionCombinedCalculator __instance)
        {
            float v = Plugin.FreiwegSchwelle.Value;
            if (Physik.An) __instance._minClearPathScore = Physik.Freiweg.Value;
            else if (v >= 0f) __instance._minClearPathScore = v;
        }
    }

    internal static class Statistik
    {
        static float naechsterLog;
        static int rechnungen, gedaempft, fehler;
        static double summeAlt, summeNeu;
        static readonly int[] treffer = new int[Plugin.AnzahlGruppen];
        static readonly Dictionary<string, int> materialien = new Dictionary<string, int>();
        static readonly Dictionary<int, int> layer = new Dictionary<int, int>();
        static int maske = -1;
        internal static int AudioMaske;   // Layer-Maske der Audio-Strahlen (zuletzt gesehen)

        public static void MaskeMerken(int m)
        {
            AudioMaske = m;
            if (m == maske || !Plugin.Diagnose.Value) return;
            maske = m;
            var namen = Enumerable.Range(0, 32).Where(i => (m & (1 << i)) != 0)
                .Select(i => $"{i}:{LayerMask.LayerToName(i)}");
            Plugin.Log.LogInfo($"[Diagnostics] Audio rays hit layers: {string.Join(", ", namen)}");
        }

        public static void MaterialGesehen(string mat, int lay)
        {
            materialien[mat] = materialien.TryGetValue(mat, out int c) ? c + 1 : 1;
            layer[lay] = layer.TryGetValue(lay, out int d) ? d + 1 : 1;
        }

        public static void Treffer(int g) => treffer[g]++;

        public static void Rechnung(float alt, float neu)
        {
            rechnungen++;
            summeAlt += alt;
            summeNeu += neu;
            if (neu < 0.5f) gedaempft++;
        }

        public static void Fehler(Exception ex)
        {
            if (fehler++ < 3) Plugin.Log.LogError($"Materialklang: {ex}");
        }

        public static void VielleichtLoggen()
        {
            if (!Plugin.Diagnose.Value || Time.realtimeSinceStartup < naechsterLog) return;
            naechsterLog = Time.realtimeSinceStartup + 30f;
            SebAkustik.Loggen();
            Physik.Loggen();
            TuerenFenster.DiagnoseLoggen();
            WandZwischenRaeumen.DiagnoseLoggen();
            if (rechnungen == 0) return;
            var tr = string.Join(", ", Enumerable.Range(0, Plugin.AnzahlGruppen).Where(i => treffer[i] > 0)
                .Select(i => $"{(Gruppe)i}={treffer[i]}"));
            var mats = string.Join(", ", materialien.OrderByDescending(p => p.Value).Take(12).Select(p => $"{p.Key}={p.Value}"));
            var lays = string.Join(", ", layer.OrderByDescending(p => p.Value).Select(p => $"{LayerMask.LayerToName(p.Key)}={p.Value}"));
            Plugin.Log.LogInfo($"[Diagnostics] {rechnungen} calculations, remaining energy vanilla {summeAlt / rechnungen:F2} -> new {summeNeu / rechnungen:F2}, " +
                               $"attenuated {gedaempft}. Ray hits: {tr}");
            Plugin.Log.LogInfo($"[Diagnostics] New colliders: {mats} | layers: {lays}");
            if (HallZonen.ZonenAnzahl > 0)
                Plugin.Log.LogInfo($"[Diagnostics] Reverb: {HallZonen.ZonenAnzahl} zones active.");
            if (BotHoeren.Ersetzt + BotHoeren.Fallback > 0)
                Plugin.Log.LogInfo($"[Diagnostics] Bots: {BotHoeren.Ersetzt} values replaced, {BotHoeren.CacheTreffer} from cache, {BotHoeren.Fallback} SAIN value (budget), {BotHoeren.Strahlen} rays.");
            BotHoeren.Ersetzt = BotHoeren.Fallback = BotHoeren.CacheTreffer = BotHoeren.Strahlen = 0;
            rechnungen = gedaempft = 0; summeAlt = summeNeu = 0;
            Array.Clear(treffer, 0, treffer.Length);
            materialien.Clear(); layer.Clear();
        }
    }
}
