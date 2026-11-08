// ---------------------------------------------------------------
//  Einmalige Uebernahme der Einstellungen von Version 0.4.0 (deutsche
//  Sektions- und Schluesselnamen) auf die englischen Namen ab 0.5.0.
//
//  So geht es: Vor den Binds merkt sich Vorhandene() alle Eintraege, die
//  die cfg schon enthaelt. Nach den Binds geht Uebernehmen() die Tabelle
//  unten durch; fuer jeden neuen Eintrag, den es NICHT schon unter dem
//  neuen Namen gab, wird der alte Wert (BepInEx haelt unbekannte Eintraege
//  in ConfigFile.OrphanedEntries) gelesen, bei Aufzaehlungen auf den neuen
//  Mitgliedsnamen umgesetzt und in den neuen Eintrag geschrieben.
//  Die alten Eintraege werden nur gelesen. Diese Datei haengt nur an
//  BepInEx - sie wird auch vom Offline-Test verwendet.
// ---------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Reflection;
using BepInEx.Configuration;

namespace Materialklang
{
	internal static class Einstellungsumzug
	{
		/// <summary>Eine Zeile der Tabelle: alter Platz -> neuer Platz.</summary>
		internal struct Zeile
		{
			public string AltSektion, AltKey, NeuSektion, NeuKey;
		}

		internal static readonly List<Zeile> Tabelle = Baue();

		// Aufzaehlungen: alter Mitgliedsname -> neuer Mitgliedsname (je Typname).
		private static readonly Dictionary<string, Dictionary<string, string>> Werte = new Dictionary<string, Dictionary<string, string>>
		{
			["BotModus"] = new Dictionary<string, string>
			{
				["Aus"] = "Off", ["NurLeiser"] = "QuieterOnly", ["Ersetzen"] = "Replace"
			}
		};

		private static void A(List<Zeile> l, string altSek, string neuSek, params string[] paare)
		{
			for (int i = 0; i + 1 < paare.Length; i += 2)
			{
				l.Add(new Zeile { AltSektion = altSek, AltKey = paare[i], NeuSektion = neuSek, NeuKey = paare[i + 1] });
			}
		}

		private static List<Zeile> Baue()
		{
			List<Zeile> l = new List<Zeile>();

			A(l, "1. Allgemein", "1. General",
				"Aktiv", "Enabled",
				"Gesamtstaerke", "Overall strength",
				"Freiweg-Schwelle", "Clear-path threshold",
				"Diagnose-Log", "Diagnostic log");

			// 2. Materialgruppen: 14 Sektionen mit je zwei Schluesseln (Reihenfolge wie enum Gruppe).
			string[] gruppeAlt =
			{
				"Beton, Stein, Fliesen, Asphalt", "Erde, Schlamm, Gelaende", "Kies, Schotter, Schnee",
				"Dickes Metall (Container, Stahltuer)", "Duennes Metall (Auto, Wellblech)", "Dickes Holz (Wand, Balken)",
				"Duennes Holz (Brett, Kiste)", "Glas", "Weich (Pappe, Stoff, Plastik, Gummi)",
				"Gitter, Maschendraht, Gras", "Koerper, Ruestung, Helm", "Wasser",
				"Unbekannt (wie Vanilla)", "Tueren (Spielobjekt, Grundwert)"
			};
			string[] gruppeNeu =
			{
				"Concrete, Stone, Tile, Asphalt", "Soil, Mud, Terrain", "Gravel, Pebbles, Snow",
				"Thick Metal (Container, Steel Door)", "Thin Metal (Car, Corrugated Sheet)", "Thick Wood (Wall, Beam)",
				"Thin Wood (Board, Crate)", "Glass", "Soft (Cardboard, Fabric, Plastic, Rubber)",
				"Grate, Chain-link Fence, Grass", "Body, Armor, Helmet", "Water",
				"Unknown (like vanilla)", "Doors (game object, base value)"
			};
			for (int i = 0; i < gruppeAlt.Length; i++)
			{
				A(l, $"2. {i + 1:00} {gruppeAlt[i]}", $"2. {i + 1:00} {gruppeNeu[i]}",
					"Daempfung je Meter", "Attenuation per meter",
					"Grunddaempfung", "Base attenuation");
			}

			// 0. Physik-Modus: sechs feste Werte, dazu je Gruppe Dichte und Massiv-Dicke.
			A(l, "0. Physik-Modus (reale Werte)", "0. Physics Mode (real values)",
				"Aktiv", "Enabled",
				"Max. Daemmung (dB)", "Max. insulation (dB)",
				"Lautheits-Faktor", "Loudness factor",
				"Bezugsfrequenz (Hz)", "Reference frequency (Hz)",
				"Tiefpass bei voller Daemmung (Hz)", "Low-pass at full insulation (Hz)",
				"Freiweg-Schwelle", "Clear-path threshold");
			string[] physAlt = { "Beton/Stein", "Erde/Gelaende", "Kies/Schnee", "Metall dick", "Metall duenn", "Holz dick", "Holz duenn",
				"Glas", "Weich", "Gitter/Zaun/Gras", "Koerper", "Wasser", "Unbekannt", "Tuerblatt" };
			string[] physNeu = { "Concrete/Stone", "Soil/Terrain", "Gravel/Snow", "Metal thick", "Metal thin", "Wood thick", "Wood thin",
				"Glass", "Soft", "Grate/Fence/Grass", "Body", "Water", "Unknown", "Door leaf" };
			for (int i = 0; i < physAlt.Length; i++)
			{
				A(l, "0. Physik-Modus (reale Werte)", "0. Physics Mode (real values)",
					$"Dichte {i + 1:00} {physAlt[i]} (kg/m3)", $"Density {i + 1:00} {physNeu[i]} (kg/m3)",
					$"Massiv bis {i + 1:00} {physAlt[i]} (m)", $"Solid up to {i + 1:00} {physNeu[i]} (m)");
			}

			A(l, "3. Bots hoeren (SAIN)", "3. Bot Hearing (SAIN)",
				"Modus", "Mode",
				"Staerke", "Strength",
				"Exponent", "Exponent",
				"Untergrenze Schuss", "Floor: gunshot",
				"Untergrenze Schalldaempfer", "Floor: suppressed shot",
				"Untergrenze Schritte", "Floor: footsteps",
				"Untergrenze Sonstiges", "Floor: other sounds",
				"Nahbereich (m)", "Close range (m)",
				"Nah-Untergrenze", "Close-range floor",
				"Max. Dicke (m)", "Max. thickness (m)",
				"Standard-Dicke (m)", "Default thickness (m)",
				"Max. Flaechen", "Max. surfaces",
				"Rechnungen je Bild", "Calculations per frame",
				"Merkzeit (s)", "Cache time (s)",
				"Merk-Toleranz (m)", "Cache tolerance (m)");

			A(l, "4. Tueren und Fenster", "4. Doors and Windows",
				"Aktiv", "Enabled",
				"Tuer duennes Holz", "Door thin wood",
				"Tuer dickes Holz", "Door thick wood",
				"Tuer duennes Metall", "Door thin metal",
				"Tuer dickes Metall", "Door thick metal",
				"Fenster heil", "Window intact");

			A(l, "5. Waende zwischen Raeumen", "5. Walls Between Rooms",
				"Aktiv", "Enabled",
				"Max. Entfernung (m)", "Max. distance (m)",
				"Zuschlag", "Surcharge",
				"Schalen-Dicke (m)", "Shell thickness (m)");

			A(l, "6. Klangfarbe", "6. Timbre",
				"Aktiv", "Enabled",
				"Staerke", "Strength");

			A(l, "7. Waffen-Richtwirkung", "7. Weapon Directivity",
				"Aktiv", "Enabled",
				"Rueckseite (dB)", "Rear side (dB)",
				"Rueck-Tiefpass (Hz)", "Rear low-pass (Hz)",
				"Innenraum-Faktor", "Indoor factor");

			A(l, "8. Luftdaempfung", "8. Air Absorption",
				"Aktiv", "Enabled",
				"Feuchte", "Humidity",
				"Schuss-Abzug", "Shot offset");

			A(l, "9. Hall je Material", "9. Reverb by Material",
				"Aktiv", "Enabled",
				"Staerke", "Strength",
				"Max Zonen", "Max zones",
				"Nur groesste Box je Raum", "Only largest box per room",
				"Diagnose", "Diagnostics");

			A(l, "10. Erweitert (Strahlen)", "10. Advanced (rays)",
				"Durchdringungs-Strahlen x", "Transmission rays x",
				"Reflexions-Strahlen x", "Reflection rays x",
				"Max. Abpraller", "Max. bounces",
				"Restenergie je Abprall", "Energy kept per bounce");
			return l;
		}

		/// <summary>
		/// BepInEx haelt Eintraege der cfg, die (noch) zu keinem Bind gehoeren, in der
		/// privaten Eigenschaft ConfigFile.OrphanedEntries.
		/// </summary>
		internal static Dictionary<ConfigDefinition, string> Waisen(ConfigFile c)
		{
			PropertyInfo pi = typeof(ConfigFile).GetProperty("OrphanedEntries", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
			return pi?.GetValue(c, null) as Dictionary<ConfigDefinition, string>;
		}

		/// <summary>VOR den Binds aufrufen: alles, was die cfg schon enthaelt.</summary>
		public static HashSet<ConfigDefinition> Vorhandene(ConfigFile c)
		{
			Dictionary<ConfigDefinition, string> w = Waisen(c);
			return w == null ? new HashSet<ConfigDefinition>() : new HashSet<ConfigDefinition>(w.Keys);
		}

		/// <summary>Alten Wert auf den neuen Mitgliedsnamen umsetzen (nur bei umbenannten Aufzaehlungen).</summary>
		internal static string Umsetzen(Type typ, string alt)
		{
			string w = alt.Trim();
			if (Werte.TryGetValue(typ.Name, out var karte) && karte.TryGetValue(w, out var neu))
			{
				return neu;
			}
			return alt;
		}

		/// <summary>
		/// NACH den Binds aufrufen. Liefert, wie viele Einstellungen aus den alten
		/// Namen uebernommen wurden. Ueberschreibt nie einen Wert, den es unter dem
		/// neuen Namen schon gab.
		/// </summary>
		public static int Uebernehmen(ConfigFile c, HashSet<ConfigDefinition> vorher)
		{
			Dictionary<ConfigDefinition, string> w = Waisen(c);
			if (w == null)
			{
				return 0;
			}
			int anzahl = 0;
			bool alt = c.SaveOnConfigSet;
			c.SaveOnConfigSet = false;
			HashSet<ConfigDefinition> fertig = new HashSet<ConfigDefinition>();
			try
			{
				foreach (Zeile z in Tabelle)
				{
					ConfigDefinition neu = new ConfigDefinition(z.NeuSektion, z.NeuKey);
					ConfigDefinition alter = new ConfigDefinition(z.AltSektion, z.AltKey);
					if (alter.Equals(neu) || fertig.Contains(neu) || vorher.Contains(neu))
					{
						continue;
					}
					if (!w.TryGetValue(alter, out var wert) || !c.ContainsKey(neu))
					{
						continue;
					}
					ConfigEntryBase e = c[neu];
					try
					{
						e.SetSerializedValue(Umsetzen(e.SettingType, wert));
						fertig.Add(neu);
						anzahl++;
					}
					catch
					{
						// Wert nicht lesbar: neuer Eintrag behaelt seinen Standard.
					}
				}
			}
			finally
			{
				c.SaveOnConfigSet = alt;
			}
			if (anzahl > 0)
			{
				c.Save();
			}
			return anzahl;
		}
	}
}
