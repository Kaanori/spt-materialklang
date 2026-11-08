using Audio.SpatialSystem.Data;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;

namespace Materialklang
{
    // Erweiterte Strahl-Regler: Startstrahlen und Abpraller der Spiel-Verdeckung.
    // Die Rechner legen ihre Puffer beim Raid-Start an -> Aenderungen wirken ab dem naechsten Raid.
    internal static class Erweitert
    {
        internal static ConfigEntry<float> TransmissionStrahlen, ReflexionStrahlen, VerlustJeAbprall;
        internal static ConfigEntry<int> Abpraller;

        internal static void Binden(ConfigFile c)
        {
            const string s = "10. Advanced (rays)";
            TransmissionStrahlen = c.Bind(s, "Transmission rays x", 1f,
                new ConfigDescription("Factor on the starting rays for walls/obstacles (game: 12 at high quality). More = less jumping, finds narrow gaps, costs a little more CPU. Takes effect from the next raid.",
                    new AcceptableValueRange<float>(0.5f, 4f)));
            ReflexionStrahlen = c.Bind(s, "Reflection rays x", 1f,
                new ConfigDescription("Factor on the starting rays for detours via walls (game: 24). Takes effect from the next raid.",
                    new AcceptableValueRange<float>(0.5f, 4f)));
            Abpraller = c.Bind(s, "Max. bounces", 0,
                new ConfigDescription("0 = game value (3). More helps little: after 6 bounces ~12 % energy is left. Takes effect from the next raid.",
                    new AcceptableValueRange<int>(0, 8)));
            VerlustJeAbprall = c.Bind(s, "Energy kept per bounce", 0f,
                new ConfigDescription("0 = game value (0.7). Higher = livelier around corners. Takes effect from the next raid.",
                    new AcceptableValueRange<float>(0f, 0.95f)));
        }

        [HarmonyPatch(typeof(TransmissionSettings), nameof(TransmissionSettings.GetRaysCountByQuality))]
        static class TStrahlen
        {
            static void Postfix(ref int __result)
            {
                if (Plugin.Aktiv.Value) __result = Mathf.Max(1, Mathf.RoundToInt(__result * TransmissionStrahlen.Value));
            }
        }

        [HarmonyPatch(typeof(ReflectionSettings), nameof(ReflectionSettings.GetRaysCountByQuality))]
        static class RStrahlen
        {
            static void Postfix(ref int __result)
            {
                if (Plugin.Aktiv.Value) __result = Mathf.Max(1, Mathf.RoundToInt(__result * ReflexionStrahlen.Value));
            }
        }

        [HarmonyPatch(typeof(ReflectionSettings), nameof(ReflectionSettings.Apply))]
        static class RWerte
        {
            static void Postfix(ReflectionSettings __instance)
            {
                if (!Plugin.Aktiv.Value) return;
                if (Abpraller.Value > 0) __instance.maxReflections = Abpraller.Value;
                if (VerlustJeAbprall.Value > 0f) __instance.energyLossFactorPerReflection = VerlustJeAbprall.Value;
            }
        }
    }
}
