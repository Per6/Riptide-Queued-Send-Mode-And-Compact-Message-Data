// This file is provided under The MIT License as part of RiptideNetworking.
// Copyright (c) Tom Weiland
// For additional information please see the included LICENSE.md file or view it on GitHub:
// https://github.com/RiptideNetworking/Riptide/blob/main/LICENSE.md

using Riptide.Utils;
using System;
using System.Collections.Generic;
using InTheHand.Net.Sockets;
using InTheHand.Net;

namespace Riptide.Transports.Bluetooth
{
    /// <summary>Represents a connection to a <see cref="BluetoothServer"/> or <see cref="BluetoothClient"/>.</summary>
    public class BluetoothConnection : Connection, IEquatable<BluetoothConnection>
    {
        /// <summary>The endpoint representing the other end of the connection.</summary>
        public readonly BluetoothEndPoint RemoteEndPoint;

        /// <summary>The local peer this connection is associated with.</summary>
        private readonly BluetoothPeer peer;

        /// <summary>Initializes the connection.</summary>
        /// <param name="remoteEndPoint">The endpoint representing the other end of the connection.</param>
        /// <param name="peer">The local peer this connection is associated with.</param>
        internal BluetoothConnection(BluetoothEndPoint remoteEndPoint, BluetoothPeer peer)
        {
            RemoteEndPoint = remoteEndPoint;
            this.peer = peer;
        }

        /// <inheritdoc/>
        protected internal override void Send(byte[] dataBuffer, int amount)
        {
            peer.Send(dataBuffer, amount);
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