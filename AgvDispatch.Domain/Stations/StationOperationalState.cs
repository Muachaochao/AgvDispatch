namespace AgvDispatch.Domain.Stations;

public enum StationOperationalState
{
    Unknown = 0,
    Offline = 10,
    Idle = 20,
    FullBoxWaitingForPickup = 30,
    PickupInProgress = 40,
    WaitingForEmptyBox = 50,
    EmptyBoxPlacementInProgress = 60,
    Completed = 70,
    Faulted = 80,
    SignalConflict = 90,
    ManualIntervention = 100
}
