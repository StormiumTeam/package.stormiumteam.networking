﻿using System;
using System.Collections.Generic;
using Collections.Pooled;
using Cysharp.Threading.Tasks;
using DefaultEcs;
using GameHost.Core.Ecs;
using GameHost.Injection;
using GameHost.Revolution.Snapshot.Systems;
using GameHost.Revolution.Snapshot.Systems.Components;
using GameHost.Revolution.Snapshot.Utilities;
using GameHost.Simulation.TabEcs;

namespace GameHost.Revolution.Snapshot.Serializers
{
	/// <summary>
	///     Represent a simple serializer that you can extend.
	/// </summary>
	public abstract class SerializerBase : AppObject, ISerializer
	{
		private readonly PooledList<byte>                  bytePool;
		private readonly Dictionary<MergeGroup, BitBuffer> dataPerGroup         = new();
		private readonly BitBuffer                         deserializeBitBuffer = new();

		private readonly Dictionary<MergeGroup, (SerializationParameters parameters, MergeGroup group, PooledList<GameEntityHandle> handles)> dict = new();

		private readonly PooledList<UniTask> tasks = new();

		private (DeserializationParameters p, PooledList<byte>? data, PooledList<GameEntityHandle>? handles) deserializeArgs;

		/// <summary>
		///     GameWorld reference
		/// </summary>
		protected GameWorld GameWorld;

		public SerializerBase(ISnapshotInstigator instigator, Context context) : base(context)
		{
			DependencyResolver.Add(() => ref GameWorld);
			DependencyResolver.OnComplete(OnDependenciesResolved);

			Instigator = instigator;
			bytePool   = new PooledList<byte>();
		}

		/// <summary>
		///     Serialize immediately without doing it on another thread.
		/// </summary>
		public virtual bool SynchronousSerialize => true;

		/// <summary>
		///     Deserialize immediately without doing it on another thread.
		/// </summary>
		public virtual bool SynchronousDeserialize => true;


		public ISnapshotInstigator Instigator { get; set; }

		public ISerializerArchetype     SerializerArchetype { get; private set; }
		public SnapshotSerializerSystem System              { get; set; }

		public abstract void UpdateMergeGroup(ReadOnlySpan<Entity> clients, MergeGroupCollection collection);

		public virtual Span<UniTask> PrepareSerializeTask(SerializationParameters parameters, MergeGroupCollection groupCollection, ReadOnlySpan<GameEntityHandle> entities)
		{
			PrepareGlobal();

			if (SynchronousSerialize)
			{
				foreach (var group in groupCollection)
				{
					if (group.ClientSpan.IsEmpty)
						continue;

					if (!dict.TryGetValue(group, out var tuple))
					{
						dict[group]         = tuple = (default, default!, new PooledList<GameEntityHandle>());
						dataPerGroup[group] = new BitBuffer();
					}

					tuple.parameters = parameters;
					tuple.group      = group;
					tuple.handles.Clear();
					tuple.handles.AddRange(entities);

					dict[group] = tuple;

					__serialize(group);
				}

				return Span<UniTask>.Empty;
			}

			tasks.Clear();
			foreach (var group in groupCollection)
			{
				if (group.ClientSpan.IsEmpty)
					continue;

				if (!dict.TryGetValue(group, out var tuple))
				{
					dict[group]         = tuple = (default, default!, new PooledList<GameEntityHandle>());
					dataPerGroup[group] = new BitBuffer();
				}

				tuple.parameters = parameters;
				tuple.group      = group;
				tuple.handles.Clear();
				tuple.handles.AddRange(entities);

				dict[group] = tuple;

				tasks.Add(UniTask.Run(__serialize, group));
			}

			return tasks.Span;
		}

		public Span<byte> FinalizeSerialize(MergeGroup group)
		{
			var bitBuffer = dataPerGroup[group];

			bytePool.Clear();
			var span = bytePool.AddSpan(bitBuffer.Length);
			bitBuffer.ToSpan(ref span);

			return span;
		}

		public virtual UniTask PrepareDeserializeTask(DeserializationParameters parameters, Span<byte> data, ReadOnlySpan<GameEntityHandle> entities)
		{
			PrepareGlobal();

			deserializeArgs.p       =   parameters;
			deserializeArgs.data    ??= new PooledList<byte>();
			deserializeArgs.handles ??= new PooledList<GameEntityHandle>();
			deserializeArgs.data.Clear();
			deserializeArgs.data.AddRange(data);
			deserializeArgs.handles.Clear();
			deserializeArgs.handles.AddRange(entities);

			if (SynchronousDeserialize)
			{
				__deserialize();
				return UniTask.CompletedTask;
			}

			return UniTask.Run(__deserialize);
		}

		private void __serialize(object? state)
		{
			var (parameters, group, handles) = dict[(MergeGroup) state!];
			Serialize(parameters, group, handles.Span);
		}

		public Span<byte> Serialize(SerializationParameters parameters, MergeGroup group, ReadOnlySpan<GameEntityHandle> entities)
		{
			lock (Synchronization)
			{
				var bitBuffer = dataPerGroup[group];

				bitBuffer.Clear();
				bytePool.Clear();

				OnSerialize(bitBuffer, parameters, group, entities);

				bytePool.AddSpan(bitBuffer.Length);

				var span = bytePool.Span;
				bitBuffer.ToSpan(ref span);
				return span;
			}
		}

		protected virtual void PrepareGlobal()
		{
		}

		private void __deserialize()
		{
			// these variables shouldn't be null when this is called
			Deserialize(deserializeArgs.p, deserializeArgs.data!.Span, deserializeArgs.handles!.Span);
		}

		public void Deserialize(DeserializationParameters parameters, Span<byte> data, ReadOnlySpan<GameEntityHandle> entities)
		{
			lock (Synchronization)
			{
				deserializeBitBuffer.Clear();

				var ro = (ReadOnlySpan<byte>) data;
				deserializeBitBuffer.FromSpan(ref ro, data.Length);

				OnDeserialize(deserializeBitBuffer, parameters, entities);
			}
		}

		protected virtual void OnDependenciesResolved(IEnumerable<object> deps)
		{
			SerializerArchetype = GetSerializerArchetype();
		}

		/// <summary>
		///     Get a <see cref="ISerializerArchetype" /> object which will manage entity validation and archetype deserialization.
		/// </summary>
		protected abstract ISerializerArchetype GetSerializerArchetype();

		protected abstract void OnSerialize(BitBuffer   bitBuffer, SerializationParameters   parameters, MergeGroup                     group, ReadOnlySpan<GameEntityHandle> entities);
		protected abstract void OnDeserialize(BitBuffer bitBuffer, DeserializationParameters parameters, ReadOnlySpan<GameEntityHandle> entities);

		/// <summary>
		///     Automatically resize a column based on the maximum entity id.
		/// </summary>
		protected void GetColumn<T>(ref T[] array, ReadOnlySpan<GameEntityHandle> entities)
		{
			if (entities.Length == 0)
				return;

			if (array.Length <= entities[^1].Id)
				Array.Resize(ref array, (int) entities[^1].Id * 2 + 1);
		}
	}
}