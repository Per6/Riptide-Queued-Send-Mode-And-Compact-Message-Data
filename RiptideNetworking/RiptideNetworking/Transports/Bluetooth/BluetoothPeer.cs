// This file is provided under The MIT License as part of RiptideNetworking.
// Copyright (c) not Tom Weiland but me https://github.com/Per6
// For additional information please see the included LICENSE.md file or view it on GitHub:
// https://github.com/RiptideNetworking/Riptide/blob/main/LICENSE.md

using System;
using System.Collections.Generic;
using System.Net.Sockets;
using InTheHand.Net;

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
        /// <summary>How long to wait for a packet, in microseconds.</summary>
        private const int ReceivePollingTime = 500000; // 0.5 seconds

        /// <summary>The size to use for the socket's send and receive buffers.</summary>
        private readonly int socketBufferSize;
        /// <summary>The Bluetooth socket to use for sending and receiving.</summary>
        private InTheHand.Net.Sockets.BluetoothClient bluetoothClient;
        /// <summary>The Bluetooth stream for sending and receiving data.</summary>
        private NetworkStream bluetoothStream;
        /// <summary>Whether or not the transport is running.</summary>
        private bool isRunning;

        /// <summary>Initializes the transport.</summary>
        /// <param name="socketBufferSize">How big the socket's send and receive buffers should be.</param>
        protected BluetoothPeer(int socketBufferSize)
        {
            if (socketBufferSize < MinSocketBufferSize)
                throw new ArgumentOutOfRangeException(nameof(socketBufferSize), $"The minimum socket buffer size is {MinSocketBufferSize}!");

            this.socketBufferSize = socketBufferSize;
        }

        /// <summary>Opens the Bluetooth connection and starts the transport.</summary>
        /// <param name="deviceAddress">The Bluetooth address of the device to connect to.</param>
        /// <param name="serviceGuid">The GUID of the service to connect to.</param>
        protected BluetoothConnection OpenConnection(BluetoothAddress deviceAddress, Guid serviceGuid)
        {
            if (isRunning)
                CloseConnection();

            bluetoothClient = new InTheHand.Net.Sockets.BluetoothClient();
			BluetoothEndPoint remoteEndPoint = new BluetoothEndPoint(deviceAddress, serviceGuid);
            bluetoothClient.Connect(remoteEndPoint);
            bluetoothStream = bluetoothClient.GetStream();

            isRunning = true;
			return new BluetoothConnection(bluetoothClient, remoteEndPoint, this);
        }

        /// <summary>Closes the Bluetooth connection and stops the transport.</summary>
        protected void CloseConnection()
        {
            if (!isRunning)
                return;

            isRunning = false;
            bluetoothStream?.Close();
            bluetoothClient?.Close();
        }

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