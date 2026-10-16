namespace AgvDispatch.Application.Abstractions;

public sealed record CloudPoint(double X,double Y,double Z);
public sealed record NativeMap3DInfo(string MapName,string FormatVersion,double Resolution,
    CloudPoint HeaderMin,CloudPoint HeaderMax,string CloudSha256,string SchemaSha256,
    bool FeatureMapPresent,int FeaturePlaneEnvelopes,int SkippedFields)
{
    public string? PackageMapName {get;init;}
    public string AssociationSummary {get;init;}="独立二进制，未提供同包二维导航。";
    public bool CoordinateAlignmentVerified=>false;
}
/// <summary>Read-only point cloud. Native decoding does not imply device validation or alignment.</summary>
public sealed record PointCloudData(string Source,string Sha256,IReadOnlyList<CloudPoint> Points,
    CloudPoint Min,CloudPoint Max)
{
    public NativeMap3DInfo? NativeMap {get;init;}
    public string Unit=>"m (input declaration)";
    public bool DeviceValidated=>false;
}
