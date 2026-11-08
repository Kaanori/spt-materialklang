// ---------------------------------------------------------------------------------------------------------------------
//  UTD-Kantenbeugung (Uniform Theory of Diffraction, Kouyoumjian & Pathak 1974), nach C# portiert aus
//  Valve Steam Audio, core/src/core/deviation.cpp, DeviationModel::utdDeviation
//  (https://github.com/ValveSoftware/steam-audio, Stand Commit 480dd64f, 25.03.2026).
//
//  Copyright 2017-2023 Valve Corporation.
//  Licensed under the Apache License, Version 2.0 (the "License"); you may not use this file except in compliance
//  with the License. You may obtain a copy of the License at http://www.apache.org/licenses/LICENSE-2.0
//  Unless required by applicable law or agreed to in writing, software distributed under the License is distributed
//  on an "AS IS" BASIS, WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
//  Siehe THIRD_PARTY_NOTICES.md im Mod-Ordner.
//
//  Aenderungen gegenueber dem Original (die Mathematik ist dieselbe, nur Parameter statt Konstanten):
//   - Keilzahl n, Einfallswinkel alpha_i und Abstandsparameter L sind Parameter (Steam Audio: n = 2, alpha_i = 0, L = 0,05 m);
//     Standardwerte = die von Steam Audio. Die Mod uebergibt L = s s' / (s + s') aus der Geometrie (Kantenpunkt des Spiels)
//     und n aus der Einstellung "Edge diffraction: wedge angle".
//   - Frequenz direkt statt Bandmitte (low + high) / 2 der Steam-Audio-Baender; Schallgeschwindigkeit als Parameter
//     (Steam Audio 340 m/s, die Mod 343 m/s wie im uebrigen Code).
//   - double statt float, eigene komplexe Arithmetik (kein System.Numerics im Spiel-Mono noetig).
//  Steam Audio zitiert dazu: N. Tsingos et al., "Modeling acoustics in virtual environments using the uniform theory of
//  diffraction", SIGGRAPH 2001.
// ---------------------------------------------------------------------------------------------------------------------
using System;

namespace Materialklang
{
    internal static class Utd
    {
        struct K
        {
            public double Re, Im;
            public K(double re, double im) { Re = re; Im = im; }
            public static K Polar(double r, double phi) => new K(r * Math.Cos(phi), r * Math.Sin(phi));
            public static K operator +(K a, K b) => new K(a.Re + b.Re, a.Im + b.Im);
            public static K operator -(K a, K b) => new K(a.Re - b.Re, a.Im - b.Im);
            public static K operator *(K a, K b) => new K(a.Re * b.Re - a.Im * b.Im, a.Re * b.Im + a.Im * b.Re);
            public static K operator *(K a, double s) => new K(a.Re * s, a.Im * s);
            public static K operator *(double s, K a) => new K(a.Re * s, a.Im * s);
            public static K operator /(K a, double s) => new K(a.Re / s, a.Im / s);
            public double Betrag => Math.Sqrt(Re * Re + Im * Im);
        }

        static double Cot(double t) => 1.0 / Math.Tan(t);
        static double NPlus(double n, double x) => x <= Math.PI * (n - 1.0) ? 0.0 : 1.0;
        static double NMinus(double n, double x)
        {
            if (x < Math.PI * (1.0 - n)) return -1.0;
            if (Math.PI * (1.0 - n) <= x && x <= Math.PI * (1.0 + n)) return 0.0;
            return 1.0;
        }
        static double A(double n, double beta, double N) { double c = Math.Cos(Math.PI * n * N - 0.5 * beta); return 2.0 * c * c; }

        // Uebergangsfunktion (Naeherung von Kawai), wie im Original
        static K F(double x)
        {
            var e = K.Polar(1.0, 0.25 * Math.PI * Math.Sqrt(x / (x + 1.4)));
            if (x < 0.8)
            {
                double t1 = Math.Sqrt(Math.PI * x);
                double t2 = 1.0 - Math.Sqrt(x) / (0.7 * Math.Sqrt(x) + 1.2);
                return e * (t1 * t2);
            }
            double t = 1.0 - 0.8 / ((x + 1.25) * (x + 1.25));
            return e * t;
        }

        static bool Endlich(double v) => !double.IsNaN(v) && !double.IsInfinity(v);

        /// <summary>
        /// |D| des UTD-Beugungskoeffizienten (Steam Audio utdDeviation) fuer die Gesamtablenkung 'winkel' (rad, Winkel zwischen
        /// einfallender und weiterlaufender Richtung an der Kante), Frequenz f. n = Keil-Aussenwinkel / pi (2 = duenne Kante),
        /// alphaI = Einfallswinkel zur Keilflaeche, l = Abstandsparameter (m).
        /// </summary>
        public static double Koeffizient(double winkel, double f, double n = 2.0, double alphaI = 0.0, double l = 0.05, double c = 340.0)
        {
            double alphaD = alphaI + Math.PI + winkel;
            double lambda = c / f;
            double k = 2.0 * Math.PI / lambda;
            var e = K.Polar(1.0, -0.25 * Math.PI);
            K d0 = e / (2.0 * n * Math.Sqrt(2.0 * Math.PI * k));

            double beta1 = alphaD - alphaI, beta2 = alphaD - alphaI, beta3 = alphaD + alphaI, beta4 = alphaD + alphaI;
            double t1 = Cot((Math.PI + beta1) / (2.0 * n)), t2 = Cot((Math.PI - beta2) / (2.0 * n));
            double t3 = Cot((Math.PI + beta3) / (2.0 * n)), t4 = Cot((Math.PI - beta4) / (2.0 * n));
            double n1 = NPlus(n, beta1), n2 = NMinus(n, beta2), n3 = NPlus(n, beta3), n4 = NMinus(n, beta4);
            double x1 = k * l * A(n, beta1, n1), x2 = k * l * A(n, beta2, n2), x3 = k * l * A(n, beta3, n3), x4 = k * l * A(n, beta4, n4);

            K D1 = t1 * F(x1), D2 = t2 * F(x2), D3 = t3 * F(x3), D4 = t4 * F(x4);

            double eps1 = beta1 - 2.0 * Math.PI * n * n1 + Math.PI;
            double eps2 = -(beta2 - 2.0 * Math.PI * n * n2 - Math.PI);
            double eps3 = beta3 - 2.0 * Math.PI * n * n3 + Math.PI;
            double eps4 = -(beta4 - 2.0 * Math.PI * n * n4 - Math.PI);

            if (!Endlich(t1)) D1 = Sonder(n, e, k, l, eps1);
            if (!Endlich(t2)) D2 = Sonder(n, e, k, l, eps2);
            if (!Endlich(t3)) D3 = Sonder(n, e, k, l, eps3);
            if (!Endlich(t4)) D4 = Sonder(n, e, k, l, eps4);

            return (d0 * (D1 + D2 + D3 + D4)).Betrag;
        }

        // Grenzfall cot -> unendlich (genau auf einer Schattengrenze), wie im Original
        static K Sonder(double n, K e, double k, double l, double eps)
            => n * e * (new K(Math.Sqrt(2.0 * Math.PI * k * l) * (eps > 0.0 ? 1 : -1), 0) - (2 * k * l * eps) * e);
    }
}
