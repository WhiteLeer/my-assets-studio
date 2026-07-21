namespace AnimeStudio
{
    public sealed class ParticleSystemRenderer : Renderer
    {
        public PPtr<Mesh>[] m_Meshes;
        public int m_BytesReadBeforeTail;
        public int m_UnparsedTailBytes;

        public ParticleSystemRenderer(ObjectReader reader) : base(reader)
        {
            m_BytesReadBeforeTail = (int)(reader.Position - reader.byteStart);
            m_Meshes = ReadMeshPointersFromEnd(reader);
            m_UnparsedTailBytes = reader.BytesLeft() - 52;
        }

        private static PPtr<Mesh>[] ReadMeshPointersFromEnd(ObjectReader reader)
        {
            var position = reader.Position;
            try
            {
                reader.Position = reader.byteStart + reader.byteSize - 52;
                var meshes = new[]
                {
                    new PPtr<Mesh>(reader),
                    new PPtr<Mesh>(reader),
                    new PPtr<Mesh>(reader),
                    new PPtr<Mesh>(reader),
                };
                return meshes;
            }
            finally
            {
                reader.Position = position;
            }
        }
    }
}
