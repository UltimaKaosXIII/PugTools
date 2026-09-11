using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace GomLib
{
    public class DomField : DomType
    {
        public GomType GomType { get; internal set; }

        // Raw field metadata from client.gom.  Modifiers are intentionally kept as a
        // numeric bit field until all replication/storage flags are understood.
        public ushort Modifiers { get; internal set; }
        public ushort SerializedTypeLength { get; internal set; }
        public ushort SerializedTypeOffset { get; internal set; }

        public override void Print(System.IO.TextWriter writer)
        {
            writer.WriteLine("Field: {0} - {2} - 0x{1:X}", Name, Id, GomType);
            if (!string.IsNullOrEmpty(Description)) writer.WriteLine("\t{0}", Description);
        }

        public override void Link(DataObjectModel dom)
        {
            Dom_ = dom;
            GomType.Link(dom);
        }

        public bool ConfirmType(GomBinaryReader reader)
        {
            // Confirm the next bytes from the GomBinaryReader match this field's type
            return GomType.ConfirmType(reader);
        }
    }
}
