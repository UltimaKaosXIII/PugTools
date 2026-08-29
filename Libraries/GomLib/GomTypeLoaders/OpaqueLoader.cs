namespace GomLib.GomTypeLoaders
{
    class OpaqueLoader : IGomTypeLoader
    {
        private readonly GomTypeId typeId;
        public OpaqueLoader(GomTypeId typeId) { this.typeId = typeId; }
        public GomTypeId SupportedType => typeId;
        public GomType Load(GomBinaryReader reader, bool fromGom, DataObjectModel dom) => new GomTypes.Opaque(typeId);
    }
}
