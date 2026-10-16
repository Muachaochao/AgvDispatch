namespace AgvDispatch.Domain.Tasks;

public enum DispatchTaskStage
{
    Created = 0,
    WaitingForVehicle = 10,
    SubmittingToAgv = 15,
    GoingToPickup = 20,
    PickingUp = 30,
    GoingToDumper = 40,
    WaitingForDumper = 50,
    Dumping = 60,
    ReturningEmptyBox = 70,
    WaitingForPlcConfirmation = 80,
    Completed = 90,
    Faulted = 100,
    Cancelled = 110,
    CancellationPending = 120,
    CancellationReview = 130
}
