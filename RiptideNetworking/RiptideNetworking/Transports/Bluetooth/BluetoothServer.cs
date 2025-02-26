// This file is provided under The MIT License as part of RiptideNetworking.
// Copyright (c) Tom Weiland
// For additional information please see the included LICENSE.md file or view it on GitHub:
// https://github.com/RiptideNetworking/Riptide/blob/main/LICENSE.md

using System;
using System.Collections.Generic;
using InTheHand.Net;
using InTheHand.Net.Sockets;

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
        private Dictionary<BluetoothEndPoint, BluetoothConnection> connections;

		/// <inheritdoc/>
        public Guid Port { get; private set; }

        /// <inheritdoc/>
        public BluetoothServer(int socketBufferSize = DefaultSocketBufferSize) : base(socketBufferSize)
        {
            connections = new Dictionary<BluetoothEndPoint, BluetoothConnection>();
        }

        /// <inheritdoc/>
        public void Start(Guid serviceGuid)
        {
			Port = serviceGuid;
            OpenConnection(null, serviceGuid);
        }

        /// <summary>Decides what to do with a connection attempt.</summary>
        /// <param name="fromEndPoint">The endpoint the connection attempt is coming from.</param>
        /// <returns>Whether or not the connection attempt was from a new connection.</returns>
        private bool HandleConnectionAttempt(BluetoothEndPoint fromEndPoint)
        {
            if (connections.ContainsKey(fromEndPoint))
                return false;

            BluetoothConnection connection = new BluetoothConnection(fromEndPoint, this);
            connections.Add(fromEndPoint, connection);
            OnConnected(connection);
            return true;
        }

        /// <inheritdoc/>
        public void Close(Connection connection)
        {
            if (connection is BluetoothConnection bluetoothConnection)
                connections.Remove(bluetoothConnection.RemoteEndPoint);
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
        protected override void OnDataReceived(byte[] dataBuffer, int amount)
        {
            foreach (var connection in connections.Values)
            {
                if (!connection.IsNotConnected)
                    DataReceived?.Invoke(this, new DataReceivedEventArgs(dataBuffer, amount, connection));
            }
        }
    }
}