using System.Runtime.CompilerServices;

namespace CodexNetworking.Transport
{
    /// <summary>16-bit sequence number arithmetic with wrap-around.</summary>
    public static class SequenceMath
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool GreaterThan(ushort a, ushort b)
        {
            return (a > b && a - b <= 32768) || (a < b && b - a > 32768);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool LessThan(ushort a, ushort b) => GreaterThan(b, a);

        /// <summary>Signed distance a - b in [-32768, 32767].</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static int Diff(ushort a, ushort b) => (short)(ushort)(a - b);
    }
}
