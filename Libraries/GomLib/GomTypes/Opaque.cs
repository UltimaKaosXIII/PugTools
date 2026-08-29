using System;

namespace GomLib.GomTypes
{
    /// <summary>
    /// DOM types whose schema declaration has no trailing payload, but whose instance-data
    /// encoding is not used/decoded by Jedipedia's generic NODE reader either.  Recognising
    /// them keeps client.gom aligned; if actual stored data is encountered, fail at that field
    /// rather than consuming guessed bytes and corrupting the rest of the node.
    /// </summary>
    public sealed class Opaque : GomType
    {
        public Opaque(GomTypeId typeId) : base(typeId) { }
        public override object ReadData(DataObjectModel dom, GomBinaryReader reader)
            => throw new NotSupportedException("Stored NODE data for GomType " + TypeId + " is not implemented.");
        public override string ToString() => TypeId.ToString();
    }
}
