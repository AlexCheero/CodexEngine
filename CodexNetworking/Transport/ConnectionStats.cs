namespace CodexNetworking.Transport
{
    /// <summary>Lifetime counters of one connection. Bytes count whole datagrams including headers.</summary>
    public struct ConnectionStats
    {
        /// <summary>Exponentially smoothed round-trip time in seconds; 0 until the first ack.</summary>
        public double SmoothedRtt;
        public long PacketsSent;
        public long PacketsReceived;
        public long PacketsAcked;
        public long PacketsLost;
        public long BytesSent;
        public long BytesReceived;

        /// <summary>Bytes sent during the second before the last Update, headers included.</summary>
        public double SendRateBytesPerSecond;

        /// <summary>Bytes received during the second before the last Update, headers included.</summary>
        public double ReceiveRateBytesPerSecond;

        /// <summary>Lost / (acked + lost) over the connection lifetime.</summary>
        public double PacketLossFraction
        {
            get
            {
                long resolved = PacketsAcked + PacketsLost;
                return resolved == 0 ? 0.0 : PacketsLost / (double)resolved;
            }
        }
    }
}
