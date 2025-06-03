// This file is provided under The MIT License as part of RiptideNetworking.
// Copyright (c) not Tom Weiland but me https://github.com/Per6
// For additional information please see the included LICENSE.md file or view it on GitHub:
// https://github.com/RiptideNetworking/Riptide/blob/main/LICENSE.md

using System;
using System.Collections.Generic;

namespace Riptide.Transports.Bluetooth
{
	/// <summary>Represents a connection to a <see cref="BluetoothServer"/> or <see cref="BluetoothClient"/>.</summary>
	internal class BluetoothSelfConnection : BluetoothConnection
	{
		private BluetoothSelfConnection bsc;
		private BluetoothPeer peer;
		private List<byte[]> pendingMessages = new List<byte[]>();

		internal BluetoothSelfConnection(BluetoothPeer peer) {
			this.peer = peer;
		}

		/// <inheritdoc/>
        public override string ToString() => $"SelfConnection";

		/// <inheritdoc/>
		protected internal override void Send(byte[] dataBuffer, int amount) {
			byte[] data = new byte[amount];
			Array.Copy(dataBuffer, data, amount);
			bsc.pendingMessages.Add(data);
		}

		internal override void Close() {
			bsc = null;
		}

		internal void Connect(BluetoothServer server) {
			BluetoothSelfConnection c = server.AddSelfConnection();
			bsc = c;
			c.bsc = this;
		}

		internal override void Recieve() {
			foreach(byte[] data in pendingMessages) {
				peer.OnDataReceived(data, data.Length, this);
			}
			pendingMessages.Clear();
		}
	}
}