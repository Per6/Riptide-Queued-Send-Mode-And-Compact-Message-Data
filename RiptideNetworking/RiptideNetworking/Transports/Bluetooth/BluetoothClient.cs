// This file is provided under The MIT License as part of RiptideNetworking.
// Copyright (c) not Tom Weiland but me https://github.com/Per6
// For additional information please see the included LICENSE.md file or view it on GitHub:
// https://github.com/RiptideNetworking/Riptide/blob/main/LICENSE.md

using System;
using System.Linq;
using InTheHand.Net;
using InTheHand.Net.Bluetooth;
using Riptide.Utils;

namespace Riptide.Transports.Bluetooth
{
	/// <inheritdoc/>
	public class BluetoothClient : BluetoothPeer, IClient
	{
		/// <inheritdoc/>
        public event EventHandler Connected;
        /// <inheritdoc/>
        public event EventHandler ConnectionFailed;
		/// <inheritdoc/>
        public event EventHandler<DataReceivedEventArgs> DataReceived;
		private BluetoothConnection bluetoothConnection;

		/// <inheritdoc/>
		public void Poll() {
			bluetoothConnection?.Poll();
		}

		/// <inheritdoc/>
		public bool Connect(string hostAddress, out Connection connection, out string connectError) {
            if (!BluetoothAddress.TryParse(hostAddress, out BluetoothAddress serverAddress)) {
				connectError = $"Invalid host address '{hostAddress}'! Bluetooth address and service GUID should be separated by a colon, for example: '00:1A:7D:DA:71:13:00001101-0000-1000-8000-00805F9B34FB'.";
                connection = null;
                return false;
            }
			bluetoothConnection = serverAddress == BluetoothRadio.Default.LocalAddress
				? (BluetoothConnection)new BluetoothSelfConnection(this)
				: new BluetoothDeviceConnection(new InTheHand.Net.Sockets.BluetoothClient(), this);
			try {
				Connect(serverAddress);
				RiptideLogger.Log(LogType.Info, "Connected to server.");
				connection = bluetoothConnection;
				connectError = "";
				return true;
			} catch (Exception e) {
				connection = null;
				connectError = $"Failed to connect to server: {e}";
				return false;
			}
		}

		private void Connect(BluetoothAddress serverAddress) {
			switch(bluetoothConnection) {
				case BluetoothSelfConnection selfConnection:
					if(BluetoothServer.GetListeningServer(out BluetoothServer server))
						selfConnection.Connect(server);
					break;
				case BluetoothDeviceConnection deviceConnection:
					deviceConnection.Connect(serverAddress);
				break;
			}
		}

		/// <inheritdoc/>
		public void Disconnect() {
			bluetoothConnection?.Close();
		}

		/// <inheritdoc/>
		protected virtual void OnConnected()
        {
            Connected?.Invoke(this, EventArgs.Empty);
        }

		/// <inheritdoc/>
        protected virtual void OnConnectionFailed()
        {
            ConnectionFailed?.Invoke(this, EventArgs.Empty);
        }

		/// <inheritdoc/>
		protected internal override void OnDataReceived(byte[] dataBuffer, int amount, BluetoothConnection fromConnection)
		{
			DataReceived?.Invoke(this, new DataReceivedEventArgs(Peer.ByteBuffer, amount, fromConnection));
		}
	}

}