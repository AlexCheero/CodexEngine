using System;

namespace CodexNetworking.Transport
{
    /// <summary>
    /// Transport tuning. Both ends of a connection must agree on <see cref="ProtocolId"/> and
    /// <see cref="Mtu"/>; the other values are local policy. All times are in seconds.
    /// </summary>
    public sealed class NetConfig
    {
        /// <summary>Identifies the game and protocol version. Packets with another id are dropped unparsed.</summary>
        public uint ProtocolId = 0x434E4554; // "CNET"

        /// <summary>Largest datagram ever sent. 1200 is safe on practically every internet path.</summary>
        public int Mtu = 1200;

        /// <summary>Host only: number of client slots. Client indices are 0..MaxClients-1.</summary>
        public int MaxClients = 64;

        /// <summary>Host only: clients that have been challenged but not yet answered.</summary>
        public int MaxPendingConnections = 32;

        /// <summary>Connected peer is dropped after this long without receiving any packet.</summary>
        public double ConnectionTimeout = 10.0;

        /// <summary>A KeepAlive is sent when nothing else has been sent for this long.</summary>
        public double KeepAliveInterval = 0.1;

        /// <summary>Client only: ConnectRequest / ChallengeResponse resend interval.</summary>
        public double HandshakeRetryInterval = 0.25;

        /// <summary>Client only: give up connecting after this long.</summary>
        public double ConnectTimeout = 5.0;

        /// <summary>Host only: forget a challenged client that never answers after this long.</summary>
        public double PendingConnectionTimeout = 5.0;

        /// <summary>Sent and received packet history per connection. Power of two.</summary>
        public int AckBufferSize = 256;

        /// <summary>A packet is presumed lost once a packet this many sequences newer has been acked.</summary>
        public int LossReorderWindow = 32;

        /// <summary>A packet is presumed lost once unacked for this long, whatever else was acked.</summary>
        public double LossTimeout = 1.0;

        /// <summary>Largest message is MaxFragments * FragmentPayloadSize. At most 64.</summary>
        public int MaxFragments = 16;

        /// <summary>Fragmented messages being reassembled concurrently per connection. Power of two.</summary>
        public int ReassemblyBufferSize = 4;

        /// <summary>Disconnect packets are unreliable, so they are sent this many times.</summary>
        public int DisconnectRedundancy = 8;

        /// <summary>Smoothed RTT above this marks the connection congested (see <see cref="CongestionControl"/>).</summary>
        public double CongestionRttThreshold = 0.25;

        /// <summary>Initial time the RTT must stay below the threshold before the congested flag clears.</summary>
        public double CongestionRecoverySeconds = 10.0;

        /// <summary>Bounds for the adaptive recovery time.</summary>
        public double CongestionMinRecoverySeconds = 1.0;
        public double CongestionMaxRecoverySeconds = 60.0;

        /// <summary>Calm time in the good state after which the recovery time halves; a relapse sooner than this doubles it.</summary>
        public double CongestionGoodPeriodSeconds = 10.0;

        /// <summary>
        /// Packet protection. Both ends must agree. With a mode other than None every session packet
        /// is encrypted and authenticated (16 bytes of tag per datagram), handshake packets are
        /// authenticated, and a peer without the key never connects. See docs/TRANSPORT_PROTOCOL.md.
        /// </summary>
        public Crypto.EncryptionMode Encryption = Crypto.EncryptionMode.None;

        /// <summary>PreSharedKey mode: 32 bytes known to the host and every client.</summary>
        public byte[] PreSharedKey;

        /// <summary>ConnectToken mode, host only: the 32-byte key the backend seals connect tokens with.</summary>
        public byte[] TokenKey;

        /// <summary>ConnectToken mode, host only: accepted tokens remembered to refuse a replay while they are valid.</summary>
        public int TokenReplayMemory = 256;

        /// <summary>ConnectToken mode, host only: the clock token expiries are checked against (Unix seconds).</summary>
        public Func<long> UnixTime = () => DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        public bool Encrypted => Encryption != Crypto.EncryptionMode.None;

        /// <summary>Bytes every session packet grows by: the authentication tag, or 0 without encryption.</summary>
        public int TagSize => Encrypted ? Crypto.SessionKeys.TagSize : 0;

        /// <summary>Largest payload that travels in a single datagram.</summary>
        public int MaxPayloadSize => Mtu - Protocol.SessionHeaderSize - TagSize;

        /// <summary>Payload bytes carried by each fragment.</summary>
        public int FragmentPayloadSize => Mtu - Protocol.FragmentHeaderSize - TagSize;

        /// <summary>Largest payload accepted by Send.</summary>
        public int MaxMessageSize => FragmentPayloadSize * MaxFragments;

        /// <summary>Size of the connect request datagram: larger in token mode to carry the token.</summary>
        public int ConnectRequestSize => Encryption == Crypto.EncryptionMode.ConnectToken ? Protocol.ConnectRequestWithTokenSize : Protocol.ConnectRequestSize;

        public void Validate()
        {
            if (Encryption == Crypto.EncryptionMode.PreSharedKey && (PreSharedKey == null || PreSharedKey.Length != Crypto.SessionKeys.KeySize))
                throw new ArgumentException("PreSharedKey mode needs a 32-byte PreSharedKey");
            if (Encryption == Crypto.EncryptionMode.ConnectToken && TokenKey != null && TokenKey.Length != Crypto.SessionKeys.KeySize)
                throw new ArgumentException("TokenKey must be 32 bytes");
            if (TokenReplayMemory < 1)
                throw new ArgumentOutOfRangeException(nameof(TokenReplayMemory));
            if (UnixTime == null)
                throw new ArgumentNullException(nameof(UnixTime));
            if (Mtu < 128 || Mtu > 65507)
                throw new ArgumentOutOfRangeException(nameof(Mtu));
            if (MaxClients < 1 || MaxClients > 4096)
                throw new ArgumentOutOfRangeException(nameof(MaxClients));
            if (MaxPendingConnections < 1)
                throw new ArgumentOutOfRangeException(nameof(MaxPendingConnections));
            if (!IsPowerOfTwo(AckBufferSize) || AckBufferSize < 64)
                throw new ArgumentOutOfRangeException(nameof(AckBufferSize));
            if (!IsPowerOfTwo(ReassemblyBufferSize))
                throw new ArgumentOutOfRangeException(nameof(ReassemblyBufferSize));
            if (MaxFragments < 2 || MaxFragments > Protocol.MaxFragmentCount)
                throw new ArgumentOutOfRangeException(nameof(MaxFragments));
            if (LossReorderWindow < 1 || LossReorderWindow >= AckBufferSize)
                throw new ArgumentOutOfRangeException(nameof(LossReorderWindow));
            if (DisconnectRedundancy < 1)
                throw new ArgumentOutOfRangeException(nameof(DisconnectRedundancy));
            if (ConnectionTimeout <= 0 || KeepAliveInterval <= 0 || HandshakeRetryInterval <= 0 ||
                ConnectTimeout <= 0 || PendingConnectionTimeout <= 0 || LossTimeout <= 0)
                throw new ArgumentOutOfRangeException("timeouts must be positive");
            if (CongestionRttThreshold <= 0 || CongestionGoodPeriodSeconds <= 0 ||
                CongestionMinRecoverySeconds <= 0 || CongestionRecoverySeconds < CongestionMinRecoverySeconds ||
                CongestionMaxRecoverySeconds < CongestionRecoverySeconds)
                throw new ArgumentOutOfRangeException("congestion settings must be positive and min <= initial <= max");
        }

        private static bool IsPowerOfTwo(int value) => value > 0 && (value & (value - 1)) == 0;
    }
}
