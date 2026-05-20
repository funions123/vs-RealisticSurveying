using ProtoBuf;

namespace RealisticSurveying.Network;

[ProtoContract]
public class DeleteNodePacket
{
    [ProtoMember(1)] public int NodeIndex;
}
