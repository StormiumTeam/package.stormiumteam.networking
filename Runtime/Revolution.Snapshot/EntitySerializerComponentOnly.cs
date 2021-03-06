using System;
using System.Collections.Generic;
using System.Linq;
using Unity.Burst;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Entities;
using Unity.Jobs;
using UnityEngine.Profiling;

namespace Revolution
{
	/// <summary>
	///     Base class for doing entity standard snapshot operations
	/// </summary>
	/// <typeparam name="TSerializer"></typeparam>
	/// <typeparam name="TComponent"></typeparam>
	/// <typeparam name="TSharedData"></typeparam>
	[UpdateInGroup(typeof(SnapshotWithDelegateSystemGroup))]
	public abstract unsafe class EntitySerializerComponent<TSerializer, TComponent, TSharedData> : JobComponentSystem, IEntityComponents, ISystemDelegateForSnapshot, IDynamicSnapshotSystem
		where TSerializer : EntitySerializerComponent<TSerializer, TComponent, TSharedData>
		where TComponent : struct, IComponentData
		where TSharedData : struct
	{
		public const uint                                 SnapshotHistorySize = SnapshotDataExtensions.SnapshotHistorySize;
		private      BurstDelegate<OnDeserializeSnapshot> m_DeserializeDelegate;

		private         BurstDelegate<OnSerializeSnapshot> m_SerializeDelegate;
		public abstract ComponentType                      ExcludeComponent { get; }

		private FixedString512 m_NativeName;
		public FixedString512 NativeName => m_NativeName;

		public bool IsChunkValid(ArchetypeChunk chunk)
		{
			var ownComponents  = EntityComponents;
			var componentTypes = chunk.Archetype.GetComponentTypes();
			if (componentTypes.Length < ownComponents.Length)
				return false;

			var ownLength = ownComponents.Length;
			var match     = 0;
			for (var i = 0; i < componentTypes.Length; i++)
			{
				// If the chunk excluded us, there is no reason to continue...
				if (componentTypes[i].TypeIndex == ExcludeComponent.TypeIndex)
					return false;

				for (var j = 0; j < ownLength; j++)
					if (componentTypes[i].TypeIndex == ownComponents[j].TypeIndex)
						match++;
			}

			return match == ownLength;
		}

		public void OnDeserializerArchetypeUpdate(NativeArray<Entity> entities, NativeArray<uint> archetypes, Dictionary<uint, NativeArray<uint>> archetypeToSystems)
		{
			var systemId        = World.GetOrCreateSystem<SnapshotManager>().GetSystemId(this);
			var snapshotManager = World.GetOrCreateSystem<SnapshotManager>();
			for (int ent = 0, length = entities.Length; ent < length; ent++)
			{
				var archetype = archetypes[ent];
				var models    = archetypeToSystems[archetype];
				var hasModel  = false;

				// Search if this entity has our system from the model list
				foreach (var model in models)
					// Bingo! This entity got our system
					if (model == systemId)
					{
						hasModel = true;

						if (!EntityManager.HasComponent<TComponent>(entities[ent]))
							for (var i = 0; i != EntityComponents.Length; i++)
								EntityManager.AddComponent(entities[ent], EntityComponents[i]);

						break;
					}

				if (hasModel)
					continue;

				if (!EntityManager.HasComponent<TComponent>(entities[ent]))
					continue;


				if (!EntityComponents.Contains(ComponentType.ReadWrite<TComponent>()))
					throw new InvalidOperationException();

				// If the entity had the snapshot (so the model) before, but now it don't have it anymore, remove the snapshot and components
				var componentsToRemove = new NativeList<ComponentType>(EntityComponents.Length, Allocator.Temp);
				componentsToRemove.AddRange(EntityComponents);
				foreach (var model in models)
				{
					var system = snapshotManager.GetSystem(model);

					// First, check if another system got the same components as us.
					if (system is IEntityComponents systemWithInterface)
					{
						var systemEntityComponents = systemWithInterface.EntityComponents;
						for (var i = 0; i < componentsToRemove.Length; i++)
						{
							if (!systemEntityComponents.Contains(componentsToRemove[i]))
								continue;

							// If that system got the same component as us, remove if from the remove list.
							componentsToRemove.RemoveAtSwapBack(i);
							i--;
						}
					}
				}

				foreach (var type in componentsToRemove)
					EntityManager.RemoveComponent(entities[ent], type);
			}
		}

		public abstract NativeArray<ComponentType> EntityComponents { get; }

		public FunctionPointer<OnSerializeSnapshot>   SerializeDelegate   => m_SerializeDelegate.Get();
		public FunctionPointer<OnDeserializeSnapshot> DeserializeDelegate => m_DeserializeDelegate.Get();

		public abstract void OnBeginSerialize(Entity entity);

		public abstract void OnBeginDeserialize(Entity entity);

		public void SetEmptySafetyHandle<TComponent>(ref ComponentTypeHandle<TComponent> comp) where TComponent : struct, IComponentData
		{
#if ENABLE_UNITY_COLLECTIONS_CHECKS
			SafetyUtility.Replace(ref comp, SafetyHandle);
#endif
		}


		protected virtual void SetSystemGroup()
		{
			var delegateGroup = World.GetOrCreateSystem<SnapshotWithDelegateSystemGroup>();
			if (!delegateGroup.Systems.Contains(this))
				delegateGroup.AddSystemToUpdateList(this);
		}


		protected override void OnCreate()
		{
			base.OnCreate();

			World.GetOrCreateSystem<SnapshotManager>().RegisterSystem(this);
			m_NativeName = ToString();

			SetSystemGroup();

#if ENABLE_UNITY_COLLECTIONS_CHECKS
			SafetyHandle = AtomicSafetyHandle.Create();
#endif

			GetDelegates(out m_SerializeDelegate, out m_DeserializeDelegate);
		}

		protected override void OnDestroy()
		{
			base.OnDestroy();

#if ENABLE_UNITY_COLLECTIONS_CHECKS
			AtomicSafetyHandle.Release(SafetyHandle);
#endif
		}

		protected abstract void GetDelegates(out BurstDelegate<OnSerializeSnapshot> onSerialize, out BurstDelegate<OnDeserializeSnapshot> onDeserialize);

		protected override JobHandle OnUpdate(JobHandle inputDeps)
		{
			return inputDeps;
		}

		private static readonly void* s_SharedData;

		static EntitySerializerComponent()
		{
			s_SharedData = SharedStatic<TSharedData>.GetOrCreate<SharedKey>().UnsafeDataPointer;
		}

		protected static ref TSharedData GetShared()
		{
			if (s_SharedData == null)
				throw new Exception("null shared data");
			return ref UnsafeUtility.AsRef<TSharedData>(s_SharedData);
		}

		private class SharedKey
		{
		}

		private class ChunkKey
		{
		}

		private class GhostKey
		{
		}

#if ENABLE_UNITY_COLLECTIONS_CHECKS

		protected AtomicSafetyHandle SafetyHandle { get; private set; }
#endif
	}
}