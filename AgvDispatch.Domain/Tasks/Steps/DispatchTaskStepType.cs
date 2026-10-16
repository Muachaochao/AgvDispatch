namespace AgvDispatch.Domain.Tasks;

public enum DispatchTaskStepType
{
    GoToPickup = 10,
    PickupFullBox = 20,
    DeliverFullBoxToDumper = 30,
    WaitForDumperReady = 40,
    EnterDumpingPosition = 50,
    DumpFullBox = 60,
    ReturnEmptyBox = 70,
    PlaceEmptyBox = 80,
    CompletePlcHandshake = 90
}
