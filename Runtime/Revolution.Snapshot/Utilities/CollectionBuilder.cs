using System;
using System.Collections.Generic;

namespace Revolution
{
	public class CollectionBuilder<T>
	{
		private readonly FastDictionary<uint, T> m_Dictionary = new FastDictionary<uint, T>();

		public CollectionBuilder<T> Add(T val)
		{
			m_Dictionary[(uint) m_Dictionary.Count] = val;
			return this;
		}

		public CollectionBuilder<T> Set(uint index, T val)
		{
			if (m_Dictionary.ContainsKey(index))
				throw new InvalidOperationException($"Key '{index}' already exist!");
			m_Dictionary[index] = val;
			return this;
		}

		public FastDictionary<uint, T> Build()
		{
			return m_Dictionary;
		}
	}
}