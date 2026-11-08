using System;

namespace Materialklang
{
    // 0.8.3 (Review-Befund 5) - Schutz gegen eine geaenderte Sound-Engine-Backport-Fassung. Reine Rechnung (im Offline-Test).
    // SebTeil (SebAkustik.cs) baut einen SEB.ShotPlaybackContext mit 11 Parametern und liest private Felder (_blend, _source,
    // Bank.BlendOptions.Values, FilterTargets.LowPass ...). Aendert SEB davon etwas, wirft das JIT eine MissingMethod-/
    // MissingFieldException - bis 0.8.2 VOR dem try im Prefix von TryStartShot, der Schuss blieb stumm.
    internal static class SebSchutz
    {
        /// <summary>Gebaut und geprueft gegen SEB 1.0.x (1.0.3). Andere Fassungen: SEB-Teile aus.</summary>
        public static bool VersionPasst(Version v) => v != null && v.Major == 1 && v.Minor == 0;

        /// <summary>Fehler, die eine geaenderte SEB-Fassung bedeuten (fehlendes Feld/Konstruktor/Typ) -> SEB-Teile abschalten.
        /// Alles andere (z. B. NullReference) wird nur gezaehlt.</summary>
        public static bool Strukturfehler(Exception ex) =>
            ex is MissingMemberException || ex is TypeLoadException || ex is InvalidProgramException || ex is BadImageFormatException;
    }
}
