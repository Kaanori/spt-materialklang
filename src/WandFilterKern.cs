using System;

namespace Materialklang
{
    // ---------------------------------------------------------------------------------------------
    //  0.8.5 Wanddaempfung: Lautstaerke + 2-poliger Tiefpass im Audiofaden (OnAudioFilterRead, WandDaempfung.WandFilter).
    //  Reine Rechnung ohne Unity (Offline-Test). Warum ein eigener Filter und nicht AudioSource.volume: die Besitzer dieser
    //  Quellen schreiben volume selbst (Ambient-Ein-/Ausblendung, Lok-FadeIn, Flugzeug-Lautstaerkekurve je Bild, BTR nach Drehzahl,
    //  BetterSource.UpdateSourceVolume). Ein Faktor im Signalweg ist davon unabhaengig und wird nie ueberschrieben.
    //  Tiefpass: Butterworth 2. Ordnung (RBJ, Q = 1/sqrt 2) - |H|^2 = 1/(1 + (f/fc)^4) wie WegeKern.MehrDb (Tiefpass des Spiels).
    //  Uebergang: Lautstaerke und Grenzfrequenz (logarithmisch) laufen exponentiell zum Ziel, Zeitkonstante = Ueberblendzeit/3
    //  (95 % nach der Ueberblendzeit, Standard 300 ms); innerhalb eines Blocks wird die Lautstaerke linear interpoliert (kein Knacken).
    //  Ziel neutral und erreicht -> Durchgang ohne Rechnung.
    // ---------------------------------------------------------------------------------------------
    internal sealed class WandFilterKern
    {
        public const float FcOffen = 20000f;
        public const int MaxKanaele = 8;

        // vom Hauptfaden gesetzt
        public volatile float ZielGain = 1f, ZielFc = 22000f, Tau = 0.1f;
        public volatile bool Springen;          // naechster Block uebernimmt das Ziel sofort (erste Messung)
        public volatile float Fs = 48000f;

        // nur Audiofaden
        float g = 1f, fc = 22000f, fcKoef = -1f;
        float b0 = 1f, b1, b2, a1, a2;
        readonly float[] z1 = new float[MaxKanaele], z2 = new float[MaxKanaele];
        bool zLeer = true;
        public volatile bool Durchgang = true;   // letzter Block ohne Rechnung (fuer den Hauptfaden: Abbau erlaubt)
        public float AktuellGain => g;
        public float AktuellFc => fc;

        public static bool IstNeutral(float gain, float fc) => gain >= 0.9999f && fc >= FcOffen;

        public void Verarbeiten(float[] data, int kanaele)
        {
            if (data == null || kanaele <= 0) return;
            int n = data.Length / kanaele;
            if (n <= 0) return;
            float fs = Fs > 1000f ? Fs : 48000f;
            float zg = ZielGain, zf = ZielFc;
            if (!(zg >= 0f)) zg = 1f; if (zg > 1f) zg = 1f;
            if (!(zf > 0f)) zf = 22000f;
            float g0 = g, g1, f1;
            if (Springen) { Springen = false; g1 = zg; f1 = zf; g0 = zg; }
            else
            {
                float tau = Tau;
                float k = tau <= 1e-4f ? 1f : 1f - (float)Math.Exp(-(n / fs) / tau);
                g1 = g + (zg - g) * k;
                f1 = (float)Math.Exp(Math.Log(fc) + (Math.Log(zf) - Math.Log(fc)) * k);
                if (Math.Abs(g1 - zg) < 1e-4f) g1 = zg;
                if (Math.Abs(f1 - zf) < 0.5f) f1 = zf;
            }
            g = g1; fc = f1;
            bool filtern = f1 < FcOffen;
            if (!filtern && IstNeutral(g0, 22000f) && IstNeutral(g1, 22000f))
            {
                if (!zLeer) { Array.Clear(z1, 0, z1.Length); Array.Clear(z2, 0, z2.Length); zLeer = true; }
                Durchgang = true;
                return;
            }
            Durchgang = false;
            int ch = Math.Min(kanaele, MaxKanaele);
            if (filtern)
            {
                if (Math.Abs(f1 - fcKoef) > 0.001f * f1) Koeffizienten(f1, fs);
                zLeer = false;
            }
            else if (!zLeer) { Array.Clear(z1, 0, z1.Length); Array.Clear(z2, 0, z2.Length); zLeer = true; }
            float dg = (g1 - g0) / n;
            int i = 0;
            for (int s = 0; s < n; s++)
            {
                float gs = g0 + dg * (s + 1);
                for (int c = 0; c < kanaele; c++, i++)
                {
                    float x = data[i];
                    if (filtern && c < ch)
                    {
                        float y = b0 * x + z1[c];
                        z1[c] = b1 * x - a1 * y + z2[c];
                        z2[c] = b2 * x - a2 * y;
                        x = y;
                    }
                    data[i] = x * gs;
                }
            }
            // Denormale vermeiden (Abklingen des Filters in Stille)
            for (int c = 0; c < ch; c++)
            {
                if (Math.Abs(z1[c]) < 1e-20f) z1[c] = 0f;
                if (Math.Abs(z2[c]) < 1e-20f) z2[c] = 0f;
            }
        }

        void Koeffizienten(float f, float fs)
        {
            double fcl = Math.Max(20.0, Math.Min(f, 0.45 * fs));
            double w0 = 2 * Math.PI * fcl / fs, cw = Math.Cos(w0), al = Math.Sin(w0) / (2 * 0.70710678118654752);
            double a0 = 1 + al;
            b0 = (float)((1 - cw) / 2 / a0); b1 = (float)((1 - cw) / a0); b2 = b0;
            a1 = (float)(-2 * cw / a0); a2 = (float)((1 - al) / a0);
            fcKoef = f;
        }

        /// <summary>Betrag des Tiefpasses bei f (dB Mehrdaempfung) mit den aktuellen Koeffizienten - fuer den Test.</summary>
        public static double MehrDbDigital(double fc, double f, double fs)
        {
            var k = new WandFilterKern { Fs = (float)fs };
            k.Koeffizienten((float)fc, (float)fs);
            double w = 2 * Math.PI * f / fs;
            // H(z) = (b0 + b1 z^-1 + b2 z^-2) / (1 + a1 z^-1 + a2 z^-2)
            double cr = Math.Cos(w), sr = -Math.Sin(w), c2 = Math.Cos(2 * w), s2 = -Math.Sin(2 * w);
            double nr = k.b0 + k.b1 * cr + k.b2 * c2, ni = k.b1 * sr + k.b2 * s2;
            double dr = 1 + k.a1 * cr + k.a2 * c2, di = k.a1 * sr + k.a2 * s2;
            double h2 = (nr * nr + ni * ni) / (dr * dr + di * di);
            return -10 * Math.Log10(h2);
        }
    }
}
