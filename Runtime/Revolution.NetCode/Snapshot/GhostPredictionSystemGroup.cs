using Unity.Collections;
using Unity.Core;
using Unity.Entities;
using Unity.Jobs;
using Unity.Jobs.LowLevel.Unsafe;
using Unity.Networking.Transport.Utilities;

namespace Unity.NetCode
{
    public abstract class BaseGhostPredictionSystemGroup : ComponentSystemGroup
    {
        public static bool ShouldPredict<T>(uint tick, Predicted<T> predicted)
        {
            return predicted.PredictionStartTick == 0 || SequenceHelpers.IsNewer(tick, predicted.PredictionStartTick);
        }

        public static bool ShouldPredict(uint tick, GhostPredictedComponent predicted)
        {
            return predicted.PredictionStartTick == 0 || SequenceHelpers.IsNewer(tick, predicted.PredictionStartTick);
        }

        public  uint                  PredictingTick;
        public  NativeArray<uint>     OldestPredictedTick;
        private NativeList<JobHandle> predictedTickWriters;
        public bool                  isServer;

        internal uint GroupOldestPredictedTick;

        private bool isMainGroup;
        
        public void AddPredictedTickWriter(JobHandle handle)
        {
            if (predictedTickWriters.Length >= predictedTickWriters.Capacity)
            {
                predictedTickWriters[0] = JobHandle.CombineDependencies(predictedTickWriters);
                predictedTickWriters.ResizeUninitialized(1);
            }

            predictedTickWriters.Add(handle);
        }

        protected override void OnCreate()
        {
            base.OnCreate();
            OldestPredictedTick  = new NativeArray<uint>(JobsUtility.MaxJobThreadCount, Allocator.Persistent);
            predictedTickWriters = new NativeList<JobHandle>(16, Allocator.Persistent);
            isServer             = World.GetExistingSystem<ServerSimulationSystemGroup>() != null;
            isMainGroup = GetType() == typeof(GhostPredictionSystemGroup);
        }

        protected override void OnDestroy()
        {
            predictedTickWriters.Dispose();
            OldestPredictedTick.Dispose();
            base.OnDestroy();
        }

        protected override void OnUpdate()
        {
            // If client, go from oldest applied predicted tick to target tick, apply. Allow filtering on latest received tick somehow
            if (isServer)
            {
                // If server, apply once
                var simulationSystemGroup = World.GetExistingSystem<ServerSimulationSystemGroup>();
                PredictingTick = simulationSystemGroup.ServerTick;
                base.OnUpdate();
            }
            else
            {
                if (predictedTickWriters.Length > 1)
                {
                    predictedTickWriters[0] = JobHandle.CombineDependencies(predictedTickWriters);
                    predictedTickWriters.ResizeUninitialized(1);
                }

                if (predictedTickWriters.Length > 0)
                    predictedTickWriters[0].Complete();
                predictedTickWriters.Clear();
                uint oldestAppliedTick = 0;
                if (isMainGroup)
                {
                    for (int i = 0; i < OldestPredictedTick.Length; ++i)
                    {
                        if (OldestPredictedTick[i] != 0)
                        {
                            if (oldestAppliedTick == 0 ||
                                SequenceHelpers.IsNewer(oldestAppliedTick, OldestPredictedTick[i]))
                                oldestAppliedTick = OldestPredictedTick[i];
                            OldestPredictedTick[i] = 0;
                        }
                    }

                    GroupOldestPredictedTick = oldestAppliedTick;
                }
                else
                {
                    oldestAppliedTick = World.GetExistingSystem<GhostPredictionSystemGroup>().GroupOldestPredictedTick;
                }

                var simulationSystemGroup = World.GetExistingSystem<ClientSimulationSystemGroup>();
                var targetTick            = simulationSystemGroup.ServerTick;

                if (oldestAppliedTick == 0 ||
                    !SequenceHelpers.IsNewer(targetTick, oldestAppliedTick))
                    //oldestAppliedTick = targetTick - 1;
                    return; // Nothing rolled back - nothing to predict
                // Do not try to predict more frames than we can have input for
                if (targetTick - oldestAppliedTick > CommandDataUtility.k_CommandDataMaxSize)
                    oldestAppliedTick = targetTick - CommandDataUtility.k_CommandDataMaxSize;

                var previousTime = Time;
                var elapsedTime  = previousTime.ElapsedTime;
                if (simulationSystemGroup.ServerTickFraction < 1)
                {
                    --targetTick;
                    elapsedTime -= simulationSystemGroup.ServerTickDeltaTime * simulationSystemGroup.ServerTickFraction;
                }

                for (uint i = oldestAppliedTick + 1; i != oldestAppliedTick + 3; ++i)
                {
                    uint tickAge = targetTick - i;
                    World.SetTime(new TimeData(elapsedTime - simulationSystemGroup.ServerTickDeltaTime * tickAge, simulationSystemGroup.ServerTickDeltaTime));
                    PredictingTick = i;
                    base.OnUpdate();
                }

                //Debug.Log($"Client Simulating From={oldestAppliedTick + 1} to {targetTick + 1}");

                if (simulationSystemGroup.ServerTickFraction < 1)
                {
                    PredictingTick = targetTick + 1;
                    World.SetTime(new TimeData(previousTime.ElapsedTime, simulationSystemGroup.ServerTickDeltaTime *
                                                                         simulationSystemGroup.ServerTickFraction));
                    base.OnUpdate();
                }

                World.SetTime(previousTime);
            }
        }
    }

    [UpdateInGroup(typeof(GhostSimulationSystemGroup))]
    public sealed class GhostPredictionSystemGroup : BaseGhostPredictionSystemGroup
    {
    }
}