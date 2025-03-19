// This file is provided under The MIT License as part of RiptideNetworking.
// Copyright (c) not Tom Weiland but me https://github.com/Per6
// For additional information please see the included LICENSE.md file or view it on GitHub:
// https://github.com/RiptideNetworking/Riptide/blob/main/LICENSE.md

using System;

/// <summary></summary>
/// <typeparam name="T1"></typeparam>
/// <typeparam name="T2"></typeparam>
public class Either2<T1, T2> where T1 : class where T2 : class
{
	readonly object item;
	readonly bool IsT1;
	internal T1 t1 => IsT1 ? (T1)item : null;
	internal T2 t2 => IsT1 ? null : (T2)item;

	internal Either2(T1 item1) {
		item = item1 ?? throw new ArgumentNullException(nameof(item1));
		IsT1 = true;
	}

	internal Either2(T2 item2) {
		item = item2 ?? throw new ArgumentNullException(nameof(item2));
		IsT1 = false;
	}

	internal void Match(Action<T1> action1, Action<T2> action2) {
		if(IsT1) action1(t1);
		else action2(t2);
	}

	internal TResult Match<TResult>(Func<T1, TResult> func1, Func<T2, TResult> func2) {
		return IsT1 ? func1(t1) : func2(t2);
	}

	/// <summary>Converts an instance of <typeparamref name="T1"/> to <see cref="Either2{T1, T2}"/>.</summary>
	public static implicit operator Either2<T1, T2>(T1 item1) => new Either2<T1, T2>(item1);
	/// <summary>Converts an instance of <typeparamref name="T1"/> to <see cref="Either2{T1, T2}"/>.</summary>
	public static implicit operator Either2<T1, T2>(T2 item2) => new Either2<T1, T2>(item2);

	/// <summary></summary>
	public override string ToString() {
		string typeName = IsT1 ? nameof(T1) : nameof(T2);
		return $"Either<{typeName}>: {item}";
	}
}
