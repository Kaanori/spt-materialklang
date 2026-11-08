using System;
using System.Collections.Generic;
using System.Reflection;
using Audio.Extensions;
using Audio.SpatialSystem;
using BepInEx.Configuration;
using Comfort.Common;
using EFT;
using EFT.Ballistics;
using EFT.Interactive;
using HarmonyLib;
using UnityEngine;

namespace Materialklang
{
    // Tueren und Fenster daempfen nach Material und Zustand.
    // Vanilla: geschlossene Tuer = Schliessung 1.0, Fenster 0.73, egal ob Holz oder Stahl. Die Wegkosten eines
    // Portals sind (Abstandsanteil + Schliessung) * WallOcclusion(Raeume). Wir setzen die Schliessung so, dass
    // genau der Zielwert herauskommt: Schliessung = Ziel / WallOcclusion.
    // Eingriffspunkt: die Portaldaten, die der (Burst-)Job spaeter liest - nicht der Job selbst.
    internal static class TuerenFenster
    {
        internal static ConfigEntry<bool> Aktiv;
        internal static ConfigEntry<float> HolzDuenn, HolzDick, MetallDuenn, MetallDick, FensterHeil;

        // -1 = dieses Portal bleibt Vanilla. Nicht gefundene Tueren werden NICHT gemerkt (Welt noch nicht fertig).
        static readonly Dictionary<short, float> Ziele = new Dictionary<short, float>();
        static readonly Dictionary<int, float> TuerZiele = new Dictionary<int, float>();   // Door-InstanceID -> Zielwert
        static readonly Dictionary<int, float> TuerS_Cache = new Dictionary<int, float>(); // Collider-ID -> S

        static int angepasst, tuerGeaendert, fensterGeaendert, unbekannteTuer;
        static double summeAlt, summeNeu;

        internal static void Binden(ConfigFile c)
        {
            const string s = "4. Doors and Windows";
            Aktiv = c.Bind(s, "Enabled", true,
                "Doors and windows attenuate by material and state (off = vanilla: a closed door is always the same). Changed values take effect from the next raid.");
            var bereich = new AcceptableValueRange<float>(0f, 0.95f);
            HolzDuenn = c.Bind(s, "Door thin wood", 0.35f,
                new ConfigDescription("How much sound a closed door made of thin wood absorbs (0 = none, 0.95 = almost all).", bereich));
            HolzDick = c.Bind(s, "Door thick wood", 0.45f, new ConfigDescription("As above, thick wood.", bereich));
            MetallDuenn = c.Bind(s, "Door thin metal", 0.6f, new ConfigDescription("As above, thin metal.", bereich));
            MetallDick = c.Bind(s, "Door thick metal", 0.8f, new ConfigDescription("As above, thick metal (steel door).", bereich));
            FensterHeil = c.Bind(s, "Window intact", 0.55f,
                new ConfigDescription("How much sound an intact window absorbs. Broken = 0; for window rows in proportion to the free area.", bereich));
        }

        internal static void Anwenden(Harmony h)
        {
            try
            {
                var upd = AccessTools.DeclaredMethod(typeof(SpatialAudioDataContainer), "UpdatePortalDataInternal");
                var neu = AccessTools.DeclaredMethod(typeof(SpatialAudioDataContainer), "CreatePortalData");
                if (upd == null || neu == null)
                {
                    Plugin.Log.LogWarning("Doors/windows: target in SpatialAudioDataContainer not found (game update?) -> vanilla.");
                    return;
                }
                h.Patch(upd, prefix: new HarmonyMethod(typeof(TuerenFenster), nameof(UpdatePrefix)));
                h.Patch(neu, postfix: new HarmonyMethod(typeof(TuerenFenster), nameof(CreatePostfix)));
                Plugin.Log.LogInfo("Doors/windows: hooked.");
            }
            catch (Exception e) { Statistik.Fehler(e); }
        }

        internal static void Leeren()
        {
            Ziele.Clear(); TuerZiele.Clear(); TuerS_Cache.Clear();
        }

        internal static void DiagnoseLoggen()
        {
            if (angepasst == 0) return;
            Plugin.Log.LogInfo($"[Diagnostics] Doors/windows: {angepasst} portal values adjusted (doors {tuerGeaendert}, windows {fensterGeaendert}), " +
                               $"closure on average {summeAlt / angepasst:F2} -> {summeNeu / angepasst:F2}, doors with unknown material {unbekannteTuer}.");
            angepasst = tuerGeaendert = fensterGeaendert = unbekannteTuer = 0; summeAlt = summeNeu = 0;
        }

        // ---- Harmony ----

        static void UpdatePrefix(SpatialAudioDataContainer __instance, short portalID, ref float closureLevel)
        {
            if (!Aktiv.Value) return;
            try
            {
                if (__instance.PortalsByID != null && __instance.PortalsByID.TryGetValue(portalID, out var portal))
                    closureLevel = Anpassen(portal, closureLevel);
            }
            catch (Exception e) { Statistik.Fehler(e); }
        }

        static void CreatePostfix(BaseSpatialAudioPortal portal, ref RoomPair.AudioPortalData __result)
        {
            if (!Aktiv.Value) return;
            try { __result.closureLevel = Anpassen(portal, __result.closureLevel); }
            catch (Exception e) { Statistik.Fehler(e); }
        }

        // ---- Rechnung ----

        static float Anpassen(BaseSpatialAudioPortal p, float closure)
        {
            if (p == null) return closure;
            var mw = p as MultiWindowPortal;
            var sp = p as SpatialAudioPortal;
            if (mw == null && (sp == null || (sp.portalType != BaseSpatialAudioPortal.PortalType.Opening
                                              && sp.portalType != BaseSpatialAudioPortal.PortalType.Window)))
                return closure;                                   // Trigger/statische Portale: Vanilla

            float ziel = Ziel(p, sp, mw);                         // Schliessung im geschlossenen Zustand
            if (ziel < 0f) return closure;

            float zu = Mathf.Lerp(1f, 0.1f, p._transmission);     // Vanilla-Wert "ganz zu"
            float r = zu > 0.001f ? Mathf.Clamp01(closure / zu) : 0f;   // 0 = offen, 1 = zu (auch beim Auf-/Zugehen)
            float neu;
            if (mw != null)
            {
                float frei = (mw._totalArea > 0.0001f && mw._freeArea > 0f) ? Mathf.Clamp01(1f - mw._freeArea / mw._totalArea) : 0f;
                neu = ziel * (r + (1f - r) * frei);               // offen: nur der Rest der Glasflaeche daempft
            }
            else neu = ziel * r;
            neu = Mathf.Clamp01(neu);

            angepasst++; summeAlt += closure; summeNeu += neu;
            if (mw != null || sp.portalType == BaseSpatialAudioPortal.PortalType.Window) fensterGeaendert++; else tuerGeaendert++;
            return neu;
        }

        static float Ziel(BaseSpatialAudioPortal p, SpatialAudioPortal sp, MultiWindowPortal mw)
        {
            if (Ziele.TryGetValue(p.ID, out float z)) return z;
            float occ = Verdeckung(p);
            if (occ < 0f) return Merken(p.ID, -1f);
            float effekt;
            if (mw != null || sp.portalType == BaseSpatialAudioPortal.PortalType.Window) effekt = FensterHeil.Value;
            else
            {
                WorldInteractiveObject tuer = FindeTuer(sp.DoorID);
                if (tuer == null) return -1f;                      // Welt noch nicht bereit -> spaeter nochmal
                if (!sp.IsPositionIdentical(tuer.transform.position)) return Merken(p.ID, -1f);
                effekt = TuerWert(tuer);
                if (effekt < 0f) { unbekannteTuer++; return Merken(p.ID, -1f); }
            }
            return Merken(p.ID, Mathf.Clamp01(effekt / Mathf.Max(occ, 0.05f)));
        }

        static float Merken(short id, float z) { Ziele[id] = z; return z; }

        // Genau die Mittelung aus dem Propagation-Job (nur ein Raum draussen -> Wert des Innenraums).
        static float Verdeckung(BaseSpatialAudioPortal p)
        {
            var f = p.FrontRoom; var b = p.BackRoom;
            if (f == null || b == null) return -1f;
            bool fo = f.Type.IsOutdoor(), bo = b.Type.IsOutdoor();
            if (fo != bo) return fo ? b.WallOcclusion : f.WallOcclusion;
            return (f.WallOcclusion + b.WallOcclusion) * 0.5f;
        }

        static WorldInteractiveObject FindeTuer(string id)
        {
            if (string.IsNullOrEmpty(id) || !Singleton<GameWorld>.Instantiated) return null;
            var w = Singleton<GameWorld>.Instance.World;       // FindDoor() wuerde bei Fehlschlag ins Log schreiben
            if (w == null || w._interactiveObjectsDictionary == null) return null;
            w._interactiveObjectsDictionary.TryGetValue(id, out var d);
            return d;
        }

        // Zielwert einer Tuer nach Material des groessten BallisticCollider; -1 = unbekannt (-> Vanilla).
        static float TuerWert(WorldInteractiveObject d)
        {
            int key = d.GetInstanceID();
            if (TuerZiele.TryGetValue(key, out float w)) return w;
            w = -1f;
            BallisticCollider best = null; float groesse = -1f;
            foreach (var bc in d.GetComponentsInChildren<BallisticCollider>(true))
            {
                var col = bc.GetComponent<Collider>();
                if (col == null) continue;
                float g = col.bounds.size.sqrMagnitude;
                if (g > groesse) { groesse = g; best = bc; }
            }
            if (best != null)
                switch (best.TypeOfMaterial)
                {
                    case MaterialType.WoodThin: w = HolzDuenn.Value; break;
                    case MaterialType.WoodThick: w = HolzDick.Value; break;
                    case MaterialType.MetalThin: w = MetallDuenn.Value; break;
                    case MaterialType.MetalThick: case MaterialType.MetalNoDecal: case MaterialType.GarbageMetal: w = MetallDick.Value; break;
                }
            if (w < 0f)
            {
                string n = d.name.ToLowerInvariant();
                if (n.Contains("steel")) w = MetallDick.Value;
                else if (n.Contains("metal")) w = MetallDuenn.Value;
            }
            TuerZiele[key] = w;
            return w;
        }

        // ---- fuer MaterialCache und die Wandmessung ----

        internal static bool IstTuer(Collider col)
        {
            if (!Aktiv.Value) return false;
            try { return col.GetComponentInParent<Door>() != null; }
            catch (Exception e) { Statistik.Fehler(e); return false; }
        }

        // Grunddaempfung S dieser Tuer (-ln(1 - Zielwert)); -1 = unbekannt -> Wert der Gruppe "Tuer".
        internal static float TuerS(int colliderId)
        {
            if (TuerS_Cache.TryGetValue(colliderId, out float s)) return s;
            s = -1f;
            try
            {
                var door = (Resources.InstanceIDToObject(colliderId) as Collider)?.GetComponentInParent<Door>();
                if (door != null)
                {
                    float z = TuerWert(door);
                    if (z > 0f) s = -Mathf.Log(1f - Mathf.Min(z, 0.95f));
                }
            }
            catch (Exception e) { Statistik.Fehler(e); }
            TuerS_Cache[colliderId] = s;
            return s;
        }
    }
}
