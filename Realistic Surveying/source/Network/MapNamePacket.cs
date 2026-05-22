using ProtoBuf;

namespace RealisticSurveying;

[ProtoContract]
public class MapNamePacket
{
    [ProtoMember(1)] public string Name = "";
}
