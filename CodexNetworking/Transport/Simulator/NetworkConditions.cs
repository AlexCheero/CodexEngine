namespace CodexNetworking.Transport.Simulator
{
    /// <summary>Link conditions applied to every datagram that enters a <see cref="SimulatedNetwork"/>.</summary>
    public struct NetworkConditions
    {
        /// <summary>One-way base delay in seconds.</summary>
        public double Latency;

        /// <summary>Extra delay in seconds, uniform in [0, Jitter]. Jitter larger than the send interval reorders packets.</summary>
        public double Jitter;

        /// <summary>Probability in [0, 1] that a datagram is dropped.</summary>
        public double LossChance;

        /// <summary>Probability in [0, 1] that a delivered datagram is delivered twice.</summary>
        public double DuplicateChance;

        public static NetworkConditions Perfect => default;

        public static NetworkConditions Create(double latency, double jitter, double lossChance, double duplicateChance = 0)
        {
            return new NetworkConditions
            {
                Latency = latency,
                Jitter = jitter,
                LossChance = lossChance,
                DuplicateChance = duplicateChance,
            };
        }
    }
}
