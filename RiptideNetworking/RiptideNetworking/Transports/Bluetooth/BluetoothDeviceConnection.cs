// This file is provided under The MIT License as part of RiptideNetworking.
// Copyright (c) not Tom Weiland but me https://github.com/Per6
// For additional information please see the included LICENSE.md file or view it on GitHub:
// https://github.com/RiptideNetworking/Riptide/blob/main/LICENSE.md

using System;
using System.IO;
using System.Threading.Tasks;
using InTheHand.Net;
using InTheHand.Net.Bluetooth;
using ITH = InTheHand.Net.Sockets;

namespace Riptide.Transports.Bluetooth
{
	/// <summary>Represents a connection to a <see cref="BluetoothServer"/> or <see cref="BluetoothClient"/>.</summary>
	public class BluetoothDeviceConnection : BluetoothConnection
	{
        ITH.BluetoothClient client;
		Stream stream;
		BluetoothPeer peer;

		/// <summary>An array to receive message size values into.</summary>
		private readonly byte[] sizeBytes = new byte[sizeof(int)];
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
        public override string ToString() => client.RemoteMachineName;

		internal async void Connect(BluetoothAddress serverAddress) {
			await Task.Yield();
			client.Connect(serverAddress, BluetoothService.SerialPort);
			stream = client.GetStream();
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

		/// <summary>Polls the stream and checks if any data was received.</summary>
		internal override void Recieve()
		{
			if(stream == null) return;
			while (TryReceive(ref nextMessageSize))
			{
				peer.OnDataReceived(Peer.ByteBuffer, nextMessageSize, this);
				nextMessageSize = 0;
			}
		}

		private bool TryReceive(ref int nextMessageSize)
		{
			try
			{
				int bytesRead;
				if (nextMessageSize == 0 && ((bytesRead = stream.Read(sizeBytes, 0, sizeof(int))) > 0))
				{
					// We have enough bytes for a complete size value
					nextMessageSize = BitConverter.ToInt32(sizeBytes, 0);
					if (nextMessageSize == 0) return true;
				}
				if (nextMessageSize == 0 || ((bytesRead = stream.Read(Peer.ByteBuffer, 0, sizeof(int))) <= 0)) return false;
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
			client.Close();
			stream.Close();
			stream.Dispose();
		}
	}

    internal class async
    {
    }
}