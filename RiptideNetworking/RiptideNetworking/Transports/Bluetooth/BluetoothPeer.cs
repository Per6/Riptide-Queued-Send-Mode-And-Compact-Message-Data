// This file is provided under The MIT License as part of RiptideNetworking.
// Copyright (c) not Tom Weiland but me https://github.com/Per6
// For additional information please see the included LICENSE.md file or view it on GitHub:
// https://github.com/RiptideNetworking/Riptide/blob/main/LICENSE.md

using System;

namespace Riptide.Transports.Bluetooth
{
    /// <summary>Provides base send &#38; receive functionality for <see cref="BluetoothServer"/> and <see cref="BluetoothClient"/>.</summary>
    public abstract class BluetoothPeer
    {
        /// <inheritdoc cref="IPeer.Disconnected"/>
        public event EventHandler<DisconnectedEventArgs> Disconnected;

        /// <summary>The default size used for the socket's send and receive buffers.</summary>
        protected const int DefaultSocketBufferSize = 1024 * 1024; // 1MB
        /// <summary>The minimum size that may be used for the socket's send and receive buffers.</summary>
        private const int MinSocketBufferSize = 256 * 1024; // 256KB

        /// <summary>Handles received data.</summary>
        /// <param name="dataBuffer">A byte array containing the received data.</param>
        /// <param name="amount">The number of bytes in <paramref name="dataBuffer"/> used by the received data.</param>
		/// <param name="fromConnection">The end point from which the data was recieved.</param>
        protected internal abstract void OnDataReceived(byte[] dataBuffer, int amount, BluetoothConnection fromConnection);

        /// <summary>Invokes the <see cref="Disconnected"/> event.</summary>
        /// <param name="connection">The closed connection.</param>
        /// <param name="reason">The reason for the disconnection.</param>
        protected internal virtual void OnDisconnected(Connection connection, DisconnectReason reason)
        {
            Disconnected?.Invoke(this, new DisconnectedEventArgs(connection, reason));
        }
    }
}