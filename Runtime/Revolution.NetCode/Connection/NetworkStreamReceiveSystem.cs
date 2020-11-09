using System;
using ENet;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Entities;
using Unity.Jobs;
using Unity.Networking.Transport;
using Unity.Networking.Transport.LowLevel.Unsafe;
using Unity.Networking.Transport.Utilities;
using UnityEngine;

namespace Unity.NetCode
{
    [DisableAutoCreation]
    [UpdateInWorld(UpdateInWorld.TargetWorld.ClientAndServer)]
    public class NetworkReceiveSystemGroup : ComponentSystemGroup
    {
    }

    [UpdateInGroup(typeof(NetworkReceiveSystemGroup))]
    [AlwaysUpdateSystem]
    public class NetworkStreamReceiveSystem : JobComponentSystem
    {
        public   ENetDriver            Driver           => m_Driver;
        internal JobHandle                   LastDriverWriter;

        public NetworkPipeline UnreliablePipeline => m_UnreliablePipeline;
        public NetworkPipeline ReliablePipeline   => m_ReliablePipeline;

        private ENetDriver m_Driver;
        private NetworkPipeline                          m_UnreliablePipeline;
        private NetworkPipeline                          m_ReliablePipeline;
        private bool                                     m_DriverListening;
        private NativeArray<int>                         numNetworkIds;
        private NativeQueue<int>                         freeNetworkIds;
        private BeginSimulationEntityCommandBufferSystem m_Barrier;
        private RpcQueue<RpcSetNetworkId>                rpcQueue;
        private int                                      m_ClientPacketDelay;
        private int                                      m_ClientPacketDrop;
        private EntityQuery                              m_NetworkStreamConnectionQuery;

        public bool Listen(Address endpoint)
        {
            LastDriverWriter.Complete();
            if (m_UnreliablePipeline == NetworkPipeline.Null)
                m_UnreliablePipeline = m_Driver.CreatePipeline(typeof(NullPipelineStage));
            if (m_ReliablePipeline == NetworkPipeline.Null)
                m_ReliablePipeline = m_Driver.CreatePipeline(typeof(ReliableSequencedPipelineStage));
            // Switching to server mode
            if (m_Driver.Bind(endpoint) != 0)
                return false;
            if (m_Driver.Listen() != 0)
                return false;
            m_DriverListening = true;
            return true;
        }

        public Entity Connect(Address endpoint)
        {
            LastDriverWriter.Complete();
            if (m_UnreliablePipeline == NetworkPipeline.Null)
            {
                if (m_ClientPacketDelay > 0 || m_ClientPacketDrop > 0)
                    m_UnreliablePipeline = m_Driver.CreatePipeline(typeof(SimulatorPipelineStage),
                        typeof(SimulatorPipelineStageInSend));
                else
                    m_UnreliablePipeline = m_Driver.CreatePipeline(typeof(NullPipelineStage));
            }

            if (m_ReliablePipeline == NetworkPipeline.Null)
            {
                if (m_ClientPacketDelay > 0 || m_ClientPacketDrop > 0)
                    m_ReliablePipeline = m_Driver.CreatePipeline(typeof(SimulatorPipelineStageInSend),
                        typeof(ReliableSequencedPipelineStage), typeof(SimulatorPipelineStage));
                else
                    m_ReliablePipeline = m_Driver.CreatePipeline(typeof(ReliableSequencedPipelineStage));
            }

            var ent = EntityManager.CreateEntity();
            EntityManager.AddComponentData(ent, new NetworkStreamConnection {Value = m_Driver.Connect(endpoint)});
            EntityManager.AddComponentData(ent, new NetworkSnapshotAckComponent());
            EntityManager.AddComponentData(ent, new CommandTargetComponent());
            EntityManager.AddBuffer<IncomingRpcDataStreamBufferComponent>(ent);
            EntityManager.AddBuffer<IncomingCommandDataStreamBufferComponent>(ent);
            EntityManager.AddBuffer<IncomingSnapshotDataStreamBufferComponent>(ent);
            return ent;
        }

#if UNITY_EDITOR
        private int ClientPacketDelayMs  => UnityEditor.EditorPrefs.GetInt($"MultiplayerPlayMode_{UnityEngine.Application.productName}_ClientDelay");
        private int ClientPacketJitterMs => UnityEditor.EditorPrefs.GetInt($"MultiplayerPlayMode_{UnityEngine.Application.productName}_ClientJitter");
        private int ClientPacketDropRate => UnityEditor.EditorPrefs.GetInt($"MultiplayerPlayMode_{UnityEngine.Application.productName}_ClientDropRate");
#elif DEVELOPMENT_BUILD
        public static int ClientPacketDelayMs = 0;
        public static int ClientPacketJitterMs = 0;
        public static int ClientPacketDropRate = 0;
#endif
        protected override void OnCreate()
        {
            if (!Library.Initialized)
            {
                if (!Library.Initialize())
                    throw new InvalidOperationException("Library not initialized");

                Application.quitting += Library.Deinitialize;
            }

            var reliabilityParams = new ReliableUtility.Parameters {WindowSize = 32};

#if UNITY_EDITOR || DEVELOPMENT_BUILD
            var netParams = new NetworkConfigParameter
            {
                maxConnectAttempts  = NetworkParameterConstants.MaxConnectAttempts,
                connectTimeoutMS    = NetworkParameterConstants.ConnectTimeoutMS,
                disconnectTimeoutMS = NetworkParameterConstants.DisconnectTimeoutMS,
                maxFrameTimeMS      = 100
            };

            m_ClientPacketDelay = ClientPacketDelayMs;
            var jitter = ClientPacketJitterMs;
            if (jitter > m_ClientPacketDelay)
                jitter = m_ClientPacketDelay;
            m_ClientPacketDrop = ClientPacketDropRate;
            int networkRate = 60; // TODO: read from some better place
            // All 3 packet types every frame stored for maximum delay, doubled for safety margin
            int maxPackets = 2 * (networkRate * 3 * m_ClientPacketDelay + 999) / 1000;
            var simulatorParams = new SimulatorUtility.Parameters
            {
                MaxPacketSize        = NetworkParameterConstants.MTU, MaxPacketCount = maxPackets,
                PacketDelayMs        = m_ClientPacketDelay, PacketJitterMs           = jitter,
                PacketDropPercentage = m_ClientPacketDrop
            };
            m_Driver = new ENetDriver(16);
            UnityEngine.Debug.Log($"Using simulator with latency={m_ClientPacketDelay} packet drop={m_ClientPacketDrop}");
#else
            m_Driver = new ENetDriver(16);
#endif
 
            m_UnreliablePipeline           = NetworkPipeline.Null;
            m_ReliablePipeline             = NetworkPipeline.Null;
            m_DriverListening              = false;
            m_Barrier                      = World.GetOrCreateSystem<BeginSimulationEntityCommandBufferSystem>();
            numNetworkIds                  = new NativeArray<int>(1, Allocator.Persistent);
            freeNetworkIds                 = new NativeQueue<int>(Allocator.Persistent);
            rpcQueue                       = World.GetOrCreateSystem<RpcSystem>().GetRpcQueue<RpcSetNetworkId>();
            m_NetworkStreamConnectionQuery = EntityManager.CreateEntityQuery(typeof(NetworkStreamConnection));
        }

        protected override void OnDestroy()
        {
            LastDriverWriter.Complete();
            numNetworkIds.Dispose();
            freeNetworkIds.Dispose();
            var driver = m_Driver;
            using (var networkStreamConnections = m_NetworkStreamConnectionQuery.ToComponentDataArray<NetworkStreamConnection>(Allocator.TempJob))
            {
                foreach (var connection in networkStreamConnections)
                {
                    driver.Disconnect(connection.Value);
                }
            }

            // TODO: can this be run without safety on the main thread?
            //            Entities.ForEach((ref NetworkStreamConnection connection) =>
            //            {
            //                driver.Disconnect(connection.Value);
            //            }).Run();
            m_Driver.Dispose();
        }

        struct ConnectionAcceptJob : IJob
        {
            public EntityCommandBuffer commandBuffer;
            public ENetDriver driver;

            public NativeArray<int>          numNetworkId;
            public NativeQueue<int>          freeNetworkIds;
            public RpcQueue<RpcSetNetworkId> rpcQueue;
            public ClientServerTickRate      tickRate;
            public NetworkProtocolVersion    protocolVersion;

            public void Execute()
            {
                NetworkConnection con;
                while ((con = driver.Accept()) != default(NetworkConnection))
                {
                    // New connection can never have any events, if this one does - just close it
                    DataStreamReader reader;
                    if (con.PopEvent(driver, out reader) != NetworkEvent.Type.Empty)
                    {
                        con.Disconnect(driver);
                        continue;
                    }

                    // create an entity for the new connection
                    var ent = commandBuffer.CreateEntity();
                    commandBuffer.AddComponent(ent, new NetworkStreamConnection {Value = con});
                    commandBuffer.AddComponent(ent, new NetworkSnapshotAckComponent());
                    commandBuffer.AddComponent(ent, new CommandTargetComponent());
                    commandBuffer.AddBuffer<IncomingRpcDataStreamBufferComponent>(ent);
                    var rpcBuffer = commandBuffer.AddBuffer<OutgoingRpcDataStreamBufferComponent>(ent);
                    commandBuffer.AddBuffer<IncomingCommandDataStreamBufferComponent>(ent);
                    commandBuffer.AddBuffer<IncomingSnapshotDataStreamBufferComponent>(ent);

                    RpcSystem.SendProtocolVersion(rpcBuffer, protocolVersion);

                    // Send RPC - assign network id
                    int nid;
                    if (!freeNetworkIds.TryDequeue(out nid))
                    {
                        // Avoid using 0
                        nid             = numNetworkId[0] + 1;
                        numNetworkId[0] = nid;
                    }

                    commandBuffer.AddComponent(ent, new NetworkIdComponent {Value = nid});
                    rpcQueue.Schedule(rpcBuffer, new RpcSetNetworkId
                    {
                        nid         = nid,
                        netTickRate = tickRate.NetworkTickRate,
                        simMaxSteps = tickRate.MaxSimulationStepsPerFrame,
                        simTickRate = tickRate.SimulationTickRate
                    });
                }
            }
        }

        [ExcludeComponent(typeof(OutgoingRpcDataStreamBufferComponent))]
        struct CompleteConnectionJob : IJobForEachWithEntity<NetworkStreamConnection>
        {
            public EntityCommandBuffer.ParallelWriter commandBuffer;
            public NetworkProtocolVersion         protocolVersion;

            public void Execute(Entity entity, int jobIndex, [ReadOnly] ref NetworkStreamConnection con)
            {
                var rpcBuffer = commandBuffer.AddBuffer<OutgoingRpcDataStreamBufferComponent>(jobIndex, entity);
                RpcSystem.SendProtocolVersion(rpcBuffer, protocolVersion);
            }
        }

        struct DisconnectJob : IJobForEachWithEntity<NetworkStreamConnection, NetworkStreamRequestDisconnect>
        {
            public EntityCommandBuffer.ParallelWriter commandBuffer;
            public ENetDriver driver;

            public void Execute(Entity entity, int jobIndex, ref NetworkStreamConnection connection, [ReadOnly] ref NetworkStreamRequestDisconnect disconnect)
            {
                driver.Disconnect(connection.Value);
                commandBuffer.AddComponent(jobIndex, entity, new NetworkStreamDisconnected {Reason = disconnect.Reason});
                commandBuffer.RemoveComponent<NetworkStreamRequestDisconnect>(jobIndex, entity);
            }
        }

        [ExcludeComponent(typeof(NetworkStreamDisconnected))]
        struct ConnectionReceiveJob : IJobForEachWithEntity<NetworkStreamConnection, NetworkSnapshotAckComponent>
        {
            public            EntityCommandBuffer.ParallelWriter                              commandBuffer;
            public ENetDriver driver;
            public            NativeQueue<int>.ParallelWriter                             freeNetworkIds;
            [ReadOnly] public ComponentDataFromEntity<NetworkIdComponent>                 networkId;
            public            BufferFromEntity<IncomingRpcDataStreamBufferComponent>      rpcBuffer;
            public            BufferFromEntity<IncomingCommandDataStreamBufferComponent>  cmdBuffer;
            public            BufferFromEntity<IncomingSnapshotDataStreamBufferComponent> snapshotBuffer;
            public            uint                                                        localTime;

            public unsafe void Execute(Entity                          entity, int index, ref NetworkStreamConnection connection,
                                       ref NetworkSnapshotAckComponent snapshotAck)
            {
                if (!connection.Value.IsCreated)
                    return;
                DataStreamReader  reader;
                NetworkEvent.Type evt;
                cmdBuffer[entity].Clear();
                while ((evt = driver.PopEventForConnection(connection.Value, out reader)) != NetworkEvent.Type.Empty)
                {
                    switch (evt)
                    {
                        case NetworkEvent.Type.Connect:
                            break;
                        case NetworkEvent.Type.Disconnect:
                            // Flag the connection as lost, it will be deleted in a separate system, giving user code one frame to detect and respond to lost connection
                            commandBuffer.AddComponent(index, entity, new NetworkStreamDisconnected
                            {
                                Reason = NetworkStreamDisconnectReason.ConnectionClose
                            });
                            rpcBuffer[entity].Clear();
                            cmdBuffer[entity].Clear();
                            connection.Value = default(NetworkConnection);
                            if (networkId.HasComponent(entity))
                                freeNetworkIds.Enqueue(networkId[entity].Value);
                            return;
                        case NetworkEvent.Type.Data:
                            // FIXME: do something with the data
                            var ctx = default(DataStreamReader.Context);
                            switch ((NetworkStreamProtocol) reader.ReadByte(ref ctx))
                            {
                                case NetworkStreamProtocol.Command:
                                {
                                    var buffer = cmdBuffer[entity];
                                    
                                    // FIXME: should be handle by a custom command stream system
                                    uint snapshot     = reader.ReadUInt(ref ctx);
                                    snapshotAck.UpdateReceivedByRemote(snapshot);
                                    uint remoteTime        = reader.ReadUInt(ref ctx);
                                    uint localTimeMinusRTT = reader.ReadUInt(ref ctx);
                                    snapshotAck.UpdateRemoteTime(remoteTime, localTimeMinusRTT, localTime);

                                    int headerSize = 1 + 4 * 3;

                                    buffer.ResizeUninitialized(reader.Length - headerSize);
                                    UnsafeUtility.MemCpy(buffer.GetUnsafePtr(),
                                        reader.GetUnsafeReadOnlyPtr() + headerSize,
                                        reader.Length - headerSize);
                                    break;
                                }
                                case NetworkStreamProtocol.Snapshot:
                                {
                                    uint remoteTime        = reader.ReadUInt(ref ctx);
                                    uint localTimeMinusRTT = reader.ReadUInt(ref ctx);
                                    snapshotAck.ServerCommandAge = reader.ReadInt(ref ctx);
                                    snapshotAck.UpdateRemoteTime(remoteTime, localTimeMinusRTT, localTime);
                                    int headerSize = 1 + 4 * 3;

                                    var temporaryArray = new NativeArray<byte>(reader.Length - headerSize, Allocator.Temp);
                                    UnsafeUtility.MemCpy(temporaryArray.GetUnsafePtr(), reader.GetUnsafeReadOnlyPtr() + headerSize, temporaryArray.Length);

                                    var buffer = snapshotBuffer[entity];
                                    buffer.Reinterpret<byte>()
                                          .AddRange(temporaryArray);
                                    break;
                                }
                                case NetworkStreamProtocol.Rpc:
                                {
                                    var buffer = rpcBuffer[entity];
                                    var oldLen = buffer.Length;
                                    buffer.ResizeUninitialized(oldLen + reader.Length - 1);
                                    UnsafeUtility.MemCpy(((byte*) buffer.GetUnsafePtr()) + oldLen,
                                        reader.GetUnsafeReadOnlyPtr() + 1,
                                        reader.Length - 1);
                                    break;
                                }
                                default:
#if ENABLE_UNITY_COLLECTIONS_CHECKS
                                    throw new InvalidOperationException("Received unknown message type");
#else
                                    break;
#endif
                            }

                            break;
                        default:
#if ENABLE_UNITY_COLLECTIONS_CHECKS
                            throw new InvalidOperationException("Received unknown network event " + evt);
#else
                            break;
#endif
                    }
                }
            }
        }

        protected override JobHandle OnUpdate(JobHandle inputDeps)
        {
            if (!HasSingleton<NetworkProtocolVersion>())
            {
                var entity      = EntityManager.CreateEntity();
                var rpcVersion  = World.GetExistingSystem<RpcSystem>().CalculateVersionHash();
                var gameVersion = HasSingleton<GameProtocolVersion>() ? GetSingleton<GameProtocolVersion>().Version : 0;
                EntityManager.AddComponentData(entity, new NetworkProtocolVersion
                {
                    NetCodeVersion       = NetworkProtocolVersion.k_NetCodeVersion,
                    GameVersion          = gameVersion,
                    RpcCollectionVersion = rpcVersion
                });
            }

            var concurrentFreeQueue = freeNetworkIds.AsParallelWriter();
            inputDeps = m_Driver.ScheduleUpdate(inputDeps);
            if (m_DriverListening)
            {
                // Schedule accept job
                var acceptJob = new ConnectionAcceptJob();
                acceptJob.driver         = m_Driver;
                acceptJob.commandBuffer  = m_Barrier.CreateCommandBuffer();
                acceptJob.numNetworkId   = numNetworkIds;
                acceptJob.freeNetworkIds = freeNetworkIds;
                acceptJob.rpcQueue       = rpcQueue;
                acceptJob.tickRate       = default(ClientServerTickRate);
                if (HasSingleton<ClientServerTickRate>())
                    acceptJob.tickRate = GetSingleton<ClientServerTickRate>();
                acceptJob.tickRate.ResolveDefaults();
                acceptJob.protocolVersion = GetSingleton<NetworkProtocolVersion>();
                inputDeps                 = acceptJob.Schedule(inputDeps);
            }
            else
            {
                freeNetworkIds.Clear();
            }

            var completeJob = new CompleteConnectionJob
            {
                commandBuffer   = m_Barrier.CreateCommandBuffer().AsParallelWriter(),
                protocolVersion = GetSingleton<NetworkProtocolVersion>()
            };
            inputDeps = completeJob.Schedule(this, inputDeps);

            inputDeps = JobHandle.CombineDependencies(inputDeps, LastDriverWriter);
            var disconnectJob = new DisconnectJob
            {
                commandBuffer = m_Barrier.CreateCommandBuffer().AsParallelWriter(),
                driver        = m_Driver
            };
            inputDeps = disconnectJob.ScheduleSingle(this, inputDeps);

            // Schedule parallel update job
            var recvJob = new ConnectionReceiveJob();
            recvJob.commandBuffer  = m_Barrier.CreateCommandBuffer().AsParallelWriter();
            recvJob.driver         = Driver;
            recvJob.freeNetworkIds = concurrentFreeQueue;
            recvJob.networkId      = GetComponentDataFromEntity<NetworkIdComponent>();
            recvJob.rpcBuffer      = GetBufferFromEntity<IncomingRpcDataStreamBufferComponent>();
            recvJob.cmdBuffer      = GetBufferFromEntity<IncomingCommandDataStreamBufferComponent>();
            recvJob.snapshotBuffer = GetBufferFromEntity<IncomingSnapshotDataStreamBufferComponent>();
            recvJob.localTime      = NetworkTimeSystem.TimestampMS;

            // FIXME: because it uses buffer from entity
            LastDriverWriter = recvJob.ScheduleSingle(this, inputDeps);
            m_Barrier.AddJobHandleForProducer(LastDriverWriter);
            return LastDriverWriter;
        }
    }
}