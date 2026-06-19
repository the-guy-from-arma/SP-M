using SeapowerMultiplayer.Messages;

namespace SeapowerMultiplayer
{
    public static class NeutralOrderPolicy
    {
        public static bool IsMovementOnlyOrder(OrderType order) => order switch
        {
            OrderType.SetSpeed => true,
            OrderType.MoveTo => true,
            OrderType.Stop => true,
            OrderType.ClearOrders => true,
            OrderType.RemoveWaypoints => true,
            OrderType.DeleteWaypoint => true,
            OrderType.EditWaypoint => true,
            _ => false,
        };
    }
}
