using System;
using CodexNetworking.FixedPoint;

namespace CodexNetworking.Serialization
{
    /// <summary>
    /// Pure functions that map values into bounded integers for <see cref="BitWriter"/> and back.
    /// Float helpers are for state-sync games (float simulation, presentation); fixed-point helpers
    /// are exact integer operations for input-sync games. Values outside the declared range clamp.
    /// </summary>
    public static class Quantize
    {
        /// <summary>Bits needed to store [min, max] at the given step size.</summary>
        public static int BitsForPrecision(float min, float max, float precision)
        {
            if (precision <= 0 || max <= min)
                throw new ArgumentOutOfRangeException(nameof(precision));
            double steps = Math.Ceiling((max - min) / (double)precision);
            if (steps >= uint.MaxValue)
                return 32;
            return Math.Max(1, BitWriter.BitsRequired((uint)steps));
        }

        /// <summary>Maps a float in [min, max] onto [0, 2^bits - 1], rounding to nearest.</summary>
        public static uint FloatToBits(float value, float min, float max, int bits)
        {
            CheckBits(bits);
            if (value <= min)
                return 0;
            uint maxInteger = MaxInteger(bits);
            if (value >= max)
                return maxInteger;
            double normalized = (value - min) / ((double)max - min);
            return (uint)Math.Round(normalized * maxInteger);
        }

        public static float BitsToFloat(uint quantized, float min, float max, int bits)
        {
            CheckBits(bits);
            uint maxInteger = MaxInteger(bits);
            if (quantized >= maxInteger)
                return max;
            return (float)(min + quantized / (double)maxInteger * ((double)max - min));
        }

        /// <summary>
        /// Maps a fixed-point value in [min, max] onto an integer keeping <paramref name="fractionBits"/>
        /// of its 16 fraction bits (0..16). Exact integer arithmetic; the result needs
        /// <see cref="FixedBits"/> bits.
        /// </summary>
        public static uint FixedToBits(Fix64 value, Fix64 min, Fix64 max, int fractionBits)
        {
            CheckFractionBits(fractionBits);
            if (value.Raw <= min.Raw)
                return 0;
            long span = (max.Raw - min.Raw) >> (Fix64.FractionBits - fractionBits);
            if (value.Raw >= max.Raw)
                return (uint)span;
            return (uint)((value.Raw - min.Raw) >> (Fix64.FractionBits - fractionBits));
        }

        public static Fix64 BitsToFixed(uint quantized, Fix64 min, int fractionBits)
        {
            CheckFractionBits(fractionBits);
            return Fix64.FromRaw(min.Raw + ((long)quantized << (Fix64.FractionBits - fractionBits)));
        }

        /// <summary>Bits needed by <see cref="FixedToBits"/> for the range and fraction bits.</summary>
        public static int FixedBits(Fix64 min, Fix64 max, int fractionBits)
        {
            CheckFractionBits(fractionBits);
            if (max.Raw <= min.Raw)
                throw new ArgumentException("max must exceed min");
            long span = (max.Raw - min.Raw) >> (Fix64.FractionBits - fractionBits);
            if (span > uint.MaxValue)
                throw new ArgumentException("range too wide for 32 bits at this precision");
            return Math.Max(1, BitWriter.BitsRequired((uint)span));
        }

        /// <summary>A unit-length float vector component in [-1, 1].</summary>
        public static uint UnitToBits(float value, int bits) => FloatToBits(value, -1f, 1f, bits);

        public static float BitsToUnit(uint quantized, int bits) => BitsToFloat(quantized, -1f, 1f, bits);

        /// <summary>
        /// Smallest-three quaternion packing: the index of the largest component (2 bits) and the other
        /// three in [-1/sqrt2, 1/sqrt2] at <paramref name="bitsPerComponent"/> bits each (at most 20).
        /// The sign is normalized so the dropped component is positive. Input need not be normalized.
        /// </summary>
        public static ulong QuaternionToBits(float x, float y, float z, float w, int bitsPerComponent)
        {
            if (bitsPerComponent < 2 || bitsPerComponent > 20)
                throw new ArgumentOutOfRangeException(nameof(bitsPerComponent));
            float length = (float)Math.Sqrt(x * x + y * y + z * z + w * w);
            if (length > 0)
            {
                x /= length;
                y /= length;
                z /= length;
                w /= length;
            }
            else
            {
                w = 1f;
            }
            int largest = 0;
            float largestAbs = Math.Abs(x);
            if (Math.Abs(y) > largestAbs) { largest = 1; largestAbs = Math.Abs(y); }
            if (Math.Abs(z) > largestAbs) { largest = 2; largestAbs = Math.Abs(z); }
            if (Math.Abs(w) > largestAbs) { largest = 3; }
            float sign = Component(largest, x, y, z, w) < 0 ? -1f : 1f;
            const float limit = 0.70710678f;
            ulong packed = (ulong)largest;
            int shift = 2;
            for (int i = 0; i < 4; i++)
            {
                if (i == largest)
                    continue;
                uint q = FloatToBits(Component(i, x, y, z, w) * sign, -limit, limit, bitsPerComponent);
                packed |= (ulong)q << shift;
                shift += bitsPerComponent;
            }
            return packed;
        }

        public static void BitsToQuaternion(ulong packed, int bitsPerComponent, out float x, out float y, out float z, out float w)
        {
            if (bitsPerComponent < 2 || bitsPerComponent > 20)
                throw new ArgumentOutOfRangeException(nameof(bitsPerComponent));
            const float limit = 0.70710678f;
            int largest = (int)(packed & 3);
            ulong mask = (1UL << bitsPerComponent) - 1;
            int shift = 2;
            float a = BitsToFloat((uint)((packed >> shift) & mask), -limit, limit, bitsPerComponent);
            shift += bitsPerComponent;
            float b = BitsToFloat((uint)((packed >> shift) & mask), -limit, limit, bitsPerComponent);
            shift += bitsPerComponent;
            float c = BitsToFloat((uint)((packed >> shift) & mask), -limit, limit, bitsPerComponent);
            float d = (float)Math.Sqrt(Math.Max(0f, 1f - a * a - b * b - c * c));
            switch (largest)
            {
                case 0: x = d; y = a; z = b; w = c; break;
                case 1: x = a; y = d; z = b; w = c; break;
                case 2: x = a; y = b; z = d; w = c; break;
                default: x = a; y = b; z = c; w = d; break;
            }
        }

        /// <summary>Total bits of a packed quaternion.</summary>
        public static int QuaternionBits(int bitsPerComponent) => 2 + 3 * bitsPerComponent;

        private static float Component(int index, float x, float y, float z, float w)
        {
            switch (index)
            {
                case 0: return x;
                case 1: return y;
                case 2: return z;
                default: return w;
            }
        }

        private static uint MaxInteger(int bits) => bits == 32 ? uint.MaxValue : (1u << bits) - 1;

        private static void CheckBits(int bits)
        {
            if (bits < 1 || bits > 32)
                throw new ArgumentOutOfRangeException(nameof(bits));
        }

        private static void CheckFractionBits(int fractionBits)
        {
            if (fractionBits < 0 || fractionBits > Fix64.FractionBits)
                throw new ArgumentOutOfRangeException(nameof(fractionBits));
        }
    }
}
