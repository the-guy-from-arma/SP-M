using HarmonyLib;
using SeaPower;

namespace SeapowerMultiplayer
{
    /// <summary>
    /// Temporarily hands neutral taskforce navigation to the third player.
    /// Combat authority is still rejected separately by HostPeerMayControl.
    /// When a fourth player joins (or the neutral navigator leaves), the original
    /// mission AI setting is restored.
    /// </summary>
    public static class NeutralNavigationManager
    {
        private static Taskforce? _trackedTaskforce;
        private static bool _originalAiControlled;
        private static readonly System.Reflection.FieldInfo _aiControlledField =
            AccessTools.Field(typeof(Taskforce), "<IsAIControlled>k__BackingField");

        public static bool HasNeutralNavigator
        {
            get
            {
                foreach (var slot in FourPlayerLobby.Slots)
                    if (slot.Connected && slot.Team == 2)
                        return true;
                return false;
            }
        }

        public static void RefreshHostPolicy()
        {
            if (!Plugin.Instance.CfgIsHost.Value)
                return;

            var neutral = Globals._neutralTaskforce;
            if (neutral == null)
                return;

            if (_trackedTaskforce != neutral)
            {
                RestoreTrackedTaskforce();
                _trackedTaskforce = neutral;
                _originalAiControlled = neutral.IsAIControlled;
            }

            bool navigationDelegated = Plugin.Instance.CfgPvP.Value && HasNeutralNavigator;
            SetAiControlled(neutral, navigationDelegated ? false : _originalAiControlled);
            Plugin.Log.LogInfo(navigationDelegated
                ? "[Neutral] Third player navigation enabled; neutral taskforce AI paused."
                : "[Neutral] Neutral taskforce returned to mission AI.");
        }

        public static void Reset()
        {
            RestoreTrackedTaskforce();
            _trackedTaskforce = null;
            _originalAiControlled = false;
        }

        private static void RestoreTrackedTaskforce()
        {
            if (_trackedTaskforce != null)
                SetAiControlled(_trackedTaskforce, _originalAiControlled);
        }

        private static void SetAiControlled(Taskforce taskforce, bool value) =>
            _aiControlledField?.SetValue(taskforce, value);
    }
}
