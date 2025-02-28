// This file is provided under The MIT License as part of RiptideNetworking.
// Copyright (c) not Tom Weiland but me https://github.com/Per6
// For additional information please see the included LICENSE.md file or view it on GitHub:
// https://github.com/RiptideNetworking/Riptide/blob/main/LICENSE.md

using InTheHand.Net;
using InTheHand.Net.Bluetooth;
using InTheHand.Net.Sockets;
using Riptide.Utils;
using System;
using System.Collections.Generic;

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
		private BluetoothListener listener;
		private HashSet<BluetoothConnection> connections = new HashSet<BluetoothConnection>();

		/// <inheritdoc/>
		[Obsolete("BluetoothServer does not have a port.", true)]
        public ushort Port => throw new Exception("BluetoothServer does not have a port.");

		/// <inheritdoc/>
		public void Start(ushort port) {
			if(listeningServer != null) throw new Exception("A local BluetoothServer is already listening!");
			listeningServer = this;
			listener = new BluetoothListener(BluetoothService.SerialPort);
            listener.Start();
			RiptideLogger.Log(LogType.Info, "Server is waiting for bluetooth connections...");
        }

		/// <inheritdoc/>
        public void Close(Connection connection) {
			if(!(connection is BluetoothConnection btc)) return;
        	btc.Close();
			connections.Remove(btc);
        }

		/// <inheritdoc/>
        public void Poll() {
			if(listener == null) return;
			if(!listener.Active) throw new Exception("BluetoothListener is not active!");
			BluetoothRadio radio = BluetoothRadio.Default ?? throw new Exception("Bluetooth is not supported on this device.");
            if(radio.Mode == RadioMode.PowerOff)
				throw new Exception("Bluetooth is not enabled on this device.");
			if(listener.Pending()) {
				InTheHand.Net.Sockets.BluetoothClient newClient = listener.AcceptBluetoothClient();
				
				BluetoothConnection newConnection = new BluetoothDeviceConnection(newClient, this);
				connections.Add(newConnection);

				RiptideLogger.Log(LogType.Info, "New client connected.");
			}

			foreach(BluetoothConnection connection in connections)
				connection.Poll();
		}

		/// <inheritdoc/>
        public void Shutdown() {
			listener.Stop();
			listeningServer = null;
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

		/// <inheritdoc/>
        protected internal override void OnDataReceived(byte[] dataBuffer, int amount, BluetoothConnection fromConnection) {
			if((MessageHeader)(dataBuffer[0] & Message.HeaderBitmask) == MessageHeader.Connect && !HandleConnectionAttempt(fromConnection))
                return;

			if(connections.Contains(fromConnection) && !fromConnection.IsNotConnected)
                DataReceived?.Invoke(this, new DataReceivedEventArgs(dataBuffer, amount, fromConnection));
		}

        private bool HandleConnectionAttempt(BluetoothConnection fromConnection) {
            if(connections.Contains(fromConnection))
				return false;
			
			connections.Add(fromConnection);
			Connected?.Invoke(this, new ConnectedEventArgs(fromConnection));
			return true;
        }

		internal BluetoothSelfConnection AddSelfConnection() {
			BluetoothSelfConnection selfConnection = new BluetoothSelfConnection(this);
			connections.Add(selfConnection);
			return selfConnection;
		}
    }
}