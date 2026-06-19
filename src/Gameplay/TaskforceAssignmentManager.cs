using SeaPower;
using SeapowerMultiplayer.Messages;

namespace SeapowerMultiplayer
{
    public enum ControlRole : byte
    {
        Any = 0,
        Surface = 1,
        Submarine = 2,
        Air = 3,
        Land = 4,
    }

    public static class TaskforceAssignmentManager
    {
        private static ControlRole _explicitRole = ControlRole.Any;

        public static ControlRole LocalRole
        {
            get
            {
                var slot = FourPlayerLobby.FindByPlayer(FourPlayerLobby.LocalPlayerId);
                return slot != null ? (ControlRole)slot.Role : _explicitRole;
            }
        }

        public static void HostAssign(byte playerId, ControlRole role)
        {
            var slot = FourPlayerLobby.FindByPlayer(playerId);
            if (slot == null) return;
            slot.Role = (byte)role;
            if (slot.PeerId > 0)
            {
                NetworkManager.Instance.SendToClient(slot.PeerId, new GameEventMessage
                {
                    EventType = GameEventType.ControlRoleAssigned,
                    Param = (float)(byte)role,
                });
            }
            NetworkManager.Instance.BroadcastRoster();
        }

        public static void OnAssignmentReceived(float param)
        {
            _explicitRole = (ControlRole)(byte)param;
            Plugin.Log.LogInfo($"[Ownership] Local control role: {_explicitRole}");
        }

        public static bool ClientMayControl(ObjectBase unit)
        {
            if (Plugin.Instance.CfgIsHost.Value) return true;
            if (unit == null || unit._taskforce == null) return false;
            if (FourPlayerLobby.LocalTeam == 2)
                return unit._taskforce == Globals._neutralTaskforce
                       && MatchesRole(LocalRole, unit);
            if (unit._taskforce != Globals._playerTaskforce) return false;
            if (MatchesRole(LocalRole, unit)) return true;
            return LocalRole == ControlRole.Air
                   && unit is Vessel
                   && unit.GetComponentInChildren<FlightDeck>() != null;
        }

        public static bool HostPeerMayControl(int peerId, ObjectBase? unit, OrderType order)
        {
            var slot = FourPlayerLobby.FindByPeer(peerId);
            if (slot == null || unit == null || unit._taskforce == null) return false;

            if (slot.Team == 2)
            {
                return unit._taskforce == Globals._neutralTaskforce
                       && NeutralOrderPolicy.IsMovementOnlyOrder(order)
                       && MatchesRole((ControlRole)slot.Role, unit);
            }

            byte unitTeam;
            if (unit._taskforce == Globals._playerTaskforce)
                unitTeam = FourPlayerLobby.HostTeam;
            else if (unit._taskforce == Globals._enemyTaskforce)
                unitTeam = FourPlayerLobby.HostTeam == 0 ? (byte)1 : (byte)0;
            else
                return false;

            if (slot.Team != unitTeam) return false;
            var role = (ControlRole)slot.Role;
            if (MatchesRole(role, unit)) return true;
            return role == ControlRole.Air
                   && order == OrderType.LaunchAircraft
                   && unit is Vessel;
        }

        public static bool LocalMayNavigateNeutral(ObjectBase? unit) =>
            !Plugin.Instance.CfgIsHost.Value
            && FourPlayerLobby.LocalTeam == 2
            && unit != null
            && unit._taskforce == Globals._neutralTaskforce;

        public static string RoleName(byte role)
            => ((ControlRole)role).ToString();

        private static bool MatchesRole(ControlRole role, ObjectBase unit) => role switch
        {
            ControlRole.Any => true,
            ControlRole.Surface => unit is Vessel,
            ControlRole.Submarine => unit is Submarine,
            ControlRole.Air => unit is Aircraft || unit is Helicopter,
            ControlRole.Land => unit is LandUnit,
            _ => false,
        };

        public static void Reset()
        {
            _explicitRole = ControlRole.Any;
        }
    }
}
