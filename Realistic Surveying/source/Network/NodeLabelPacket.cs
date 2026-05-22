using ProtoBuf;

namespace RealisticSurveying;

[ProtoContract]
public class NodeLabelPacket
{
    [ProtoMember(1)] public int    NodeIndex;
    [ProtoMember(2)] public string Label = "";
}
