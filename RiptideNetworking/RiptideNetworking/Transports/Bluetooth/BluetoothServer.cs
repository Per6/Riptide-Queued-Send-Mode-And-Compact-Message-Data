// This file is provided under The MIT License as part of RiptideNetworking.
// Copyright (c) not Tom Weiland but me https://github.com/Per6
// For additional information please see the included LICENSE.md file or view it on GitHub:
// https://github.com/RiptideNetworking/Riptide/blob/main/LICENSE.md

using InTheHand.Net.Bluetooth;
using Riptide.Utils;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using ITH = InTheHand.Net.Sockets;

namespace Riptide.Transports.Bluetooth
{
	/// <inheritdoc/>
	public class BluetoothServer : BluetoothPeer, IServer
	{
		static BluetoothServer listeningServer;

		/// <inheritdoc/>
		public event EventHandler<ConnectedEventArgs> Connected;
		/// <inheritdoc/>
		public event EventHandler<DataReceivedEventArgs> DataReceived;
		private ITH.BluetoothListener listener;
		private HashSet<BluetoothConnection> connections = new HashSet<BluetoothConnection>();
		private Task<ITH.BluetoothClient> pendingClient;
		private CancellationTokenSource cancelPendingClient;

		/// <inheritdoc/>
		[Obsolete("BluetoothServer does not have a port.", true)]
		public ushort Port => throw new Exception("BluetoothServer does not have a port.");

		/// <inheritdoc/>
		public void Start(ushort _) {
			if(listeningServer != null) throw new Exception("A local BluetoothServer is already listening!");
			listeningServer = this;
			listener = new ITH.BluetoothListener(BluetoothService.SerialPort);
			listener.Start();
			SetNextPendingClient();
		}

		/// <inheritdoc/>
		public void Close(Connection connection) {
			if(!(connection is BluetoothConnection btc)) return;
			btc.Close();
			connections.Remove(btc);
		}

		private void SetNextPendingClient() {
			cancelPendingClient = new CancellationTokenSource();
			pendingClient = Task.Run(() => listener.AcceptBluetoothClient(), cancelPendingClient.Token);
		}

		/// <inheritdoc/>
		public void Poll() {
			if(listener == null) return;
			if(!listener.Active) throw new Exception("BluetoothListener is not active!");
			BluetoothRadio radio = BluetoothRadio.Default ?? throw new Exception("Bluetooth is not supported on this device.");
			if(radio.Mode == RadioMode.PowerOff) throw new Exception("Bluetooth is not enabled on this device.");
			if(pendingClient.IsCompleted) {
                ITH.BluetoothClient newClient = pendingClient.Result ?? throw new Exception("BluetoothClient is null!");
                SetNextPendingClient();

				BluetoothConnection newConnection = new BluetoothDeviceConnection(newClient, this);
				connections.Add(newConnection);
				OnConnected(newConnection);
			}

			foreach(BluetoothConnection connection in connections)
				connection.Recieve();
		}

		/// <inheritdoc/>
		public void Shutdown() {
			listener.Stop();
			listeningServer = null;
			if(!pendingClient.IsCompleted) cancelPendingClient.Cancel();
			foreach(BluetoothConnection client in connections)
				client.Close();
			connections.Clear();
		}

		internal static bool GetListeningServer(out BluetoothServer server) {
			if(listeningServer == null) {
				server = null;
				return false;
			}
			server = listeningServer;
			return true;
		}

		internal BluetoothSelfConnection AddSelfConnection() {
			BluetoothSelfConnection selfConnection = new BluetoothSelfConnection(this);
			connections.Add(selfConnection);
			OnConnected(selfConnection);
			return selfConnection;
		}

		/// <summary>Invokes the <see cref="Connected"/> event.</summary>
        /// <param name="connection">The successfully established connection.</param>
        protected virtual void OnConnected(Connection connection)
        {
            Connected?.Invoke(this, new ConnectedEventArgs(connection));
        }

		/// <inheritdoc/>
		protected internal override void OnDataReceived(byte[] dataBuffer, int amount, BluetoothConnection fromConnection) {
			if(connections.Contains(fromConnection)) {
				DataReceived?.Invoke(this, new DataReceivedEventArgs(dataBuffer, amount, fromConnection));
			}
		}
	}
}