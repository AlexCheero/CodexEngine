using System;

namespace CodexNetworking.FixedPoint
{
    /// <summary>Two-component <see cref="Fix64"/> vector. Squared length overflows beyond ~46000 units.</summary>
    public readonly struct FixVector2 : IEquatable<FixVector2>
    {
        public readonly Fix64 X;
        public readonly Fix64 Y;

        public FixVector2(Fix64 x, Fix64 y)
        {
            X = x;
            Y = y;
        }

        public static readonly FixVector2 Zero = new FixVector2(Fix64.Zero, Fix64.Zero);
        public static readonly FixVector2 One = new FixVector2(Fix64.One, Fix64.One);
        public static readonly FixVector2 UnitX = new FixVector2(Fix64.One, Fix64.Zero);
        public static readonly FixVector2 UnitY = new FixVector2(Fix64.Zero, Fix64.One);

        public static FixVector2 operator +(FixVector2 a, FixVector2 b) => new FixVector2(a.X + b.X, a.Y + b.Y);
        public static FixVector2 operator -(FixVector2 a, FixVector2 b) => new FixVector2(a.X - b.X, a.Y - b.Y);
        public static FixVector2 operator -(FixVector2 a) => new FixVector2(-a.X, -a.Y);
        public static FixVector2 operator *(FixVector2 a, Fix64 s) => new FixVector2(a.X * s, a.Y * s);
        public static FixVector2 operator *(Fix64 s, FixVector2 a) => new FixVector2(a.X * s, a.Y * s);
        public static FixVector2 operator /(FixVector2 a, Fix64 s) => new FixVector2(a.X / s, a.Y / s);
        public static bool operator ==(FixVector2 a, FixVector2 b) => a.X == b.X && a.Y == b.Y;
        public static bool operator !=(FixVector2 a, FixVector2 b) => !(a == b);

        public static Fix64 Dot(FixVector2 a, FixVector2 b) => a.X * b.X + a.Y * b.Y;

        /// <summary>Z component of the 3D cross product; positive when b is counter-clockwise from a.</summary>
        public static Fix64 Cross(FixVector2 a, FixVector2 b) => a.X * b.Y - a.Y * b.X;

        public Fix64 SqrMagnitude => X * X + Y * Y;

        public Fix64 Magnitude => Fix64.Sqrt(SqrMagnitude);

        /// <summary>Unit vector, or zero for a zero vector.</summary>
        public FixVector2 Normalized
        {
            get
            {
                Fix64 length = Magnitude;
                return length == Fix64.Zero ? Zero : this / length;
            }
        }

        /// <summary>Rotated counter-clockwise by 90 degrees.</summary>
        public FixVector2 Perpendicular => new FixVector2(-Y, X);

        public static Fix64 Distance(FixVector2 a, FixVector2 b) => (b - a).Magnitude;

        public static Fix64 SqrDistance(FixVector2 a, FixVector2 b) => (b - a).SqrMagnitude;

        public static FixVector2 Lerp(FixVector2 a, FixVector2 b, Fix64 t) => a + (b - a) * t;

        public static FixVector2 Min(FixVector2 a, FixVector2 b) => new FixVector2(Fix64.Min(a.X, b.X), Fix64.Min(a.Y, b.Y));

        public static FixVector2 Max(FixVector2 a, FixVector2 b) => new FixVector2(Fix64.Max(a.X, b.X), Fix64.Max(a.Y, b.Y));

        /// <summary>Unit vector at the given angle from +X, counter-clockwise.</summary>
        public static FixVector2 FromAngle(Fix64 radians)
        {
            Fix64.SinCos(radians, out Fix64 sin, out Fix64 cos);
            return new FixVector2(cos, sin);
        }

        /// <summary>Angle from +X in (-π, π].</summary>
        public Fix64 Angle => Fix64.Atan2(Y, X);

        public FixVector2 Rotate(Fix64 radians)
        {
            Fix64.SinCos(radians, out Fix64 sin, out Fix64 cos);
            return new FixVector2(X * cos - Y * sin, X * sin + Y * cos);
        }

        public bool Equals(FixVector2 other) => this == other;
        public override bool Equals(object obj) => obj is FixVector2 other && this == other;
        public override int GetHashCode() => HashCode.Combine(X.Raw, Y.Raw);
        public override string ToString() => "(" + X + ", " + Y + ")";
    }
}
