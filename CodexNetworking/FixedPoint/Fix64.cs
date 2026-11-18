using System;
using System.Globalization;
using System.Runtime.CompilerServices;

namespace CodexNetworking.FixedPoint
{
    /// <summary>
    /// Signed Q48.16 fixed-point number: 48 integer bits, 16 fraction bits, resolution 1/65536.
    /// Every operation is integer arithmetic, so results are bit-identical on every runtime and CPU.
    /// Intended for simulation state of input-synchronized games (lockstep, rollback); games that
    /// use server-authoritative state sync may keep hardware floats.
    ///
    /// Range and overflow: values span about ±1.4e14. Multiplication uses a 64-bit intermediate and
    /// wraps when the real product exceeds about 2.1e9 (for example 46341 × 46341), so squared
    /// lengths of vectors longer than ~46000 units overflow. Keep world coordinates in the thousands
    /// or scale them. Arithmetic is unchecked by design; a wrapped value is a bug in the caller.
    ///
    /// Float conversions exist for authoring constants, tooling and presentation only. Converting a
    /// runtime float into simulation state reintroduces non-determinism.
    /// </summary>
    public readonly struct Fix64 : IEquatable<Fix64>, IComparable<Fix64>
    {
        public const int FractionBits = 16;
        public const long OneRaw = 1L << FractionBits;
        private const long FractionMask = OneRaw - 1;
        private const long HalfRaw = OneRaw >> 1;
        // |raw| below this can be shifted left by 16 without overflow.
        private const long ShiftSafeLimit = 1L << 47;

        public readonly long Raw;

        private Fix64(long raw) => Raw = raw;

        public static readonly Fix64 Zero = new Fix64(0);
        public static readonly Fix64 One = new Fix64(OneRaw);
        public static readonly Fix64 MinusOne = new Fix64(-OneRaw);
        public static readonly Fix64 Half = new Fix64(HalfRaw);
        public static readonly Fix64 Two = new Fix64(OneRaw * 2);
        public static readonly Fix64 Epsilon = new Fix64(1);
        public static readonly Fix64 MaxValue = new Fix64(long.MaxValue);
        public static readonly Fix64 MinValue = new Fix64(long.MinValue);
        public static readonly Fix64 Pi = new Fix64(205887);
        public static readonly Fix64 TwoPi = new Fix64(411775);
        public static readonly Fix64 PiOver2 = new Fix64(102944);
        public static readonly Fix64 PiOver4 = new Fix64(51472);
        public static readonly Fix64 Deg2Rad = new Fix64(1144);
        public static readonly Fix64 Rad2Deg = new Fix64(3754936);

        // ---- construction and conversion -----------------------------------------------------------

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static Fix64 FromRaw(long raw) => new Fix64(raw);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static Fix64 FromInt(int value) => new Fix64((long)value << FractionBits);

        /// <summary>Wraps when |value| is 2^47 or more.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static Fix64 FromLong(long value) => new Fix64(value << FractionBits);

        /// <summary>Numerator / denominator as an exact rational, floored to the nearest raw step.</summary>
        public static Fix64 FromFraction(long numerator, long denominator) => FromLong(numerator) / FromLong(denominator);

        /// <summary>Authoring and tooling only: rounds to the nearest representable value.</summary>
        public static Fix64 FromDouble(double value) => new Fix64((long)Math.Round(value * OneRaw));

        /// <summary>Authoring and tooling only.</summary>
        public static Fix64 FromFloat(float value) => FromDouble(value);

        /// <summary>Presentation only.</summary>
        public double ToDouble() => Raw / (double)OneRaw;

        /// <summary>Presentation only.</summary>
        public float ToFloat() => (float)(Raw / (double)OneRaw);

        /// <summary>Integer part, rounded toward negative infinity.</summary>
        public int ToInt() => (int)(Raw >> FractionBits);

        public long ToLong() => Raw >> FractionBits;

        public static implicit operator Fix64(int value) => FromInt(value);
        public static explicit operator int(Fix64 value) => value.ToInt();
        public static explicit operator long(Fix64 value) => value.ToLong();
        public static explicit operator float(Fix64 value) => value.ToFloat();
        public static explicit operator double(Fix64 value) => value.ToDouble();

        // ---- arithmetic ---------------------------------------------------------------------------

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static Fix64 operator +(Fix64 a, Fix64 b) => new Fix64(a.Raw + b.Raw);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static Fix64 operator -(Fix64 a, Fix64 b) => new Fix64(a.Raw - b.Raw);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static Fix64 operator -(Fix64 a) => new Fix64(-a.Raw);

        /// <summary>Floors toward negative infinity. Wraps when the real product exceeds ~2.1e9.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static Fix64 operator *(Fix64 a, Fix64 b) => new Fix64((a.Raw * b.Raw) >> FractionBits);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static Fix64 operator *(Fix64 a, int b) => new Fix64(a.Raw * b);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static Fix64 operator *(int a, Fix64 b) => new Fix64(b.Raw * a);

        /// <summary>Truncates toward zero like integer division. Throws DivideByZeroException on zero.</summary>
        public static Fix64 operator /(Fix64 a, Fix64 b)
        {
            long ar = a.Raw;
            long br = b.Raw;
            if (ar > -ShiftSafeLimit && ar < ShiftSafeLimit)
                return new Fix64((ar << FractionBits) / br);
            // Large numerator: split into integer quotient and a remainder that is safe to shift.
            long quotient = ar / br;
            long remainder = ar % br;
            if (br > -ShiftSafeLimit && br < ShiftSafeLimit)
                return new Fix64((quotient << FractionBits) + (remainder << FractionBits) / br);
            return new Fix64((quotient << FractionBits) + remainder / (br >> FractionBits));
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static Fix64 operator /(Fix64 a, int b) => new Fix64(a.Raw / b);

        /// <summary>Remainder with the sign of the dividend, like C# %.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static Fix64 operator %(Fix64 a, Fix64 b) => new Fix64(a.Raw % b.Raw);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static Fix64 operator <<(Fix64 a, int bits) => new Fix64(a.Raw << bits);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static Fix64 operator >>(Fix64 a, int bits) => new Fix64(a.Raw >> bits);

        // ---- comparison ---------------------------------------------------------------------------

        public static bool operator ==(Fix64 a, Fix64 b) => a.Raw == b.Raw;
        public static bool operator !=(Fix64 a, Fix64 b) => a.Raw != b.Raw;
        public static bool operator <(Fix64 a, Fix64 b) => a.Raw < b.Raw;
        public static bool operator >(Fix64 a, Fix64 b) => a.Raw > b.Raw;
        public static bool operator <=(Fix64 a, Fix64 b) => a.Raw <= b.Raw;
        public static bool operator >=(Fix64 a, Fix64 b) => a.Raw >= b.Raw;

        public bool Equals(Fix64 other) => Raw == other.Raw;
        public override bool Equals(object obj) => obj is Fix64 other && Raw == other.Raw;
        public override int GetHashCode() => Raw.GetHashCode();
        public int CompareTo(Fix64 other) => Raw.CompareTo(other.Raw);

        /// <summary>Debug formatting; allocates.</summary>
        public override string ToString() => ToDouble().ToString("0.#####", CultureInfo.InvariantCulture);

        // ---- elementary functions -----------------------------------------------------------------

        public static Fix64 Abs(Fix64 v) => new Fix64(v.Raw < 0 ? -v.Raw : v.Raw);

        public static int Sign(Fix64 v) => v.Raw < 0 ? -1 : v.Raw > 0 ? 1 : 0;

        public static Fix64 Floor(Fix64 v) => new Fix64(v.Raw & ~FractionMask);

        public static Fix64 Ceiling(Fix64 v) => new Fix64((v.Raw + FractionMask) & ~FractionMask);

        /// <summary>Rounds half up (toward positive infinity).</summary>
        public static Fix64 Round(Fix64 v) => new Fix64((v.Raw + HalfRaw) & ~FractionMask);

        public static Fix64 Min(Fix64 a, Fix64 b) => a.Raw < b.Raw ? a : b;

        public static Fix64 Max(Fix64 a, Fix64 b) => a.Raw > b.Raw ? a : b;

        public static Fix64 Clamp(Fix64 v, Fix64 min, Fix64 max) => v.Raw < min.Raw ? min : v.Raw > max.Raw ? max : v;

        public static Fix64 Clamp01(Fix64 v) => Clamp(v, Zero, One);

        /// <summary>a + (b - a) * t, t unclamped.</summary>
        public static Fix64 Lerp(Fix64 a, Fix64 b, Fix64 t) => a + (b - a) * t;

        /// <summary>Exact floor of the square root at 16-bit resolution. Throws for negative input.</summary>
        public static Fix64 Sqrt(Fix64 v)
        {
            if (v.Raw < 0)
                throw new ArgumentOutOfRangeException(nameof(v), "square root of a negative value");
            if (v.Raw < ShiftSafeLimit)
                return new Fix64((long)IntegerSqrt((ulong)v.Raw << FractionBits));
            return new Fix64((long)IntegerSqrt((ulong)v.Raw) << (FractionBits / 2));
        }

        /// <summary>Floor of the integer square root, digit by digit.</summary>
        public static ulong IntegerSqrt(ulong n)
        {
            ulong result = 0;
            ulong bit = 1UL << 62;
            while (bit > n)
                bit >>= 2;
            while (bit != 0)
            {
                if (n >= result + bit)
                {
                    n -= result + bit;
                    result = (result >> 1) + bit;
                }
                else
                {
                    result >>= 1;
                }
                bit >>= 2;
            }
            return result;
        }

        // ---- trigonometry (CORDIC, Q32 internal, 24 iterations) -----------------------------------

        private const int CordicIterations = 24;
        private const long CordicGain32 = 2608131496L; // 0.6072529350088812 in Q32
        private const long Pi32 = 13493037705L;
        private const long TwoPi32 = 26986075409L;
        private const long PiOver2_32 = 6746518852L;
        private static readonly long[] Atan32 =
        {
            3373259426L, 1991351318L, 1052175346L, 534100635L, 268086748L, 134174063L, 67103403L, 33553749L,
            16777131L, 8388597L, 4194303L, 2097152L, 1048576L, 524288L, 262144L, 131072L,
            65536L, 32768L, 16384L, 8192L, 4096L, 2048L, 1024L, 512L,
        };

        /// <summary>
        /// Sine and cosine of an angle in radians. The angle is reduced modulo 2π first; very large
        /// angles lose accuracy through the rounding of the 2π constant (about 6e-7 rad per turn).
        /// Absolute error is within a few raw steps (~5e-5).
        /// </summary>
        public static void SinCos(Fix64 angle, out Fix64 sin, out Fix64 cos)
        {
            long reduced = angle.Raw % TwoPi.Raw;
            if (reduced < 0)
                reduced += TwoPi.Raw;
            long z = reduced << FractionBits; // Q32 in [0, 2π)

            bool negateCos = false;
            if (z > PiOver2_32 && z < Pi32 + PiOver2_32)
            {
                z = Pi32 - z; // second and third quadrant: sin(π - z) = sin z, cos(π - z) = -cos z
                negateCos = true;
            }
            else if (z >= Pi32 + PiOver2_32)
            {
                z -= TwoPi32; // fourth quadrant: [-π/2, 0)
            }

            long x = CordicGain32;
            long y = 0;
            for (int i = 0; i < CordicIterations; i++)
            {
                long dx = x >> i;
                long dy = y >> i;
                if (z >= 0)
                {
                    x -= dy;
                    y += dx;
                    z -= Atan32[i];
                }
                else
                {
                    x += dy;
                    y -= dx;
                    z += Atan32[i];
                }
            }
            if (negateCos)
                x = -x;
            sin = new Fix64(RoundShift16(y));
            cos = new Fix64(RoundShift16(x));
        }

        public static Fix64 Sin(Fix64 angle)
        {
            SinCos(angle, out Fix64 sin, out _);
            return sin;
        }

        public static Fix64 Cos(Fix64 angle)
        {
            SinCos(angle, out _, out Fix64 cos);
            return cos;
        }

        /// <summary>sin / cos; grows without bound near ±π/2 and divides by zero exactly there.</summary>
        public static Fix64 Tan(Fix64 angle)
        {
            SinCos(angle, out Fix64 sin, out Fix64 cos);
            return sin / cos;
        }

        /// <summary>Angle of (x, y) in (-π, π]; zero for the origin. Absolute error within ~5e-5.</summary>
        public static Fix64 Atan2(Fix64 y, Fix64 x)
        {
            long xr = x.Raw;
            long yr = y.Raw;
            if (xr == 0 && yr == 0)
                return Zero;

            // Vectoring CORDIC is scale invariant: bring the larger magnitude into [2^59, 2^60) so the
            // shifts keep precision for tiny inputs and nothing overflows for huge ones.
            long ax = xr < 0 ? -xr : xr;
            long ay = yr < 0 ? -yr : yr;
            long largest = ax > ay ? ax : ay;
            int shift = 59 - Log2(largest);
            if (shift > 0)
            {
                xr <<= shift;
                yr <<= shift;
            }
            else if (shift < 0)
            {
                xr >>= -shift;
                yr >>= -shift;
            }

            long z = 0;
            if (xr < 0)
            {
                // Rotate by π so the iteration starts in the right half-plane.
                z = yr >= 0 ? Pi32 : -Pi32;
                xr = -xr;
                yr = -yr;
            }
            for (int i = 0; i < CordicIterations; i++)
            {
                long dx = xr >> i;
                long dy = yr >> i;
                if (yr > 0)
                {
                    xr += dy;
                    yr -= dx;
                    z += Atan32[i];
                }
                else
                {
                    xr -= dy;
                    yr += dx;
                    z -= Atan32[i];
                }
            }
            return new Fix64(RoundShift16(z));
        }

        public static Fix64 Atan(Fix64 v) => Atan2(v, One);

        /// <summary>Clamps the input to [-1, 1].</summary>
        public static Fix64 Asin(Fix64 v)
        {
            v = Clamp(v, MinusOne, One);
            return Atan2(v, Sqrt(One - v * v));
        }

        /// <summary>Clamps the input to [-1, 1].</summary>
        public static Fix64 Acos(Fix64 v)
        {
            v = Clamp(v, MinusOne, One);
            return Atan2(Sqrt(One - v * v), v);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static long RoundShift16(long value) => (value + HalfRaw) >> FractionBits;

        /// <summary>Floor of log2 for a positive value.</summary>
        private static int Log2(long value)
        {
            int result = 0;
            if (value >= 1L << 32) { value >>= 32; result += 32; }
            if (value >= 1L << 16) { value >>= 16; result += 16; }
            if (value >= 1L << 8) { value >>= 8; result += 8; }
            if (value >= 1L << 4) { value >>= 4; result += 4; }
            if (value >= 1L << 2) { value >>= 2; result += 2; }
            if (value >= 1L << 1) { result += 1; }
            return result;
        }
    }
}
