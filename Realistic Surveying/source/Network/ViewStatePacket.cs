using ProtoBuf;

namespace RealisticSurveying;

[ProtoContract]
public class ViewStatePacket
{
    [ProtoMember(1)] public double Zoom;
    [ProtoMember(2)] public double PanX;
    [ProtoMember(3)] public double PanZ;
    [ProtoMember(4)] public bool ShowFaces;
    [ProtoMember(5)] public bool ShowEdges;
    [ProtoMember(6)] public bool ShowNodes;
    [ProtoMember(7)] public bool ShowLabels;
    [ProtoMember(8)] public bool ShowCoords;
}
