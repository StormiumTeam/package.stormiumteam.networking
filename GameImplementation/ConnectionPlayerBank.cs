﻿using System.Collections.Generic;
using package.stormiumteam.networking;
using package.stormiumteam.networking.game;
using package.stormiumteam.shared;
using package.stormiumteam.shared.online;
using Unity.Entities;

namespace GameImplementation
{
    public class ConnectionPlayerBank : NetworkConnectionSystem
    {
        private Dictionary<int, GamePlayer> m_AllPlayers = new Dictionary<int, GamePlayer>();
        
        protected override void OnUpdate()
        {
            
        }

        protected override void OnDestroyManager()
        {
            m_AllPlayers.Clear();
            m_AllPlayers = null;
        }

        public void RegisterPlayer(int index, GamePlayer player)
        {
            m_AllPlayers[index] = player;

            player.WorldPointer.SetOrAddComponentData(new ClientPlayerServerPlayerLink(index));
        }

        public void UnregisterPlayer(int index)
        {
            if (m_AllPlayers.ContainsKey(index))
            {
                var player = m_AllPlayers[index];
                player.WorldPointer.RemoveComponentIfExist<ClientPlayerServerPlayerLink>();
                
                m_AllPlayers[index] = new GamePlayer();
            }
        }

        public GamePlayer Get(int index)
        {
            return m_AllPlayers.ContainsKey(index) ? m_AllPlayers[index] : new GamePlayer();
        }
    }
}