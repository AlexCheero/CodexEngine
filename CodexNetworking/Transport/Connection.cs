using System;
using CodexNetworking.Transport.Crypto;

namespace CodexNetworking.Transport
{
    internal enum ConnectionState : byte
    {
        Disconnected = 0,
        Connected = 1,
    }

    /// <summary>
    /// Established-session state shared by a host's client slots and the client endpoint: salts,
    /// acks, fragment reassembly, keep-alive timing and, in encrypted modes, the directional keys
    /// and packet counters. Handshake state lives in the host and client.
    /// </summary>
    internal sealed class Connection
    {
        public ConnectionState State;
        public Address Remote;
        public ulong ClientSalt;
        public ulong ServerSalt;
        public uint SessionId;
        public double LastReceiveTime;
        public double LastSendTime;
        public bool Encrypted;
        public readonly PacketAcks Acks;
        public readonly FragmentAssembler Fragments;
        public readonly CongestionControl Congestion;
        private readonly byte[] _sendKey = new byte[SessionKeys.KeySize];
        private readonly byte[] _receiveKey = new byte[SessionKeys.KeySize];
        // 64-bit packet counters; the wire sequence is their low 16 bits (PacketAcks counts in step).
        private ulong _sendCounter;
        private ulong _receiveHighest;
        private bool _anyReceived;

        public Connection(NetConfig config)
        {
            Acks = new PacketAcks(config.AckBufferSize, config.LossTimeout, config.LossReorderWindow);
            Fragments = new FragmentAssembler(config.ReassemblyBufferSize, config.MaxFragments, config.FragmentPayloadSize);
            Congestion = new CongestionControl(config.CongestionRttThreshold, config.CongestionRecoverySeconds,
                config.CongestionMinRecoverySeconds, config.CongestionMaxRecoverySeconds, config.CongestionGoodPeriodSeconds);
        }

        /// <summary>The key packets from the remote side are opened with (client-to-server on a host).</summary>
        public ReadOnlySpan<byte> ReceiveKey => _receiveKey;

        /// <summary>Per-update bookkeeping: loss detection, rate sampling, congestion state.</summary>
        public void Update(double now, NetListener listener, int index)
        {
            Acks.Update(now, listener, index);
            Congestion.Update(now, Acks.Stats.SmoothedRtt);
        }

        /// <summary>
        /// Starts the session. With a non-empty <paramref name="secret"/> the directional keys are
        /// derived from it and the salts, and every packet from here on is sealed.
        /// </summary>
        public void Begin(in Address remote, ulong clientSalt, ulong serverSalt, double now, ReadOnlySpan<byte> secret, bool isHost)
        {
            State = ConnectionState.Connected;
            Remote = remote;
            ClientSalt = clientSalt;
            ServerSalt = serverSalt;
            SessionId = Protocol.DeriveSessionId(clientSalt, serverSalt);
            LastReceiveTime = now;
            LastSendTime = now;
            Acks.Reset();
            Fragments.Reset();
            Congestion.Reset(now);
            _sendCounter = 0;
            _receiveHighest = 0;
            _anyReceived = false;
            Encrypted = secret.Length > 0;
            if (Encrypted)
            {
                if (isHost)
                    SessionKeys.Derive(secret, clientSalt, serverSalt, _receiveKey, _sendKey);
                else
                    SessionKeys.Derive(secret, clientSalt, serverSalt, _sendKey, _receiveKey);
            }
        }

        public void End()
        {
            State = ConnectionState.Disconnected;
            Remote = Address.None;
            Array.Clear(_sendKey, 0, _sendKey.Length);
            Array.Clear(_receiveKey, 0, _receiveKey.Length);
        }

        /// <summary>
        /// Sends a payload as one datagram or as fragments. False when not connected or the payload is
        /// empty or larger than <see cref="NetConfig.MaxMessageSize"/>.
        /// </summary>
        public bool TrySendPayload(IUdpSocket socket, byte[] scratch, NetConfig config, double now,
            ReadOnlySpan<byte> payload, NetListener listener, int index, out ushort sequence)
        {
            sequence = 0;
            if (State != ConnectionState.Connected || payload.Length == 0 || payload.Length > config.MaxMessageSize)
                return false;

            if (payload.Length <= config.MaxPayloadSize)
            {
                int total = Protocol.SessionHeaderSize + payload.Length + config.TagSize;
                sequence = NextSequence(now, total, true, listener, index, out ulong counter);
                int headerSize = WriteHeader(scratch, config, PacketType.Payload, sequence, 0, 0);
                payload.CopyTo(new Span<byte>(scratch, headerSize, payload.Length));
                Transmit(socket, scratch, headerSize, payload.Length, 0, counter, now);
                return true;
            }

            int fragmentPayload = config.FragmentPayloadSize;
            int count = (payload.Length + fragmentPayload - 1) / fragmentPayload;
            if (count > config.MaxFragments)
                return false;
            sequence = NextSequence(now, (Protocol.FragmentHeaderSize + config.TagSize) * count + payload.Length, true, listener, index, out ulong fragmentCounter);
            for (int i = 0; i < count; i++)
            {
                int offset = i * fragmentPayload;
                int length = Math.Min(fragmentPayload, payload.Length - offset);
                int headerSize = WriteHeader(scratch, config, PacketType.Fragment, sequence, (byte)i, (byte)count);
                payload.Slice(offset, length).CopyTo(new Span<byte>(scratch, headerSize, length));
                Transmit(socket, scratch, headerSize, length, (byte)i, fragmentCounter, now);
            }
            return true;
        }

        public void SendKeepAlive(IUdpSocket socket, byte[] scratch, NetConfig config, double now, NetListener listener, int index)
        {
            ushort sequence = NextSequence(now, Protocol.SessionHeaderSize + config.TagSize, false, listener, index, out ulong counter);
            int size = WriteHeader(scratch, config, PacketType.KeepAlive, sequence, 0, 0);
            Transmit(socket, scratch, size, 0, 0, counter, now);
        }

        public void SendDisconnect(IUdpSocket socket, byte[] scratch, NetConfig config, double now, NetListener listener, int index)
        {
            for (int i = 0; i < config.DisconnectRedundancy; i++)
            {
                ushort sequence = NextSequence(now, Protocol.SessionHeaderSize + config.TagSize, false, listener, index, out ulong counter);
                int size = WriteHeader(scratch, config, PacketType.Disconnect, sequence, 0, 0);
                Transmit(socket, scratch, size, 0, 0, counter, now);
            }
        }

        /// <summary>
        /// Handles a session packet whose prefix and session id were already verified; the datagram
        /// is the whole received buffer, decrypted in place when the session is encrypted. Returns
        /// false when the packet is a Disconnect; the caller then ends the connection.
        /// </summary>
        public bool HandleSessionPacket(in SessionHeader header, Span<byte> datagram, int headerSize,
            double now, NetListener listener, int index)
        {
            Span<byte> body = datagram.Slice(headerSize);
            if (Encrypted)
            {
                ulong counter = SessionKeys.ExtendSequence(header.Sequence, _receiveHighest, _anyReceived);
                if (!SessionKeys.Open(_receiveKey, SessionKeys.PurposeSession, header.FragmentIndex, counter, datagram.Slice(0, headerSize), body))
                    return true; // forged, replayed from an old epoch, or corrupted: ignored
                body = body.Slice(0, body.Length - SessionKeys.TagSize);
                if (!_anyReceived || counter > _receiveHighest)
                    _receiveHighest = counter;
                _anyReceived = true;
            }

            LastReceiveTime = now;
            if (header.Type == PacketType.Disconnect)
                return false;

            if (header.Type == PacketType.Fragment)
            {
                if (Acks.HasReceived(header.Sequence))
                    Acks.AddReceivedBytes(datagram.Length, now);
                else if (!Acks.OnReceive(header.Sequence, now, datagram.Length))
                    return true; // stale
                Acks.ProcessAcks(header.Ack, header.AckBits, now, listener, index);
                if (State != ConnectionState.Connected)
                    return true; // listener disconnected us from a callback
                if (Fragments.TryAdd(header.Sequence, header.FragmentIndex, header.FragmentCount, body, out ReadOnlySpan<byte> message))
                    listener.OnPacket(index, header.Sequence, message);
                return true;
            }

            if (!Acks.OnReceive(header.Sequence, now, datagram.Length))
                return true; // duplicate or stale: its acks were already applied
            Acks.ProcessAcks(header.Ack, header.AckBits, now, listener, index);
            if (State != ConnectionState.Connected)
                return true;
            if (header.Type == PacketType.Payload && body.Length > 0)
                listener.OnPacket(index, header.Sequence, body);
            return true;
        }

        private ushort NextSequence(double now, int bytes, bool notify, NetListener listener, int index, out ulong counter)
        {
            counter = _sendCounter++;
            return Acks.OnSend(now, bytes, notify, listener, index);
        }

        private int WriteHeader(byte[] scratch, NetConfig config, PacketType type, ushort sequence, byte fragmentIndex, byte fragmentCount)
        {
            Acks.GetAckHeader(out ushort ack, out ulong ackBits);
            var header = new SessionHeader
            {
                Type = type,
                SessionId = SessionId,
                Sequence = sequence,
                Ack = ack,
                AckBits = ackBits,
                FragmentIndex = fragmentIndex,
                FragmentCount = fragmentCount,
            };
            return Protocol.WriteSessionHeader(scratch, config.ProtocolId, header);
        }

        // Seals the body when encrypted (header as associated data, tag appended) and sends the datagram.
        private void Transmit(IUdpSocket socket, byte[] scratch, int headerSize, int bodyLength, byte fragmentIndex, ulong counter, double now)
        {
            int length = headerSize + bodyLength;
            if (Encrypted)
            {
                var packet = new Span<byte>(scratch);
                length = headerSize + SessionKeys.Seal(_sendKey, SessionKeys.PurposeSession, fragmentIndex, counter,
                    packet.Slice(0, headerSize), packet.Slice(headerSize), bodyLength);
            }
            socket.Send(new ReadOnlySpan<byte>(scratch, 0, length), Remote);
            LastSendTime = now;
        }
    }
}
