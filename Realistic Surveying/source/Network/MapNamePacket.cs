using ProtoBuf;

namespace RealisticSurveying.Network;

[ProtoContract]
public class MapNamePacket
{
    [ProtoMember(1)] public string Name = "";
}
