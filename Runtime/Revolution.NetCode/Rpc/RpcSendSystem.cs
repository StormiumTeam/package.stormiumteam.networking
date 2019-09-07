using Revolution.NetCode;
using Unity.Burst;
using Unity.Collections;
using Unity.Entities;
using Unity.Jobs;
using Unity.Networking.Transport;
using UnityEngine;

namespace Revolution.NetCode
{
    [UpdateInGroup(typeof(ClientAndServerSimulationSystemGroup))]
    public class RpcSendSystem : JobComponentSystem
    {
        private NetworkStreamReceiveSystem m_ReceiveSystem;

        protected override void OnCreate()
        {
            m_ReceiveSystem = World.GetOrCreateSystem<NetworkStreamReceiveSystem>();
        }

        [BurstCompile]
        [ExcludeComponent(typeof(NetworkStreamDisconnected))]
        struct SendJob : IJobForEachWithEntity<NetworkStreamConnection>
        {
            public UdpNetworkDriver.Concurrent                            driver;
            public NetworkPipeline                                        reliablePipeline;
            public BufferFromEntity<OutgoingRpcDataStreamBufferComponent> rpcBufferFromEntity;

            public unsafe void Execute(Entity entity, int index, ref NetworkStreamConnection connection)
            {
                if (!connection.Value.IsCreated)
                    return;
                var buffer = rpcBufferFromEntity[entity];
                if (buffer.Length > 0)
                {
                    DataStreamWriter tmp = new DataStreamWriter(buffer.Length + sizeof(byte), Allocator.Temp);
                    tmp.Write((byte) NetworkStreamProtocol.Rpc);
                    tmp.WriteBytes((byte*) buffer.GetUnsafePtr(), buffer.Length);
                    driver.Send(reliablePipeline, connection.Value, tmp);
                    buffer.Clear();
                }
            }
        }

        protected override JobHandle OnUpdate(JobHandle inputDeps)
        {
            var sendJob = new SendJob();
            sendJob.driver              = m_ReceiveSystem.ConcurrentDriver;
            sendJob.reliablePipeline    = m_ReceiveSystem.RpcPipeline;
            sendJob.rpcBufferFromEntity = GetBufferFromEntity<OutgoingRpcDataStreamBufferComponent>();
            // FIXME: because the job gets buffer from entity
            return sendJob.ScheduleSingle(this, inputDeps);
        }
    }
}