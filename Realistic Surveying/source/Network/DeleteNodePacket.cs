using ProtoBuf;

namespace RealisticSurveying;

[ProtoContract]
public class DeleteNodePacket
{
    [ProtoMember(1)] public int NodeIndex;
}
