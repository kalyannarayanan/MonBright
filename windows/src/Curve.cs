//
//  Curve.cs
//  MonBright for Windows - the brightness curve shared with the macOS build.
//
//  One slider, two mechanisms. DDC/CI bottoms out at the panel's minimum
//  backlight - VCP 0x10 = 0 means "dimmest the hardware supports", not
//  "off", which on a typical IPS panel is still 40-60 nits. Below the
//  crossover the hardware is pinned at 0 and a per-monitor overlay does
//  the rest. Same constants as macos/main.swift so both builds feel alike.
//
using System;
using System.Windows.Forms;

namespace MonBright
{
    internal static class Curve
    {
        /// <summary>Slider position where the hardware bottoms out.</summary>
        public const int Crossover = 30;

        /// <summary>Darkest software scale (1.0 = untouched, 0.25 = a quarter).</summary>
        public const double Floor = 0.25;

        /// <summary>
        /// Split a 0-100 slider position into a DDC percent and a picture scale.
        /// Continuous at Crossover: both branches give (ddc 0, scale 1.0), so
        /// there is no visible step as the slider crosses it.
        /// </summary>
        public static void Split(int slider, out int ddc, out double scale)
        {
            if (slider < 0) slider = 0;
            if (slider > 100) slider = 100;
            if (slider >= Crossover)
            {
                ddc = (int)Math.Round((slider - Crossover) * 100.0 / (100 - Crossover),
                                      MidpointRounding.AwayFromZero);
                scale = 1.0;
            }
            else
            {
                ddc = 0;
                scale = Floor + (1.0 - Floor) * slider / Crossover;
            }
        }

        /// <summary>
        /// Inverse of the hardware branch: the slider position that produces a
        /// given DDC percent. Used once to migrate 1.0.0's stored values.
        /// </summary>
        public static int SliderForDdc(int ddc)
        {
            if (ddc < 0) ddc = 0;
            if (ddc > 100) ddc = 100;
            return (int)Math.Round(Crossover + ddc * (100.0 - Crossover) / 100.0,
                                   MidpointRounding.AwayFromZero);
        }

        /// <summary>Runnable check: MonBright.exe --selftest</summary>
        public static void SelfTest()
        {
            int ddc; double scale;

            Split(0, out ddc, out scale);
            Check(ddc == 0 && Math.Abs(scale - Floor) < 1e-9, "bottom must be (0, Floor)");
            Split(Crossover, out ddc, out scale);
            Check(ddc == 0 && Math.Abs(scale - 1.0) < 1e-9, "crossover must be (0, 1.0)");
            Split(100, out ddc, out scale);
            Check(ddc == 100 && Math.Abs(scale - 1.0) < 1e-9, "top must be (100, 1.0)");

            // No seam: one step below the crossover is still DDC 0, nearly full scale.
            Split(Crossover - 1, out ddc, out scale);
            Check(ddc == 0 && scale > 0.97, "seam at crossover");

            // Perceived brightness never goes down as the slider goes up.
            double prev = -1;
            for (int s = 0; s <= 100; s++)
            {
                Split(s, out ddc, out scale);
                double perceived = scale * (ddc + 1);
                Check(perceived >= prev - 1e-9, "non-monotonic at slider " + s);
                prev = perceived;
            }

            // Migrating a raw DDC value round-trips to the same DDC (+/-1 from rounding).
            for (int d = 0; d <= 100; d += 5)
            {
                Split(SliderForDdc(d), out ddc, out scale);
                Check(Math.Abs(ddc - d) <= 1, "migration drift at " + d + ": got " + ddc);
            }

            MessageBox.Show("selftest OK - crossover " + Crossover + ", floor " + Floor,
                            "MonBright", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        private static void Check(bool ok, string message)
        {
            if (!ok) throw new InvalidOperationException("selftest FAILED: " + message);
        }
    }
}
