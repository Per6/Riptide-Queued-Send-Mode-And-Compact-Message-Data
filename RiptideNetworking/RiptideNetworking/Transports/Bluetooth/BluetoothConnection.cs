// This file is provided under The MIT License as part of RiptideNetworking.
// Copyright (c) not Tom Weiland but me https://github.com/Per6
// For additional information please see the included LICENSE.md file or view it on GitHub:
// https://github.com/RiptideNetworking/Riptide/blob/main/LICENSE.md

namespace Riptide.Transports.Bluetooth
{
	/// <summary>Represents a connection to a <see cref="BluetoothServer"/> or <see cref="BluetoothClient"/>.</summary>
	public abstract class BluetoothConnection : Connection
	{
		internal abstract void Close();
		internal abstract void Poll();
	}
}