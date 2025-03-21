// This file is provided under The MIT License as part of RiptideNetworking.
// Copyright (c) not Tom Weiland but me https://github.com/Per6
// For additional information please see the included LICENSE.md file or view it on GitHub:
// https://github.com/RiptideNetworking/Riptide/blob/main/LICENSE.md

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using InTheHand.Net;
using InTheHand.Net.Bluetooth;
using Riptide.Utils;
using ITH = InTheHand.Net.Sockets;

namespace Riptide.Transports.Bluetooth
{
	/// <summary>Represents a connection to a <see cref="BluetoothServer"/> or <see cref="BluetoothClient"/>.</summary>
	public class BluetoothDeviceConnection : BluetoothConnection
	{
        ITH.BluetoothClient client;
		Stream stream;
		BluetoothPeer peer;
		private Task<byte[]> pendingData;
		private CancellationTokenSource cancelPendingData;

		/// <summary>The size of the next message to be received.</summary>
		private int nextMessageSize;

		/// <summary>Initializes the connection.</summary>
		/// <param name="client">The Bluetooth client to use for sending and receiving.</param>
		/// <param name="peer">The local peer this connection is associated with.</param>
		internal BluetoothDeviceConnection(ITH.BluetoothClient client, BluetoothPeer peer)
		{
			this.client = client;
			this.peer = peer;
		}

		/// <inheritdoc/>
        public override string ToString() => client.ToString();

		internal void SetStream(ITH.BluetoothClient client) {
			stream = client.GetStream();
			if(stream == null) throw new Exception($"Stream is null. Cannot receive data.");
			StartPendingData();
		}

		internal async Task Connect(string deviceName, string devicePin, Action OnConnected) {
			RiptideLogger.Log(LogType.Info, $"Establishing Connection to {deviceName}{(devicePin != null ? ":" + devicePin : "")}...");
			await Task.Run(() => {
				ITH.BluetoothDeviceInfo device = DiscoverServer(deviceName.ToUpper()) ?? throw new Exception($"Device '{deviceName}' not found.");
				if(!device.Authenticated) {
					if(devicePin == null) throw new Exception($"Device '{deviceName}' is not paired. Please pair it first or add a device pin.");
					BluetoothSecurity.PairRequest(device.DeviceAddress, devicePin);
				}
				device.Refresh();
				client.Connect(device.DeviceAddress, BluetoothService.SerialPort);
			});
			SetStream(client);
			OnConnected();
		}

		private ITH.BluetoothDeviceInfo DiscoverServer(string deviceName) {
			ITH.BluetoothDeviceInfo closestDevice = null;
			List<string> possibleDevices = new List<string>();
			RiptideLogger.Log(LogType.Info, $"(BLUETOOTH) Searching for device '{deviceName}'...");
			foreach(ITH.BluetoothDeviceInfo device in client.DiscoverDevices()) {
				if(device.DeviceName.ToUpper() == deviceName) return device;
				if(device.DeviceName.ToUpper().Contains(deviceName)) closestDevice = device;
				possibleDevices.Add(device.DeviceName);
			}
			string possibleDevicesString = string.Join(", ", possibleDevices);
			if(closestDevice == null) throw new Exception($"(BLUETOOTH) Device '{deviceName}' not found. Try one of: [{possibleDevicesString}]");
			return closestDevice;
		}

		/// <inheritdoc/>
		protected internal override void Send(byte[] dataBuffer, int amount)
		{
			if(stream == null) return;
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

		private void StartPendingData() => SetPendingData(sizeof(int));
		private void SetPendingData(int size) {
			cancelPendingData = new CancellationTokenSource();
			pendingData = Task.Run(() => {
				byte[] data = new byte[size];
				int offset = 0;
				while (offset < size) {
					if (!stream.CanRead) return null;
					int read = stream.Read(data, offset, size - offset);
					if (read == 0) return null;
					offset += read;
				}
				return data;
			}, cancelPendingData.Token);
		}

		/// <summary>Polls the stream and checks if any data was received.</summary>
		internal override void Recieve()
		{
			if(stream == null) return;
			while (TryReceive(ref nextMessageSize))
			{
				StartPendingData();
				peer.OnDataReceived(Peer.ByteBuffer, nextMessageSize, this);
				nextMessageSize = 0;
			}
		}

		private bool PendingDataHasResult() {
			if(pendingData.Status != TaskStatus.RanToCompletion) return false;
			return pendingData.Result != null;
		}

		private bool TryReceive(ref int nextMessageSize)
		{
			try
			{
				if(!PendingDataHasResult()) return false;
				if(nextMessageSize == 0) {
					nextMessageSize = BitConverter.ToInt32(pendingData.Result, 0);
					if(nextMessageSize == 0) return true;
					SetPendingData(nextMessageSize);
					if(!PendingDataHasResult()) return false;
				}
				Array.Copy(pendingData.Result, 0, Peer.ByteBuffer, 0, nextMessageSize);
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
		internal override void Close()
		{
			cancelPendingData.Cancel();
			client.Close();
			stream?.Close();
			stream?.Dispose();
		}
	}
}