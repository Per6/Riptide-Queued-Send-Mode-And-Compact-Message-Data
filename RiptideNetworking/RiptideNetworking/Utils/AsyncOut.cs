// This file is provided under The MIT License as part of RiptideNetworking.
// Copyright (c) not Tom Weiland but me https://github.com/Per6
// For additional information please see the included LICENSE.md file or view it on GitHub:
// https://github.com/RiptideNetworking/Riptide/blob/main/LICENSE.md

using System;

#pragma warning disable CS8500

internal unsafe class AsyncOut<T> where T : class {
	private readonly T* _target;

	public AsyncOut(out T val) {
		fixed (T* ptr = &val) {
			_target = ptr;
		}
	}

	public void Set(T value) {
		if (_target == null)
			throw new InvalidOperationException("Target is null or has been moved.");

		*_target = value;
	}
}
#pragma warning restore CS8500
