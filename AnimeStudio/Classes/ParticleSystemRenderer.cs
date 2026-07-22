namespace AnimeStudio
{
    public sealed class ParticleSystemRenderer : Renderer
    {
        public PPtr<Mesh>[] m_Meshes;
        public int m_BytesReadBeforeTail;
        public int m_UnparsedTailBytes;
        public bool m_RendererPrefixParsed;
        public ushort m_RenderMode;
        public ushort m_SortMode;
        public float m_MinParticleSize;
        public float m_MaxParticleSize;
        public float m_CameraVelocityScale;
        public float m_VelocityScale;
        public float m_LengthScale;
        public float m_SortingFudge;
        public float m_NormalDirection;
        public float m_ShadowBias;
        public int m_RenderAlignment;
        public Vector3 m_Pivot;
        public Vector3 m_Flip;
        public bool m_UseCustomVertexStreams;
        public bool m_EnableGPUInstancing;
        public bool m_ApplyActiveColorSpace;
        public bool m_AllowRoll;

        public ParticleSystemRenderer(ObjectReader reader) : base(reader)
        {
            m_BytesReadBeforeTail = (int)(reader.Position - reader.byteStart);
            m_RendererPrefixParsed = TryReadRendererPrefix(reader);
            m_Meshes = ReadMeshPointersFromEnd(reader);
            m_UnparsedTailBytes = reader.BytesLeft() - 52;
        }

        private bool TryReadRendererPrefix(ObjectReader reader)
        {
            var position = reader.Position;
            try
            {
                m_RenderMode = reader.ReadUInt16();
                m_SortMode = reader.ReadUInt16();
                if (m_RenderMode > 5 || m_SortMode > 4)
                    throw new System.IO.InvalidDataException("Invalid particle renderer mode prefix.");
                m_MinParticleSize = ReadFinite(reader);
                m_MaxParticleSize = ReadFinite(reader);
                m_CameraVelocityScale = ReadFinite(reader);
                m_VelocityScale = ReadFinite(reader);
                m_LengthScale = ReadFinite(reader);
                m_SortingFudge = ReadFinite(reader);
                m_NormalDirection = ReadFinite(reader);
                m_ShadowBias = ReadFinite(reader);
                m_RenderAlignment = reader.ReadInt32();
                m_Pivot = reader.ReadVector3();
                m_Flip = reader.ReadVector3();
                m_UseCustomVertexStreams = ReadBoolean(reader);
                m_EnableGPUInstancing = ReadBoolean(reader);
                m_ApplyActiveColorSpace = ReadBoolean(reader);
                m_AllowRoll = ReadBoolean(reader);
                return true;
            }
            catch
            {
                reader.Position = position;
                return false;
            }
        }

        private static float ReadFinite(ObjectReader reader)
        {
            var value = reader.ReadSingle();
            if (float.IsNaN(value) || float.IsInfinity(value) || System.Math.Abs(value) > 100000f)
                throw new System.IO.InvalidDataException("Invalid particle renderer float prefix.");
            return value;
        }

        private static bool ReadBoolean(ObjectReader reader)
        {
            var value = reader.ReadByte();
            if (value > 1)
                throw new System.IO.InvalidDataException("Invalid particle renderer Boolean prefix.");
            return value != 0;
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
