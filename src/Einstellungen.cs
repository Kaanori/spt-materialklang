using BepInEx.Configuration;

namespace Materialklang
{
    // Alle Einstellungen (F12-Menue) werden hier angelegt - in einer Methode, damit der
    // Offline-Test sie ohne Spiel aufrufen kann.
    internal static class Einstellungen
    {
        /// <summary>Wie viele Werte beim Start aus den alten (deutschen) Namen uebernommen wurden.</summary>
        public static int Migriert;

        public static void Anlegen(ConfigFile c)
        {
            // Vor allen Binds merken, welche Eintraege die cfg schon unter den neuen
            // (englischen) Namen hat - die werden nie ueberschrieben.
            var vorher = Einstellungsumzug.Vorhandene(c);

            Plugin.Aktiv = c.Bind("1. General", "Enabled", true, "Material-dependent attenuation on/off (off = vanilla).");
            Plugin.Gesamtstaerke = c.Bind("1. General", "Overall strength", 1.0f,
                new ConfigDescription("Multiplies all material values.", new AcceptableValueRange<float>(0f, 3f)));
            Plugin.FreiwegSchwelle = c.Bind("1. General", "Clear-path threshold", -1f,
                new ConfigDescription("Up to which occlusion a path counts as clear (vanilla 0.5). -1 = game value. Smaller = even glass/thin wood attenuate slightly. Applies live. Ignored while Physics Mode is on (it has its own threshold).",
                    new AcceptableValueRange<float>(-1f, 1f)));
            Plugin.Diagnose = c.Bind("1. General", "Diagnostic log", true,
                "Writes a summary to the BepInEx log every 30 s (material hits, vanilla vs. new values).");
            Stetigkeit.Binden(c);

            foreach (var (g, name, k, s) in Plugin.Startwerte)
            {
                string sek = $"2. {(int)g + 1:00} {name}";
                Plugin.Dicke[(int)g] = c.Bind(sek, "Attenuation per meter", k,
                    new ConfigDescription("How strongly each meter of this material attenuates sound.", new AcceptableValueRange<float>(0f, 20f)));
                Plugin.Oberflaeche[(int)g] = c.Bind(sek, "Base attenuation", s,
                    new ConfigDescription("Fixed share per pass-through, even for very thin parts.", new AcceptableValueRange<float>(0f, 5f)));
            }

            Physik.Binden(c);
            BotHoeren.Binden(c);
            SebAkustik.Binden(c);
            Erweitert.Binden(c);
            HallZonen.Binden(c);
            TuerenFenster.Binden(c);
            WandZwischenRaeumen.Binden(c);
            KlangLog.Binden(c);
            KartenExport.Binden(c);   // 0.8.4, ebenfalls Abschnitt 11
            Wald.Binden(c);
            Fernschuss.Binden(c);   // 0.8.4, Abschnitt 13
            Wege.Binden(c);         // 0.8.4 Stufe 1, Abschnitt 14

            // Einmalig: Werte aus den alten deutschen Namen uebernehmen.
            Migriert = Einstellungsumzug.Uebernehmen(c, vorher);
        }
    }
}
