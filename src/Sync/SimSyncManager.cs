using System.Collections.Generic;
using SeaPower;

namespace SeapowerMultiplayer
{
    public enum SimState
    {
        Idle,
        WaitingForClient,
        Synchronized,
    }

    public static class SimSyncManager
    {
        private static readonly HashSet<int> _waitingPeers = new();
        private static readonly HashSet<int> _readyPeers = new();
        private static SimState _currentState = SimState.Idle;

        public static SimState CurrentState
        {
            get => _currentState;
            set
            {
                if (_currentState == value) return;
                Plugin.Log.LogInfo($"[SimSync] State transition: {_currentState} -> {value}");
                _currentState = value;
            }
        }

        public static int ReadyClientCount => _readyPeers.Count;
        public static int WaitingClientCount => _waitingPeers.Count;
        public static bool BothSidesReady => AllClientsReady;
        public static bool AllClientsReady =>
            _waitingPeers.Count > 0 && _readyPeers.IsSupersetOf(_waitingPeers);

        public static void Reset()
        {
            _waitingPeers.Clear();
            _readyPeers.Clear();
            CurrentState = SimState.Idle;
        }

        public static void BeginWaitingForClients()
        {
            _waitingPeers.Clear();
            _readyPeers.Clear();
            foreach (int peerId in NetworkManager.Instance.EstablishedPeerIds)
                _waitingPeers.Add(peerId);
            CurrentState = _waitingPeers.Count > 0 ? SimState.WaitingForClient : SimState.Idle;
        }

        public static void BeginWaitingForPeer(int peerId)
        {
            _waitingPeers.Add(peerId);
            _readyPeers.Remove(peerId);
            CurrentState = SimState.WaitingForClient;
        }

        public static void OnClientReady(int peerId, bool ready)
        {
            if (ready) _readyPeers.Add(peerId);
            else _readyPeers.Remove(peerId);

            if (AllClientsReady)
            {
                CurrentState = SimState.Synchronized;
                Plugin.Log.LogInfo($"[SimSync] All {_waitingPeers.Count} expected clients ready - paused={GameTime.IsPaused()}, TC={GameTime.TimeCompression}");
            }
            else
            {
                CurrentState = SimState.WaitingForClient;
                Plugin.Log.LogInfo($"[SimSync] Ready clients: {_readyPeers.Count}/{_waitingPeers.Count}");
            }
        }

        public static void OnClientDisconnected(int peerId)
        {
            _readyPeers.Remove(peerId);
            _waitingPeers.Remove(peerId);
            if (_waitingPeers.Count == 0)
                CurrentState = SimState.Idle;
            else if (AllClientsReady)
                CurrentState = SimState.Synchronized;
        }
    }
}
