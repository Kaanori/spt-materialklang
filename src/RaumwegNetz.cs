using System;
using System.Collections.Generic;
using System.Diagnostics;

namespace Materialklang
{
    // ------------------------------------------------------------------------------------------
    // 0.8.5 Leistung (Interchange, grosses Bot-Gefecht 08.11.2026: SpatialAudioSystem.LateUpdate 26-28 ms je Bild, ruhig 0,1 ms).
    // Ursache: Raumweg.Rechnen lief je Klang nach jedem PropagationJob ueber ALLE Routen des Raumpaars (Log: 22404 Kandidaten
    // in 140 Rechnungen = 160 je Rechnung, bis 350) und rechnete fuer jedes Portal jeder Route die Absorptionsflaeche der Raeume
    // neu - darin fuer JEDE Oeffnung des Raums GetPortalSize (Collider.size, transform, localScale: drei Unity-Aufrufe),
    // PortalClosureLevel, GetInstanceID und zwei Woerterbuchzugriffe. Grosse Hallen haben Dutzende Oeffnungen: je Rechnung
    // Zehntausende Unity-Aufrufe, mehrere Rechnungen im selben Bild (Schuesse, Einschlaege, Schritte vieler Bots).
    // Loesung (dieselben Formeln, dieselben Werte):
    //  - Raumnetz einmal je Raid: je Portal Flaeche, Transmission, Raeume vorn/hinten; je Raum Oberflaeche, Volumen und seine
    //    Oeffnungen (dichte Indizes statt Woerterbuecher). Je Bild einmal die Schliessung aller Portale, die offene Flaeche und
    //    die Absorptionsflaeche je Raum hoechstens einmal je Bild.
    //  - Die Routenschleife ist reine Rechnung auf diesen Feldern (allokationsfrei, Offline-Test), Spiel-Teil liest nur noch die
    //    Job-Daten ein (Raumweg.cs).
    //  - Ergebnis-Wiederverwendung: gleiche Route (Raumpaar, Richtung, Quelle und Hoerer in derselben 1-cm-Zelle, gleiche
    //    Einstellungen) mit unveraendertem Zustand aller beteiligten Portale und Raeume -> die gespeicherten Kandidaten gehen
    //    unveraendert in den RoutenMischer (der Spielteil selbst rechnet erst nach 1 m Bewegung neu).
    // ------------------------------------------------------------------------------------------
    internal abstract class RaumwegNetz
    {
        public const int MaxPortale = 16;
        public const double ZelleM = 0.01;         // Toleranz der Wiederverwendung (Quelle und Hoerer, 1 cm: < 0,05 dB)
        public const int CacheMax = 512;

        // ---- je Portal (Index wie im Job / _indexMap), statisch je Raid
        public int PortalN;
        public bool[] HatObjekt = new bool[0];      // Portal-Objekt bekannt (sonst gilt die Schliessung aus den Job-Daten)
        public float[] Voll = new float[0];         // Mathf.Lerp(1, 0.1, _transmission)
        public double[] FlaecheP = new double[0];   // |Breite * Hoehe| (GetPortalSize)
        public int[] Vorn = new int[0], Hinten = new int[0];   // Raumindex oder -1
        // je Bild: Schliessung des Spiels (PortalClosureLevel)
        public float[] Schliessung = new float[0];

        // ---- je Portal aus den Job-Daten (je Rechnung geladen)
        public RaumPortal[] JGeo = new RaumPortal[0];
        public float[] JZu = new float[0], JWand = new float[0];
        public int[] JGeoKey = new int[0];
        int[] jStempel = new int[0];
        int rechnung = 1;

        // ---- je Raum (dichter Index), statisch je Raid
        public int RaumN;
        public short[] RaumId = new short[0];
        public bool[] Draussen = new bool[0];
        public int[] PStart = new int[0], PAnz = new int[0], PListe = new int[0];
        double[] oberflaeche = new double[0], volumen = new double[0];
        bool[] gemessen = new bool[0];
        // je Bild
        double[] offen = new double[0], aWert = new double[0];
        float[] aAlpha = new float[0];
        int[] offenBild = new int[0], aBild = new int[0];
        int bild = int.MinValue;

        // Diagnose
        public int DRechnungen, DTreffer, DFehl, DRouten, DRaumA, DRaumAGespart;

        /// <summary>Oberflaeche (m2) und Volumen (m3) eines Innenraums messen (Spiel: Collider-Boxen). Einmal je Raid und Raum.</summary>
        protected abstract void Messen(int raum, out double flaeche, out double vol);

        // ---------------------------------------------------------------- Aufbau

        public void PortaleAnlegen(int n)
        {
            PortalN = n;
            Array.Resize(ref HatObjekt, n); Array.Resize(ref Voll, n); Array.Resize(ref FlaecheP, n);
            Array.Resize(ref Vorn, n); Array.Resize(ref Hinten, n); Array.Resize(ref Schliessung, n);
            for (int i = 0; i < n; i++) { HatObjekt[i] = false; Voll[i] = 1f; FlaecheP[i] = 0; Vorn[i] = Hinten[i] = -1; Schliessung[i] = 0f; }
            JobAnlegen(n);
            bild = int.MinValue;
            Leeren();
        }

        void JobAnlegen(int n)
        {
            if (JGeo.Length >= n) return;
            int k = Math.Max(n, 16);
            Array.Resize(ref JGeo, k); Array.Resize(ref JZu, k); Array.Resize(ref JWand, k); Array.Resize(ref JGeoKey, k); Array.Resize(ref jStempel, k);
        }

        public void RaeumeAnlegen(int n, int portalEintraege)
        {
            RaumN = n;
            Array.Resize(ref RaumId, n); Array.Resize(ref Draussen, n); Array.Resize(ref PStart, n); Array.Resize(ref PAnz, n);
            Array.Resize(ref PListe, Math.Max(portalEintraege, 1));
            Array.Resize(ref oberflaeche, n); Array.Resize(ref volumen, n); Array.Resize(ref gemessen, n);
            Array.Resize(ref offen, n); Array.Resize(ref aWert, n); Array.Resize(ref aAlpha, n); Array.Resize(ref offenBild, n); Array.Resize(ref aBild, n);
            for (int i = 0; i < n; i++) { gemessen[i] = false; offenBild[i] = aBild[i] = int.MinValue; PAnz[i] = 0; PStart[i] = 0; }
            bild = int.MinValue;
            Leeren();
        }

        /// <summary>Raum ohne eigene Oeffnungen nachtragen (Quell-/Hoererraum, der an keinem Portal haengt). Rueckgabe: Index.</summary>
        public int RaumDazu(short id, bool draussen)
        {
            int n = RaumN + 1;
            if (n > RaumId.Length)
            {
                int k = Math.Max(16, RaumId.Length * 2);
                Array.Resize(ref RaumId, k); Array.Resize(ref Draussen, k); Array.Resize(ref PStart, k); Array.Resize(ref PAnz, k);
                Array.Resize(ref oberflaeche, k); Array.Resize(ref volumen, k); Array.Resize(ref gemessen, k);
                Array.Resize(ref offen, k); Array.Resize(ref aWert, k); Array.Resize(ref aAlpha, k); Array.Resize(ref offenBild, k); Array.Resize(ref aBild, k);
            }
            int r = RaumN; RaumN = n;
            RaumId[r] = id; Draussen[r] = draussen; PStart[r] = 0; PAnz[r] = 0; gemessen[r] = false; offenBild[r] = aBild[r] = int.MinValue;
            return r;
        }

        /// <summary>Neues Bild: Werte "je Bild" verfallen (Spiel: danach Schliessung[] fuer alle Portale fuellen).</summary>
        public bool BildSetzen(int b)
        {
            if (b == bild) return false;
            bild = b;
            return true;
        }

        /// <summary>Vor dem Einlesen der Job-Daten einer Rechnung.</summary>
        public void RechnungBeginn(int portalAnzahlJob)
        {
            JobAnlegen(portalAnzahlJob);
            if (++rechnung == int.MaxValue) { rechnung = 1; Array.Clear(jStempel, 0, jStempel.Length); }
        }

        /// <summary>Portal-Objekt zum Job-Index bekannt (wie 'pi &lt; portalJeIndex.Length ? portalJeIndex[pi] : null' != null).</summary>
        public bool Obj(int pi) => pi < PortalN && HatObjekt[pi];

        public bool JobGeladen(int pi) => pi < jStempel.Length && jStempel[pi] == rechnung;

        /// <summary>Daten eines Portals aus dem Job (center/right/up/normal/halfSize, closureLevel, Wandfaktor, Geometrie-Schluessel).</summary>
        public void JobSetzen(int pi, in RaumPortal geo, float zu, float wand, int geoKey)
        {
            JGeo[pi] = geo; JZu[pi] = zu; JWand[pi] = wand; JGeoKey[pi] = geoKey; jStempel[pi] = rechnung;
        }

        // ---------------------------------------------------------------- Raeume

        static float Clamp01f(float v) => v < 0f ? 0f : (v > 1f ? 1f : v);

        /// <summary>Geschlossener Anteil eines Portals aus der Schliessung des Spiels (wie 0.8.4/0.8.5: closure / Lerp(1, 0.1, transmission)).</summary>
        public double ZuSpiel(int pi)
        {
            float voll = Voll[pi];
            return voll > 0.001f ? Clamp01f(Schliessung[pi] / voll) : 0f;
        }

        /// <summary>Offene Flaeche (m2) aller Oeffnungen eines Raums (einmal je Bild).</summary>
        public double OffeneFlaeche(int raum)
        {
            if (offenBild[raum] == bild) return offen[raum];
            double summe = 0;
            int s = PStart[raum], e = s + PAnz[raum];
            for (int i = s; i < e; i++)
            {
                int pi = PListe[i];
                summe += FlaecheP[pi] * (1.0 - ZuSpiel(pi));
            }
            offen[raum] = summe; offenBild[raum] = bild;
            return summe;
        }

        bool Gemessen(int raum)
        {
            if (!gemessen[raum])
            {
                Messen(raum, out oberflaeche[raum], out volumen[raum]);
                gemessen[raum] = true;
            }
            return oberflaeche[raum] > 0;
        }

        /// <summary>Absorptionsflaeche eines Innenraums (wie Raumweg.RaumFlaeche bis 0.8.5): -1 = im Freien/unbekannt.</summary>
        public double RaumFlaeche(int raum, float alpha, out double vol)
        {
            vol = 0;
            if (raum < 0 || Draussen[raum]) return -1;
            if (!Gemessen(raum)) return -1;
            vol = volumen[raum];
            DRaumA++;
            if (aBild[raum] == bild && aAlpha[raum] == alpha) { DRaumAGespart++; return aWert[raum]; }
            double a = RaumwegKern.Absorption(oberflaeche[raum], alpha, OffeneFlaeche(raum));
            aWert[raum] = a; aAlpha[raum] = alpha; aBild[raum] = bild;
            return a;
        }

        // Raum hinter dem Portal (wie Raumweg.RaumNach): der andere als der davor; passt keiner, der mit dem anderen
        // Drinnen/Draussen-Zustand.
        double RaumNach(int pi, ref short vorRaum, ref bool vorDraussen, float alpha, out double vol, out int nach)
        {
            vol = 0; nach = -1;
            if (!Obj(pi)) return -1;
            int f = Vorn[pi], bk = Hinten[pi];
            if (f >= 0 && RaumId[f] == vorRaum) nach = bk;
            else if (bk >= 0 && RaumId[bk] == vorRaum) nach = f;
            else if (f >= 0 && bk >= 0) nach = Draussen[f] != vorDraussen ? f : bk;
            else nach = bk >= 0 ? bk : f;
            if (nach < 0) return -1;
            vorRaum = RaumId[nach]; vorDraussen = Draussen[nach];
            return RaumFlaeche(nach, alpha, out vol);
        }

        // ---------------------------------------------------------------- Routen

        /// <summary>Eingabe einer Rechnung (Job-Richtung: A = Emitter des Raumpaars).</summary>
        public struct Eingabe
        {
            public V3 A, B;
            public bool Umgekehrt;
            public uint Paar;
            public int RaumA, RaumB;          // Raumindex oder -1
            public short RaumAId;             // ID des Raums A oder -1
            public bool RaumADraussen;
            public float Dyn, Alpha, TiefpassMin;
            public double FRef, FHoch;
            public float Fit1, Fit2;
            public int RoutenGesamt;          // Laenge der Routenliste (Schluessel)
        }

        // Routen in Job-Reihenfolge: Start in RIdx, Anzahl (-1 = ungueltig, wie 'continue' im Spielteil)
        public int[] RStart = new int[64], RN = new int[64], RIdx = new int[256];

        // Arbeitsfelder (allokationsfrei)
        readonly RaumPortal[] rp = new RaumPortal[MaxPortale];
        readonly V3[] pkt = new V3[MaxPortale], pktJob = new V3[MaxPortale];
        readonly double[] raumA = new double[MaxPortale + 1], raumV = new double[MaxPortale + 1], beug = new double[MaxPortale], umweg = new double[MaxPortale];
        readonly int[] idx = new int[MaxPortale], geo = new int[MaxPortale], raumIdx = new int[MaxPortale + 1];

        // Lauteste Route (Klang-Log)
        public RaumwegErgebnis Best;
        public int BestN, BestRoute = -1;
        public readonly int[] BestIdx = new int[MaxPortale];
        public readonly double[] BestA = new double[MaxPortale + 1], BestV = new double[MaxPortale + 1];
        public double BestOmega, BestR1;
        public readonly double[] BestBeug = new double[MaxPortale], BestUm = new double[MaxPortale], BestOffen = new double[MaxPortale], BestTauDb = new double[MaxPortale];

        /// <summary>Alle Routen [0, anzahl) rechnen und als Kandidaten in den Mischer geben (mix.Beginn() inklusive).
        /// logDetails = lauteste Route fuer das Klang-Log merken (dann ohne Wiederverwendung). routen = gerechnete Kandidaten.
        /// Rueckgabe: mindestens eine gueltige Route.</summary>
        public bool Rechne(in Eingabe ein, int anzahl, RoutenMischer mix, bool logDetails, out int routen, out bool treffer)
        {
            routen = 0; treffer = false;
            DRechnungen++;
            bool cache = !logDetails;
            ulong schluessel = 0;
            Eintrag alt = null;
            if (cache)
            {
                schluessel = Schluessel(in ein, anzahl);
                if (tabelle.TryGetValue(schluessel, out alt) && alt.Passt(in ein, anzahl, this))
                {
                    treffer = true; DTreffer++;
                    mix.Beginn();
                    for (int i = 0; i < alt.KN; i++)
                    {
                        ref var k = ref alt.K[i];
                        mix.Kandidat(k.Id, k.D0, k.D1, k.D2, k.Aus, k.Erg, alt.KPortale, k.N, k.Start);
                    }
                    routen = alt.Routen;
                    DRouten += routen;
                    return alt.Hat;
                }
                DFehl++;
                if (alt == null)
                {
                    if (tabelle.Count >= CacheMax) Leeren();
                    alt = frei.Count > 0 ? frei.Pop() : new Eintrag();
                    tabelle[schluessel] = alt;
                }
                alt.Beginn(in ein, anzahl);
            }

            float dyn = ein.Dyn, alpha = ein.Alpha;
            double fRef = ein.FRef;
            V3 a = ein.A, b = ein.B;
            V3 quelle = ein.Umgekehrt ? b : a, hoerer = ein.Umgekehrt ? a : b;
            bool hat = false; double bestDb = 0;
            BestRoute = -1;
            mix.Beginn();
            float fit1 = ein.Fit1, fit2 = ein.Fit2;

            for (int r = 0; r < anzahl; r++)
            {
                int n = RN[r];
                if (n <= 0) continue;
                int s0 = RStart[r];
                // Portalpunkte genau wie der Job (Richtung A -> B, Austritt immer B)
                V3 eintritt = a; int m = 0;
                short vorRaum = ein.RaumAId; bool vorDraussen = ein.RaumADraussen;
                for (int i = 0; i < n; i++)
                {
                    int pi = RIdx[s0 + i];
                    var p = JGeo[pi];
                    V3 punkt = RaumwegKern.PortalPunkt(p, eintritt, b);
                    if (punkt.IstNull) continue;
                    // Zustand: offener Anteil aus der Schliessung des Spiels, geschlossener Teil mit dem Wert der Mod (Abschnitt 4)
                    float wand = JWand[pi], jz = JZu[pi];
                    double zu = Obj(pi) ? ZuSpiel(pi) : (jz > 0.001f ? 1.0 : 0.0);
                    double tlDb = zu > 1e-3 ? Math.Min(dyn, jz / zu * wand * dyn) : 0.0;
                    p.Offen = 1.0 - zu;
                    p.Tau = Math.Pow(10, -tlDb / 10);
                    rp[m] = p; pktJob[m] = punkt; idx[m] = pi; geo[m] = JGeoKey[pi];
                    // Raum hinter dem Portal (in Job-Richtung)
                    raumA[m + 1] = RaumNach(pi, ref vorRaum, ref vorDraussen, alpha, out raumV[m + 1], out raumIdx[m + 1]);
                    if (cache) alt.PortalMerken(pi, zu, jz);
                    eintritt = punkt; m++;
                }
                if (m == 0) continue;
                raumA[0] = RaumFlaeche(ein.RaumA, alpha, out raumV[0]);
                raumA[m] = RaumFlaeche(ein.RaumB, alpha, out raumV[m]);
                if (cache)
                {
                    for (int i = 1; i < m; i++) alt.RaumMerken(raumIdx[i], raumA[i]);
                    alt.RaumMerken(ein.RaumA, raumA[0]); alt.RaumMerken(ein.RaumB, raumA[m]);
                }
                // Stabile Routen-ID aus der Portalfolge, ueber die Geometrie der Portale (doppelt eingetragene Portale = eine Route)
                ulong id = RoutenMischer.Id(ein.Paar, geo, m);
                // in echte Richtung (Quelle -> Hoerer) bringen
                if (ein.Umgekehrt) Umdrehen(m);
                for (int i = 0; i < m; i++) pkt[i] = pktJob[i];
                var e = RaumwegKern.Route(quelle, hoerer, rp, pkt, m, raumA, fRef, ein.FHoch, dyn, beug, umweg, raumV);
                routen++;
                if (!e.Gueltig) continue;
                // Baender (Bezug / 1 kHz / 4 kHz): Beugung physikalisch je Band, geschlossene Tueren/Fenster mit ihrem Tiefpass
                float lpTuer = Abbildung(Clamp01f((float)(e.DbTuer / dyn)), ein.TiefpassMin);
                double t0 = WegeKern.MehrDb(lpTuer, fRef);
                double x1 = Math.Max(e.DbMitte - e.Db, WegeKern.MehrDb(lpTuer, fit1) - t0);
                double x2 = Math.Max(e.DbHoch - e.Db, WegeKern.MehrDb(lpTuer, fit2) - t0);
                double d0 = e.Db, d1 = Math.Min(dyn, e.Db + Math.Max(0, x1)), d2 = Math.Min(dyn, e.Db + Math.Max(0, x2));
                mix.Kandidat(id, d0, d1, d2, pkt[m - 1], e, idx, m);
                if (cache) alt.Kandidat(id, d0, d1, d2, pkt[m - 1], in e, idx, m);
                if (!hat || e.Db < bestDb)
                {
                    hat = true; bestDb = e.Db; Best = e; BestN = m; BestRoute = r;
                    if (logDetails)
                    {
                        for (int i = 0; i < m; i++) { BestIdx[i] = idx[i]; BestBeug[i] = beug[i]; BestUm[i] = umweg[i]; BestOffen[i] = rp[i].Offen; BestTauDb[i] = -10 * Math.Log10(Math.Max(rp[i].Tau, 1e-12)); }
                        for (int i = 0; i <= m; i++) { BestA[i] = raumA[i]; BestV[i] = raumV[i]; }
                        BestOmega = RaumwegKern.Raumwinkel(rp[0], quelle) / (4 * Math.PI); BestR1 = V3.Abstand(quelle, pkt[0]);
                    }
                }
            }
            DRouten += routen;
            if (cache) { alt.Hat = hat; alt.Routen = routen; alt.Fertig = true; }
            return hat;
        }

        void Umdrehen(int m)
        {
            Array.Reverse(rp, 0, m); Array.Reverse(pktJob, 0, m); Array.Reverse(idx, 0, m);
            Array.Reverse(raumA, 0, m + 1); Array.Reverse(raumV, 0, m + 1); Array.Reverse(geo, 0, m);
        }

        /// <summary>Wie Physik.Abbildung (Mathf.Pow = (float)Math.Pow): 22 kHz * (min/22 kHz)^w.</summary>
        public static float Abbildung(float w, float tiefpassMin) => 22000f * (float)Math.Pow(tiefpassMin / 22000f, Clamp01f(w));

        // ---------------------------------------------------------------- Wiederverwendung

        sealed class Eintrag
        {
            public struct Kand { public ulong Id; public double D0, D1, D2; public V3 Aus; public RaumwegErgebnis Erg; public int Start, N; }
            public Kand[] K = new Kand[16];
            public int KN;
            public int[] KPortale = new int[64];
            int kp;
            // beteiligte Portale (Zustand) und Raeume (Absorptionsflaeche)
            public int[] PPi = new int[32]; public double[] PZu = new double[32]; public float[] PJob = new float[32];
            public int PN;
            public int[] RIdx = new int[16]; public double[] RA = new double[16];
            public int RN;
            public bool Hat, Fertig;
            public int Routen;
            // Schluesselfelder
            uint paar; bool umg; long ax, ay, az, bx, by, bz; int anzahl, gesamt, raumA, raumB;
            float dyn, alpha, tpMin, fit1, fit2; double fRef, fHoch;

            public void Beginn(in Eingabe e, int anz)
            {
                KN = kp = PN = RN = 0; Hat = Fertig = false; Routen = 0;
                paar = e.Paar; umg = e.Umgekehrt; anzahl = anz; gesamt = e.RoutenGesamt; raumA = e.RaumA; raumB = e.RaumB;
                ax = Z(e.A.X); ay = Z(e.A.Y); az = Z(e.A.Z); bx = Z(e.B.X); by = Z(e.B.Y); bz = Z(e.B.Z);
                dyn = e.Dyn; alpha = e.Alpha; tpMin = e.TiefpassMin; fit1 = e.Fit1; fit2 = e.Fit2; fRef = e.FRef; fHoch = e.FHoch;
            }

            public void Kandidat(ulong id, double d0, double d1, double d2, V3 aus, in RaumwegErgebnis erg, int[] portale, int n)
            {
                if (KN == K.Length) Array.Resize(ref K, K.Length * 2);
                if (kp + n > KPortale.Length) Array.Resize(ref KPortale, Math.Max(KPortale.Length * 2, kp + n));
                Array.Copy(portale, 0, KPortale, kp, n);
                K[KN++] = new Kand { Id = id, D0 = d0, D1 = d1, D2 = d2, Aus = aus, Erg = erg, Start = kp, N = n };
                kp += n;
            }

            public void PortalMerken(int pi, double zu, float job)
            {
                for (int i = 0; i < PN; i++) if (PPi[i] == pi) return;
                if (PN == PPi.Length) { Array.Resize(ref PPi, PN * 2); Array.Resize(ref PZu, PN * 2); Array.Resize(ref PJob, PN * 2); }
                PPi[PN] = pi; PZu[PN] = zu; PJob[PN] = job; PN++;
            }

            public void RaumMerken(int raum, double a)
            {
                if (raum < 0) return;
                for (int i = 0; i < RN; i++) if (RIdx[i] == raum) return;
                if (RN == RIdx.Length) { Array.Resize(ref RIdx, RN * 2); Array.Resize(ref RA, RN * 2); }
                RIdx[RN] = raum; RA[RN] = a; RN++;
            }

            public bool Passt(in Eingabe e, int anz, RaumwegNetz netz)
            {
                if (!Fertig || paar != e.Paar || umg != e.Umgekehrt || anzahl != anz || gesamt != e.RoutenGesamt || raumA != e.RaumA || raumB != e.RaumB) return false;
                if (ax != Z(e.A.X) || ay != Z(e.A.Y) || az != Z(e.A.Z) || bx != Z(e.B.X) || by != Z(e.B.Y) || bz != Z(e.B.Z)) return false;
                if (dyn != e.Dyn || alpha != e.Alpha || tpMin != e.TiefpassMin || fit1 != e.Fit1 || fit2 != e.Fit2 || fRef != e.FRef || fHoch != e.FHoch) return false;
                // Zustand aller beteiligten Portale (Spiel und Job) und die Absorptionsflaeche aller beteiligten Raeume
                for (int i = 0; i < PN; i++)
                {
                    int pi = PPi[i];
                    if (!netz.JobGeladen(pi) || netz.JZu[pi] != PJob[i]) return false;
                    double zu = netz.Obj(pi) ? netz.ZuSpiel(pi) : (netz.JZu[pi] > 0.001f ? 1.0 : 0.0);
                    if (zu != PZu[i]) return false;
                }
                for (int i = 0; i < RN; i++)
                    if (netz.RaumFlaeche(RIdx[i], alpha, out _) != RA[i]) return false;
                return true;
            }
        }

        static long Z(double x) => (long)Math.Floor(x / ZelleM);

        readonly Dictionary<ulong, Eintrag> tabelle = new Dictionary<ulong, Eintrag>(CacheMax);
        readonly Stack<Eintrag> frei = new Stack<Eintrag>();

        public int CacheAnzahl => tabelle.Count;

        public void Leeren()
        {
            foreach (var kv in tabelle) frei.Push(kv.Value);
            tabelle.Clear();
        }

        static ulong Schluessel(in Eingabe e, int anzahl)
        {
            ulong h = 14695981039346656037UL;
            unchecked
            {
                void M(ulong v) { h = (h ^ v) * 1099511628211UL; }
                M(e.Paar); M(e.Umgekehrt ? 1UL : 0UL); M((ulong)anzahl); M((ulong)e.RoutenGesamt);
                M((ulong)Z(e.A.X)); M((ulong)Z(e.A.Y)); M((ulong)Z(e.A.Z));
                M((ulong)Z(e.B.X)); M((ulong)Z(e.B.Y)); M((ulong)Z(e.B.Z));
            }
            return h;
        }
    }

    /// <summary>0.8.5: Laufzeitmessung je Teil (Hauptfaden). Summe und groesster Wert je Bild, Zaehler je 30 s.</summary>
    internal sealed class Laufzeit
    {
        public readonly string Name;
        long summe, bildSumme, bildMax, anzahl;
        int bild = int.MinValue, bilder;
        public Laufzeit(string name) { Name = name; }
        public static long Jetzt => Stopwatch.GetTimestamp();
        public static double Ms(long ticks) => ticks * 1000.0 / Stopwatch.Frequency;

        /// <summary>Zeit seit 'start' diesem Teil im Bild 'b' zuschlagen.</summary>
        public void Dazu(long start, int b)
        {
            long dt = Stopwatch.GetTimestamp() - start;
            if (b != bild) { Abschliessen(); bild = b; bilder++; }
            bildSumme += dt; summe += dt; anzahl++;
        }

        void Abschliessen() { if (bildSumme > bildMax) bildMax = bildSumme; bildSumme = 0; }

        /// <summary>Was in diesem Bild schon verbraucht ist (ms).</summary>
        public double ImBild(int b) => b == bild ? Ms(bildSumme) : 0;

        public bool Leer => anzahl == 0;

        /// <summary>"name x ms in n calls / k frames, max y ms/frame" und zuruecksetzen.</summary>
        public string Text()
        {
            Abschliessen();
            var inv = System.Globalization.CultureInfo.InvariantCulture;
            string s = Name + " " + Ms(summe).ToString("F1", inv) + " ms in " + anzahl + " calls / " + bilder + " frames (max " + Ms(bildMax).ToString("F2", inv) + " ms per frame)";
            summe = bildMax = anzahl = 0; bilder = 0; bild = int.MinValue;
            return s;
        }
    }
}
