// This file is provided under The MIT License as part of RiptideNetworking.
// Copyright (c) not Tom Weiland but me https://github.com/Per6
// For additional information please see the included LICENSE.md file or view it on GitHub:
// https://github.com/RiptideNetworking/Riptide/blob/main/LICENSE.md

using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Sockets;
using InTheHand.Net;

namespace Riptide.Transports.Bluetooth
{
    /// <summary>Represents a connection to a <see cref="BluetoothServer"/> or <see cref="BluetoothClient"/>.</summary>
    public class BluetoothConnection : Connection, IEquatable<BluetoothConnection>
    {
        /// <summary>The endpoint representing the other end of the connection.</summary>
        public readonly BluetoothEndPoint RemoteEndPoint;

        /// <summary>The Bluetooth client to use for sending and receiving.</summary>
        private readonly InTheHand.Net.Sockets.BluetoothClient client;
        /// <summary>The stream used for sending and receiving data.</summary>
        private readonly NetworkStream stream;
        /// <summary>The local peer this connection is associated with.</summary>
        private readonly BluetoothPeer peer;
        /// <summary>An array to receive message size values into.</summary>
        private readonly byte[] sizeBytes = new byte[sizeof(int)];
        /// <summary>The size of the next message to be received.</summary>
        private int nextMessageSize;

        /// <summary>Initializes the connection.</summary>
        /// <param name="client">The Bluetooth client to use for sending and receiving.</param>
        /// <param name="remoteEndPoint">The endpoint representing the other end of the connection.</param>
        /// <param name="peer">The local peer this connection is associated with.</param>
        internal BluetoothConnection(InTheHand.Net.Sockets.BluetoothClient client, BluetoothEndPoint remoteEndPoint, BluetoothPeer peer)
        {
            RemoteEndPoint = remoteEndPoint;
            this.client = client;
            this.stream = client.GetStream();
            this.peer = peer;
        }

        /// <inheritdoc/>
        protected internal override void Send(byte[] dataBuffer, int amount)
        {
            try
            {
                if (client.Connected)
                {
                    byte[] amountBytes = BitConverter.GetBytes(amount);
                    stream.Write(amountBytes, 0, sizeof(int));
                    stream.Write(dataBuffer, 0, amount);
                }
            }
            catch (IOException)
            {
                // Handle Bluetooth disconnection or errors
                peer.OnDisconnected(this, DisconnectReason.TransportError);
            }
            catch (ObjectDisposedException)
            {
                peer.OnDisconnected(this, DisconnectReason.TransportError);
            }
        }

		internal void Poll() {
			Receive();
		}

        /// <summary>Polls the stream and checks if any data was received.</summary>
        internal void Receive()
        {
            while (TryReceive(ref nextMessageSize))
            {
                peer.OnDataReceived(Peer.ByteBuffer, nextMessageSize, this);
                nextMessageSize = 0;
            }
        }

        private bool TryReceive(ref int nextMessageSize)
        {
            try
            {
				int bytesRead;
                if (nextMessageSize == 0 && ((bytesRead = stream.Read(sizeBytes, 0, sizeof(int))) > 0))
                {
                    // We have enough bytes for a complete size value
                    nextMessageSize = BitConverter.ToInt32(sizeBytes, 0);
                    if (nextMessageSize == 0) return true;
                }
                if (nextMessageSize == 0 || ((bytesRead = stream.Read(Peer.ByteBuffer, 0, sizeof(int))) <= 0)) return false;
                return true;
            }
            catch (IOException)
            {
                // Handle Bluetooth disconnection or errors
                peer.OnDisconnected(this, DisconnectReason.TransportError);
                return false;
            }
            catch (ObjectDisposedException)
            {
                peer.OnDisconnected(this, DisconnectReason.TransportError);
                return false;
            }
        }

        /// <summary>Closes the connection.</summary>
        internal void Close()
        {
            client.Close();
        }

        /// <inheritdoc/>
        public override string ToString() => RemoteEndPoint.ToString();

        /// <inheritdoc/>
        public override bool Equals(object obj) => Equals(obj as BluetoothConnection);
        /// <inheritdoc/>
        public bool Equals(BluetoothConnection other)
        {
            if (other is null)
                return false;

            if (ReferenceEquals(this, other))
                return true;

            return RemoteEndPoint.Equals(other.RemoteEndPoint);
        }

        /// <inheritdoc/>
        public override int GetHashCode()
        {
            return -288961498 + EqualityComparer<BluetoothEndPoint>.Default.GetHashCode(RemoteEndPoint);
        }

#pragma warning disable CS1591 // Missing XML comment for publicly visible type or member
        public static bool operator ==(BluetoothConnection left, BluetoothConnection right)
#pragma warning restore CS1591 // Missing XML comment for publicly visible type or member
        {
            if (left is null)
            {
                if (right is null)
                    return true;

                return false; // Only the left side is null
            }

            // Equals handles case of null on right side
            return left.Equals(right);
        }

#pragma warning disable CS1591 // Missing XML comment for publicly visible type or member
        public static bool operator !=(BluetoothConnection left, BluetoothConnection right) => !(left == right);
#pragma warning restore CS1591 // Missing XML comment for publicly visible type or member
    }
}