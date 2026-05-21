using ProtoBuf;

namespace RealisticSurveying.Network;

[ProtoContract]
public class DeleteEdgePacket
{
    [ProtoMember(1)] public int EdgeIndex;
}
