using ProtoBuf;

namespace RealisticSurveying.Network;

[ProtoContract]
public class MapStrokePacket
{
    [ProtoMember(1)] public int     ColorIndex;
    [ProtoMember(2)] public float   Width;
    [ProtoMember(3)] public float[] Points = null!;   // flat [dX,dZ, dX,dZ, …]
}
