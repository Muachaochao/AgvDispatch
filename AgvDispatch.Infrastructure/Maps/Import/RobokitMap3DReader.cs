using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using AgvDispatch.Application.Abstractions;
namespace AgvDispatch.Infrastructure.Maps;

/// <summary>Bounded read-only projection of the supplied Message_Map3D schema, ordinary points only.</summary>
public sealed class RobokitMap3DReader
{
    public const string SchemaSha256="82CC9690A75B3500371EDABFE4509069578DDDE9A5661B28264C8B512758595C";
    public const int MaxBytes=192*1024*1024;
    public const int MaxPoints=1_000_000;
    private sealed class Budget(CancellationToken ct){public int Fields,Skipped;public void Tick(){ct.ThrowIfCancellationRequested();if(++Fields>10_000_000)throw new InvalidDataException("Protobuf字段数超出预算。");}}
    private ref struct Wire(ReadOnlySpan<byte> bytes,Budget budget)
    {
        private ReadOnlySpan<byte> remaining=bytes;
        public bool Next(out int field,out int type)
        {
            field=type=0;if(remaining.Length==0)return false;budget.Tick();var key=Varint();
            if(key>uint.MaxValue||(key>>3)==0)throw new InvalidDataException("无效Protobuf字段编号。");field=(int)(key>>3);type=(int)(key&7);return true;
        }
        public ulong Varint()
        {
            ulong value=0;for(int i=0;i<10;i++){if(remaining.IsEmpty)throw new InvalidDataException("Protobuf varint截断。");var b=remaining[0];remaining=remaining[1..];if(i==9&&b>1)throw new InvalidDataException("Protobuf varint溢出。");value|=(ulong)(b&127)<<(i*7);if(b<128)return value;}throw new InvalidDataException("Protobuf varint过长。");
        }
        public ReadOnlySpan<byte> Block(int type){Expect(type,2);var n=Varint();if(n>(ulong)remaining.Length)throw new InvalidDataException("Protobuf消息长度截断或越界。");var block=remaining[..(int)n];remaining=remaining[(int)n..];return block;}
        public double Double(int type){Expect(type,1);if(remaining.Length<8)throw new InvalidDataException("Protobuf double截断。");var n=BinaryPrimitives.ReadDoubleLittleEndian(remaining);remaining=remaining[8..];return n;}
        public string Text(int type){var block=Block(type);if(block.Length>1024)throw new InvalidDataException("地图字符串超过预算。");var s=new UTF8Encoding(false,true).GetString(block);if(s.Any(char.IsControl))throw new InvalidDataException("地图字符串含控制字符。");return s;}
        public void Skip(int type)
        {
            budget.Skipped++;switch(type){case 0:Varint();return;case 1:Take(8);return;case 2:Block(2);return;case 5:Take(4);return;default:throw new NotSupportedException("未支持的Protobuf wire type："+type);}
        }
        private void Take(int n){if(remaining.Length<n)throw new InvalidDataException("Protobuf固定字段截断。");remaining=remaining[n..];}
        public static void Expect(int actual,int expected){if(actual!=expected)throw new InvalidDataException("Protobuf已知字段wire type不符。");}
    }
    private sealed class Header
    {
        public string Type="",Name="",Version="";public double Resolution;public CloudPoint Min=new(0,0,0),Max=new(0,0,0);public bool HasMin,HasMax;
    }
    private static CloudPoint Point(ReadOnlySpan<byte> bytes,Budget budget,CloudPoint? previous=null)
    {
        var wire=new Wire(bytes,budget);double x=previous?.X??0,y=previous?.Y??0,z=previous?.Z??0;
        while(wire.Next(out var field,out var type)){switch(field){case 1:x=wire.Double(type);break;case 2:y=wire.Double(type);break;case 3:z=wire.Double(type);break;default:wire.Skip(type);break;}}
        if(!double.IsFinite(x)||!double.IsFinite(y)||!double.IsFinite(z)||Math.Abs(x)>1e6||Math.Abs(y)>1e6||Math.Abs(z)>1e6)throw new InvalidDataException("三维坐标不是预算内有限数字。");return new(x,y,z);
    }
    private static void ReadHeader(ReadOnlySpan<byte> bytes,Budget budget,Header header)
    {
        var wire=new Wire(bytes,budget);while(wire.Next(out var field,out var type)){switch(field){
            case 1:header.Type=wire.Text(type);break;case 2:header.Name=wire.Text(type);break;
            case 3:header.Min=Point(wire.Block(type),budget,header.Min);header.HasMin=true;break;
            case 4:header.Max=Point(wire.Block(type),budget,header.Max);header.HasMax=true;break;
            case 5:header.Resolution=wire.Double(type);break;case 8:header.Version=wire.Text(type);break;
            default:wire.Skip(type);break;}}
    }
    public PointCloudData Read(byte[] bytes,string source,CancellationToken ct=default)
    {
        ct.ThrowIfCancellationRequested();if(bytes.Length==0||bytes.Length>MaxBytes)throw new InvalidDataException("三维Protobuf为空或超过192MiB。");
        var budget=new Budget(ct);var wire=new Wire(bytes,budget);var header=new Header();var points=new List<CloudPoint>();var feature=false;var planes=0;
        while(wire.Next(out var field,out var type)){switch(field){
            case 1:wire.Text(type);break;
            case 2:ReadHeader(wire.Block(type),budget,header);break;
            case 3:if(points.Count>=MaxPoints)throw new InvalidDataException("普通三维点云超过100万点。");points.Add(Point(wire.Block(type),budget));break;
            case 4:
                feature=true;var featureWire=new Wire(wire.Block(type),budget);
                while(featureWire.Next(out var f,out var t)){if(f==2){featureWire.Block(t);if(++planes>1_000_000)throw new InvalidDataException("特征平面条目超出预算。");}else featureWire.Skip(t);}break;
            default:wire.Skip(type);break;}}
        if(header.Type!="3D-Map"||string.IsNullOrWhiteSpace(header.Name)||string.IsNullOrWhiteSpace(header.Version)||!header.HasMin||!header.HasMax||!double.IsFinite(header.Resolution)||header.Resolution<=0||header.Resolution>1e6||header.Min.X>header.Max.X||header.Min.Y>header.Max.Y||header.Min.Z>header.Max.Z)throw new InvalidDataException("三维header类型/名称/版本/边界/分辨率不完整或无效。");
        if(points.Count==0)throw new NotSupportedException(feature?"该地图没有normal_pos3d_list普通点，仅含特征地图；尚未将特征平面/八叉树转换成点云。":"三维地图没有normal_pos3d_list普通点。");
        var tolerance=Math.Max(.001,header.Resolution);
        if(points.Any(p=>p.X<header.Min.X-tolerance||p.X>header.Max.X+tolerance||p.Y<header.Min.Y-tolerance||p.Y>header.Max.Y+tolerance||p.Z<header.Min.Z-tolerance||p.Z>header.Max.Z+tolerance))throw new InvalidDataException("普通点云超出header声明边界。");
        var sha=Convert.ToHexString(SHA256.HashData(bytes));
        return new(source,sha,points.AsReadOnly(),new(points.Min(p=>p.X),points.Min(p=>p.Y),points.Min(p=>p.Z)),new(points.Max(p=>p.X),points.Max(p=>p.Y),points.Max(p=>p.Z)))
        {NativeMap=new(header.Name,header.Version,header.Resolution,header.Min,header.Max,sha,SchemaSha256,feature,planes,budget.Skipped)};
    }
}
