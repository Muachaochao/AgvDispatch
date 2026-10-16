namespace AgvDispatch.Application.Abstractions;

public sealed record MapCalibrationPoint(
    double CadX,
    double CadY,
    double MapX,
    double MapY);

public sealed record MapCalibrationSnapshot(
    double Scale,
    double RotationDegrees,
    double TranslationX,
    double TranslationY,
    double RootMeanSquareError,
    IReadOnlyList<MapCalibrationPoint> ControlPoints)
{
    public (double X, double Y) Transform(double cadX, double cadY)
    {
        var radians = RotationDegrees * Math.PI / 180d;
        var cosine = Math.Cos(radians);
        var sine = Math.Sin(radians);
        return (
            Scale * (cosine * cadX - sine * cadY) + TranslationX,
            Scale * (sine * cadX + cosine * cadY) + TranslationY);
    }
}

public interface IMapCalibrationService
{
    MapCalibrationSnapshot Calculate(IReadOnlyList<MapCalibrationPoint> points);
}
