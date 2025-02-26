// This file is provided under The MIT License as part of RiptideNetworking.
// Copyright (c) Tom Weiland
// For additional information please see the included LICENSE.md file or view it on GitHub:
// https://github.com/RiptideNetworking/Riptide/blob/main/LICENSE.md

using System;
using System.Linq;
using System.Net.Sockets;
using InTheHand.Net;
using InTheHand.Net.Sockets;

namespace Riptide.Transports.Bluetooth
{
    /// <summary>A client which can connect to a <see cref="BluetoothServer"/>.</summary>
    public class BluetoothClient : BluetoothPeer, IClient
    {
        /// <inheritdoc/>
        public event EventHandler Connected;
        /// <inheritdoc/>
        public event EventHandler ConnectionFailed;
        /// <inheritdoc/>
        public event EventHandler<DataReceivedEventArgs> DataReceived;

        /// <summary>The connection to the server.</summary>
        private BluetoothConnection bluetoothConnection;

        /// <inheritdoc/>
        public BluetoothClient(int socketBufferSize = DefaultSocketBufferSize) : base(socketBufferSize) { }

        /// <inheritdoc/>
        /// <remarks>Expects the host address to consist of a Bluetooth address and service GUID, separated by a colon. For example: <c>00:1A:7D:DA:71:13:00001101-0000-1000-8000-00805F9B34FB</c>.</remarks>
        public bool Connect(string hostAddress, out Connection connection, out string connectError)
        {
            connectError = $"Invalid host address '{hostAddress}'! Bluetooth address and service GUID should be separated by a colon, for example: '00:1A:7D:DA:71:13:00001101-0000-1000-8000-00805F9B34FB'.";
            if (!ParseHostAddress(hostAddress, out BluetoothAddress bluetoothAddress, out Guid serviceGuid))
            {
                connection = null;
                return false;
            }

            try
            {
                OpenConnection(bluetoothAddress, serviceGuid);
                connection = bluetoothConnection = new BluetoothConnection(new BluetoothEndPoint(bluetoothAddress, serviceGuid), this);
                OnConnected(); // Bluetooth is connection-oriented, so the connection is established immediately
                return true;
            }
            catch (SocketException ex)
            {
                connectError = $"Failed to connect to the Bluetooth device: {ex.Message}";
                connection = null;
                OnConnectionFailed();
                return false;
            }
        }

        /// <summary>Parses <paramref name="hostAddress"/> into <paramref name="bluetoothAddress"/> and <paramref name="serviceGuid"/>, if possible.</summary>
        /// <param name="hostAddress">The host address to parse.</param>
        /// <param name="bluetoothAddress">The retrieved Bluetooth address.</param>
        /// <param name="serviceGuid">The retrieved service GUID.</param>
        /// <returns>Whether or not <paramref name="hostAddress"/> was in a valid format.</returns>
        private bool ParseHostAddress(string hostAddress, out BluetoothAddress bluetoothAddress, out Guid serviceGuid)
        {
            string[] addressAndGuid = hostAddress.Split(':');
            if (addressAndGuid.Length != 7)
            {
                bluetoothAddress = null;
                serviceGuid = Guid.Empty;
                return false;
            }

            string bluetoothAddressString = string.Join(":", addressAndGuid.Take(6));
            string serviceGuidString = addressAndGuid[6];

            return BluetoothAddress.TryParse(bluetoothAddressString, out bluetoothAddress)
				& Guid.TryParse(serviceGuidString, out serviceGuid);
        }

        /// <inheritdoc/>
        public void Disconnect()
        {
            CloseConnection();
        }

        /// <summary>Invokes the <see cref="Connected"/> event.</summary>
        protected virtual void OnConnected()
        {
            Connected?.Invoke(this, EventArgs.Empty);
        }

        /// <summary>Invokes the <see cref="ConnectionFailed"/> event.</summary>
        protected virtual void OnConnectionFailed()
        {
            ConnectionFailed?.Invoke(this, EventArgs.Empty);
        }

        /// <inheritdoc/>
        protected override void OnDataReceived(byte[] dataBuffer, int amount)
        {
            if (bluetoothConnection != null && !bluetoothConnection.IsNotConnected)
                DataReceived?.Invoke(this, new DataReceivedEventArgs(dataBuffer, amount, bluetoothConnection));
        }
    }
}