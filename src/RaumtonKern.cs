using System;

namespace Materialklang
{
    // Reine Rechnung fuer "15. Ambient emitters / Indoor room tone volume (%)" (0.8.5) - ohne Unity, damit der Offline-Test sie prueft.
    internal static class RaumtonKern
    {
        internal const float MinProzent = 0f, MaxProzent = 150f;
        /// <summary>Name der SEB-Gruppe des Innen-Raumtons und ihr exponierter Lautstaerke-Parameter (live_mastermixer.bundle, Gruppe 40).</summary>
        internal const string Gruppe = "AmbientInRoomTone", Parameter = "RoomToneVolume";
        /// <summary>Wert des Parameters in allen drei Snapshots des SEB-Mixers (Fallback, falls GetFloat nichts liefert).</summary>
        internal const float SnapshotDb = 4f;

        /// <summary>Lineare Verstaerkung aus Prozent (0..150 %, ungueltig = 100 %).</summary>
        internal static float Faktor(float prozent)
        {
            if (float.IsNaN(prozent) || float.IsInfinity(prozent)) return 1f;
            return Math.Max(MinProzent, Math.Min(MaxProzent, prozent)) / 100f;
        }

        /// <summary>Pegelaenderung in dB (0 % = -80 dB wie Unitys Mixer-Untergrenze).</summary>
        internal static float Db(float prozent)
        {
            float f = Faktor(prozent);
            if (f <= 1e-4f) return -80f;
            return Math.Max(-80f, (float)(20.0 * Math.Log10(f)));
        }

        /// <summary>Mixerweg nur, wenn der Raumton wirklich in der eigenen Gruppe landet UND der Mixer den Parameter exponiert.
        /// Sonst (SPT 4.1.6: Raumton geht nach "AmbientIn", zusammen mit aller Innen-Ambience) wird die Quelle selbst skaliert.</summary>
        internal static bool MixerWeg(string gruppenName, bool parameterVorhanden) => parameterVorhanden && gruppenName == Gruppe;

        /// <summary>Lautstaerke der Quelle vor Okklusion/Fade: wie im Spiel (BaseVolume x RoomToneVolume) mal Faktor.</summary>
        internal static float Soll(float basis, float raumtonVolume, float faktor) => Math.Max(0f, basis * raumtonVolume * faktor);

        /// <summary>Was wirklich ankommt: Unity klemmt AudioSource.volume auf 1 (ueber 100 % nur bis dahin).</summary>
        internal static float Wirklich(float soll) => Math.Min(1f, Math.Max(0f, soll));
    }
}
