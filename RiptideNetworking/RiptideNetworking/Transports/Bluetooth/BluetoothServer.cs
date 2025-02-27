// This file is provided under The MIT License as part of RiptideNetworking.
// Copyright (c) not Tom Weiland but me https://github.com/Per6
// For additional information please see the included LICENSE.md file or view it on GitHub:
// https://github.com/RiptideNetworking/Riptide/blob/main/LICENSE.md

using InTheHand.Net;
using System;
using System.Collections.Generic;

namespace Riptide.Transports.Bluetooth
{
    /// <summary>A server which can accept connections from <see cref="BluetoothClient"/>s.</summary>
    public class BluetoothServer : BluetoothPeer, IServer<Guid>
    {
        /// <inheritdoc/>
        public event EventHandler<ConnectedEventArgs> Connected;
        /// <inheritdoc/>
        public event EventHandler<DataReceivedEventArgs> DataReceived;

        /// <summary>The currently open connections, accessible by their endpoints.</summary>
        private HashSet<BluetoothConnection> connections;

		/// <inheritdoc/>
        public Guid Port { get; private set; }

		private BluetoothAddress bluetoothAddress;

        /// <inheritdoc/>
        public BluetoothServer(BluetoothAddress bluetoothAddress, int socketBufferSize = DefaultSocketBufferSize) : base(socketBufferSize)
        {
            connections = new HashSet<BluetoothConnection>();
			this.bluetoothAddress = bluetoothAddress;
        }

        /// <inheritdoc/>
        public void Start(Guid serviceGuid)
        {
			Port = serviceGuid;
            // OpenConnection(bluetoothAddress, serviceGuid);
        }

        /// <summary>Decides what to do with a connection attempt.</summary>
        /// <param name="fromConnection">The endpoint the connection attempt is coming from.</param>
        /// <returns>Whether or not the connection attempt was from a new connection.</returns>
        private bool HandleConnectionAttempt(BluetoothConnection fromConnection)
        {
            if (connections.Contains(fromConnection))
                return false;

            connections.Add(fromConnection);
            OnConnected(fromConnection);
            return true;
        }

		/// <inheritdoc/>
		public void Poll() {
			foreach (BluetoothConnection connection in connections)
				connection.Poll();
		}

        /// <inheritdoc/>
        public void Close(Connection connection)
        {
            if (connection is BluetoothConnection bluetoothConnection)
                connections.Remove(bluetoothConnection);
        }

        /// <inheritdoc/>
        public void Shutdown()
        {
            CloseConnection();
            connections.Clear();
        }

        /// <summary>Invokes the <see cref="Connected"/> event.</summary>
        /// <param name="connection">The successfully established connection.</param>
        protected virtual void OnConnected(Connection connection)
        {
            Connected?.Invoke(this, new ConnectedEventArgs(connection));
        }

        /// <inheritdoc/>
        protected internal override void OnDataReceived(byte[] dataBuffer, int amount, BluetoothConnection fromConnection)
		{
			if ((MessageHeader)(dataBuffer[0] & Message.HeaderBitmask) == MessageHeader.Connect && !HandleConnectionAttempt(fromConnection))
                return;

			if (!fromConnection.IsNotConnected)
				DataReceived?.Invoke(this, new DataReceivedEventArgs(dataBuffer, amount, fromConnection));
		}
    }
}