using System.Collections.Generic;
using SeaPower;
using SeapowerMultiplayer.Messages;

namespace SeapowerMultiplayer
{
    public static class UnitLockManager
    {
        private static readonly Dictionary<byte, int> _remoteLocks = new();
        private static int _localControlledUnitId;

        public static int LocalControlledUnitId => _localControlledUnitId;

        public static void SetLocalControlled(int unitId) => _localControlledUnitId = unitId;
        public static void ClearLocalControlled() => _localControlledUnitId = 0;

        public static void OnRemoteSelected(byte playerId, int unitId)
        {
            int previous = _remoteLocks.TryGetValue(playerId, out int old) ? old : 0;
            _remoteLocks[playerId] = unitId;

            if (previous != 0 && previous != unitId)
                MapUnitViewModelRegistry.NotifyLockChanged(previous);
            if (unitId != 0)
                MapUnitViewModelRegistry.NotifyLockChanged(unitId);

            TryAutoClaim(previous, unitId);
        }

        public static void OnRemoteDeselected(byte playerId)
        {
            int released = _remoteLocks.TryGetValue(playerId, out int unitId) ? unitId : 0;
            _remoteLocks.Remove(playerId);
            if (released != 0)
                MapUnitViewModelRegistry.NotifyLockChanged(released);
            TryAutoClaim(released, 0);
        }

        private static void TryAutoClaim(int releasedRemoteId, int newRemoteId)
        {
            if (releasedRemoteId == 0 || releasedRemoteId == newRemoteId) return;
            if (_localControlledUnitId == releasedRemoteId || IsLockedByRemote(releasedRemoteId)) return;

            var selected = Singleton<RenderPosition>.Instance?.SelectedObject;
            if (selected == null || selected.UniqueID != releasedRemoteId) return;

            NetworkManager.Instance.SendToOther(new GameEventMessage
            {
                EventType = GameEventType.UnitSelected,
                SourceEntityId = FourPlayerLobby.LocalPlayerId,
                Param = releasedRemoteId,
            });
            _localControlledUnitId = releasedRemoteId;
        }

        public static bool IsLockedByRemote(int unitId)
        {
            foreach (int locked in _remoteLocks.Values)
                if (locked == unitId)
                    return true;
            return false;
        }

        public static void Reset()
        {
            foreach (int released in _remoteLocks.Values)
                if (released != 0)
                    MapUnitViewModelRegistry.NotifyLockChanged(released);
            _remoteLocks.Clear();
            _localControlledUnitId = 0;
        }
    }
}
