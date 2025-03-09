// This file is provided under The MIT License as part of RiptideNetworking.
// Copyright (c) not Tom Weiland but me https://github.com/Per6
// For additional information please see the included LICENSE.md file or view it on GitHub:
// https://github.com/RiptideNetworking/Riptide/blob/main/LICENSE.md


namespace Riptide.Utils
{
	class SplitList<T> {
		int sliceSize;
		WrappingList<T> list = new WrappingList<T>();
		Bitfield setSlices = new Bitfield();

		internal SplitList(int sliceSize) {
			this.sliceSize = sliceSize;
		}

		internal void AddSlice(T[] values, int index) {
			if(values.Length != sliceSize) throw new System.Exception("Invalid slice size");
			setSlices.Set(index);
			index *= sliceSize;
			for(int i = 0; i < sliceSize; i++)
				list.SetUnchecked(index + i, values[i]);
		}

		internal bool TryCollect(int length, out T[] values) {
			if(!setSlices.AllSet(length)) {
				values = null;
				return false;
			}
			values = new T[length * sliceSize];
			return true;
		}
	}
}