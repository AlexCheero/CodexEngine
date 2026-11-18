using System;

namespace CodexNetworking.FixedPoint
{
    /// <summary>Three-component <see cref="Fix64"/> vector. Squared length overflows beyond ~46000 units.</summary>
    public readonly struct FixVector3 : IEquatable<FixVector3>
    {
        public readonly Fix64 X;
        public readonly Fix64 Y;
        public readonly Fix64 Z;

        public FixVector3(Fix64 x, Fix64 y, Fix64 z)
        {
            X = x;
            Y = y;
            Z = z;
        }

        public static readonly FixVector3 Zero = new FixVector3(Fix64.Zero, Fix64.Zero, Fix64.Zero);
        public static readonly FixVector3 One = new FixVector3(Fix64.One, Fix64.One, Fix64.One);
        public static readonly FixVector3 UnitX = new FixVector3(Fix64.One, Fix64.Zero, Fix64.Zero);
        public static readonly FixVector3 UnitY = new FixVector3(Fix64.Zero, Fix64.One, Fix64.Zero);
        public static readonly FixVector3 UnitZ = new FixVector3(Fix64.Zero, Fix64.Zero, Fix64.One);

        public static FixVector3 operator +(FixVector3 a, FixVector3 b) => new FixVector3(a.X + b.X, a.Y + b.Y, a.Z + b.Z);
        public static FixVector3 operator -(FixVector3 a, FixVector3 b) => new FixVector3(a.X - b.X, a.Y - b.Y, a.Z - b.Z);
        public static FixVector3 operator -(FixVector3 a) => new FixVector3(-a.X, -a.Y, -a.Z);
        public static FixVector3 operator *(FixVector3 a, Fix64 s) => new FixVector3(a.X * s, a.Y * s, a.Z * s);
        public static FixVector3 operator *(Fix64 s, FixVector3 a) => new FixVector3(a.X * s, a.Y * s, a.Z * s);
        public static FixVector3 operator /(FixVector3 a, Fix64 s) => new FixVector3(a.X / s, a.Y / s, a.Z / s);
        public static bool operator ==(FixVector3 a, FixVector3 b) => a.X == b.X && a.Y == b.Y && a.Z == b.Z;
        public static bool operator !=(FixVector3 a, FixVector3 b) => !(a == b);

        public static Fix64 Dot(FixVector3 a, FixVector3 b) => a.X * b.X + a.Y * b.Y + a.Z * b.Z;

        public static FixVector3 Cross(FixVector3 a, FixVector3 b) =>
            new FixVector3(a.Y * b.Z - a.Z * b.Y, a.Z * b.X - a.X * b.Z, a.X * b.Y - a.Y * b.X);

        public Fix64 SqrMagnitude => X * X + Y * Y + Z * Z;

        public Fix64 Magnitude => Fix64.Sqrt(SqrMagnitude);

        /// <summary>Unit vector, or zero for a zero vector.</summary>
        public FixVector3 Normalized
        {
            get
            {
                Fix64 length = Magnitude;
                return length == Fix64.Zero ? Zero : this / length;
            }
        }

        public static Fix64 Distance(FixVector3 a, FixVector3 b) => (b - a).Magnitude;

        public static Fix64 SqrDistance(FixVector3 a, FixVector3 b) => (b - a).SqrMagnitude;

        public static FixVector3 Lerp(FixVector3 a, FixVector3 b, Fix64 t) => a + (b - a) * t;

        public static FixVector3 Min(FixVector3 a, FixVector3 b) =>
            new FixVector3(Fix64.Min(a.X, b.X), Fix64.Min(a.Y, b.Y), Fix64.Min(a.Z, b.Z));

        public static FixVector3 Max(FixVector3 a, FixVector3 b) =>
            new FixVector3(Fix64.Max(a.X, b.X), Fix64.Max(a.Y, b.Y), Fix64.Max(a.Z, b.Z));

        /// <summary>Projection of <paramref name="v"/> onto <paramref name="onto"/>; zero when onto is zero.</summary>
        public static FixVector3 Project(FixVector3 v, FixVector3 onto)
        {
            Fix64 denominator = Dot(onto, onto);
            return denominator == Fix64.Zero ? Zero : onto * (Dot(v, onto) / denominator);
        }

        public FixVector2 XZ => new FixVector2(X, Z);

        public bool Equals(FixVector3 other) => this == other;
        public override bool Equals(object obj) => obj is FixVector3 other && this == other;
        public override int GetHashCode() => HashCode.Combine(X.Raw, Y.Raw, Z.Raw);
        public override string ToString() => "(" + X + ", " + Y + ", " + Z + ")";
    }
}
