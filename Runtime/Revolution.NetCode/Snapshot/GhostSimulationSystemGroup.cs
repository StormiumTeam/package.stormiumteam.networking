using Unity.Entities;
using Unity.Jobs;
using Unity.Transforms;

namespace Unity.NetCode
{
    [UpdateInGroup(typeof(ClientAndServerSimulationSystemGroup))]
    [UpdateBefore(typeof(TransformSystemGroup))]
    [UpdateAfter(typeof(SnapshotReceiveSystem))]
    public class GhostSimulationSystemGroup : ComponentSystemGroup
    {
    }

    [UpdateInGroup(typeof(GhostSimulationSystemGroup))]
    [UpdateBefore(typeof(GhostPredictionSystemGroup))]
    [UpdateInWorld(UpdateInWorld.TargetWorld.Client)]
    public class GhostUpdateSystemGroup : ComponentSystemGroup
    {
	    public JobHandle LastGhostMapWriter;
    }

    [UpdateInGroup(typeof(ClientSimulationSystemGroup))]
    public class GhostSpawnSystemGroup : ComponentSystemGroup
    {
    }
}
