// This file is provided under The MIT License as part of RiptideNetworking.
// Copyright (c) not Tom Weiland but me https://github.com/Per6
// For additional information please see the included LICENSE.md file or view it on GitHub:
// https://github.com/RiptideNetworking/Riptide/blob/main/LICENSE.md

using System;
using System.Threading.Tasks;
using InTheHand.Net;
using InTheHand.Net.Bluetooth;
using Riptide.Utils;
using ITH = InTheHand.Net.Sockets;

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
        public override string ToString() {
            return $"BluetoothClient {bluetoothConnection?.ToString() ?? "Not connected"}";
        }

        /// <inheritdoc/>
        public void Poll() {
			bluetoothConnection?.Recieve();
		}

		/// <inheritdoc/>
		public async Task<Either2<Connection, string>> Connect(string hostNameAndMaybePin) {
			ParseNameAndPin(hostNameAndMaybePin, out string serverName, out string serverPin);
			try {
				bluetoothConnection = serverName == BluetoothRadio.Default.Name
					? (BluetoothConnection)new BluetoothSelfConnection(this)
					: new BluetoothDeviceConnection(new ITH.BluetoothClient(), this);
				await Connect(serverName, serverPin, OnConnected);
				return bluetoothConnection;
			} catch (Exception e) {
				OnConnectionFailed();
				return $"Failed to connect to server: {e}";
			}
		}

		private async Task Connect(string serverName, string devicePin, Action OnConnected) {
			switch(bluetoothConnection) {
				case BluetoothSelfConnection selfConnection:
					if(BluetoothServer.GetListeningServer(out BluetoothServer server)) {
						selfConnection.Connect(server);
						OnConnected();
					}
					break;
				case BluetoothDeviceConnection deviceConnection:
					await deviceConnection.Connect(serverName, devicePin, OnConnected);
					break;
			}
		}

		private void ParseNameAndPin(string hostAddress, out string serverName, out string devicePin) {
			int lastColon = hostAddress.LastIndexOf(':');
			if(lastColon == -1) {
				serverName = hostAddress;
				devicePin = null;
				return;
			}
			serverName = hostAddress.Substring(0, lastColon);
			devicePin = hostAddress.Substring(lastColon + 1);
			return;
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
			DataReceived?.Invoke(this, new DataReceivedEventArgs(dataBuffer, amount, fromConnection));
		}
	}

}