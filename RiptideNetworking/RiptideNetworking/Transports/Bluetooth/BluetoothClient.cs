// This file is provided under The MIT License as part of RiptideNetworking.
// Copyright (c) not Tom Weiland but me https://github.com/Per6
// For additional information please see the included LICENSE.md file or view it on GitHub:
// https://github.com/RiptideNetworking/Riptide/blob/main/LICENSE.md

using System;
using System.Linq;
using System.Net.Sockets;
using InTheHand.Net;
using Riptide.Utils;

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
                connection = bluetoothConnection = OpenConnection(bluetoothAddress, serviceGuid);
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

		/// <inheritdoc/>
		public void Poll() {
			if (bluetoothConnection != null && !bluetoothConnection.IsNotConnected)
				bluetoothConnection.Poll();
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

			RiptideLogger.Log(LogType.Info, $"Bluetooth address: {bluetoothAddressString}, service GUID: {serviceGuidString}");
			if(!BluetoothAddress.TryParse(bluetoothAddressString, out bluetoothAddress))
				return false;
			RiptideLogger.Log(LogType.Info, $"Bluetooth address: {bluetoothAddress}");
			if(!Guid.TryParse(serviceGuidString, out serviceGuid))
				return false;
			RiptideLogger.Log(LogType.Info, $"Bluetooth address: {bluetoothAddress}, service GUID: {serviceGuid}");
			return true;
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
        protected internal override void OnDataReceived(byte[] dataBuffer, int amount, BluetoothConnection fromConnection)
        {
            if (bluetoothConnection != null && !bluetoothConnection.IsNotConnected)
                DataReceived?.Invoke(this, new DataReceivedEventArgs(dataBuffer, amount, fromConnection));
        }
    }
}