using System.Linq;

namespace AnimeStudio
{
    public sealed class ParticleSystemRenderer : Renderer
    {
        public PPtr<Mesh>[] m_Meshes;
        public int m_BytesReadBeforeTail;
        public int m_UnparsedTailBytes;
        public bool m_RendererPrefixParsed;
        public bool m_RendererTailParsed;
        public byte[] m_UnknownTailBytes = System.Array.Empty<byte>();
        public ushort m_RenderMode;
        public ushort m_SortMode;
        public ushort m_OrderType;
        public ushort m_LodLevel;
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
        public bool m_UseOctagonShape;
        public bool m_SkipAutoScalingOpt;
        public float m_OctagonExpand;
        public int m_AlphaThresholdParticles;
        public byte[] m_VertexStreams;
        public PPtr<Mesh> m_Mesh;
        public PPtr<Mesh> m_Mesh1;
        public PPtr<Mesh> m_Mesh2;
        public PPtr<Mesh> m_Mesh3;
        public PPtr<Mesh> m_OctagonMesh;
        public int m_MaskInteraction;

        public ParticleSystemRenderer(ObjectReader reader) : base(reader)
        {
            m_BytesReadBeforeTail = (int)(reader.Position - reader.byteStart);
            m_Meshes = System.Array.Empty<PPtr<Mesh>>();
            m_RendererPrefixParsed = TryReadRendererPrefix(reader);
            if (m_RendererPrefixParsed)
                TryReadRendererTail(reader);
            m_Meshes = new[] { m_Mesh, m_Mesh1, m_Mesh2, m_Mesh3, m_OctagonMesh }
                .Where(pointer => pointer != null && !pointer.IsNull)
                .ToArray();
            m_UnparsedTailBytes = reader.BytesLeft();
            if (m_UnparsedTailBytes > 0)
                m_UnknownTailBytes = reader.ReadBytes(m_UnparsedTailBytes);
        }

        private void TryReadRendererTail(ObjectReader reader)
        {
            var position = reader.Position;
            try
            {
                m_UseOctagonShape = ReadBoolean(reader);
                m_SkipAutoScalingOpt = ReadBoolean(reader);
                // Unity aligns this field because the TypeTree marks the
                // following scalar as aligned.
                reader.AlignStream();
                m_OctagonExpand = ReadFinite(reader);
                m_AlphaThresholdParticles = reader.ReadInt32();
                var vertexStreamSize = reader.ReadInt32();
                if (vertexStreamSize < 0 || vertexStreamSize > reader.BytesLeft())
                    throw new System.IO.InvalidDataException("Invalid particle vertex stream size.");
                m_VertexStreams = reader.ReadBytes(vertexStreamSize);
                reader.AlignStream();
                m_Mesh = new PPtr<Mesh>(reader);
                m_Mesh1 = new PPtr<Mesh>(reader);
                m_Mesh2 = new PPtr<Mesh>(reader);
                m_Mesh3 = new PPtr<Mesh>(reader);
                m_OctagonMesh = new PPtr<Mesh>(reader);
                m_MaskInteraction = reader.ReadInt32();
                m_VertexStreams ??= System.Array.Empty<byte>();
                m_RendererTailParsed = true;
            }
            catch
            {
                reader.Position = position;
                m_RendererTailParsed = false;
                m_VertexStreams = System.Array.Empty<byte>();
                m_UnknownTailBytes = System.Array.Empty<byte>();
                m_Mesh = null;
                m_Mesh1 = null;
                m_Mesh2 = null;
                m_Mesh3 = null;
                m_OctagonMesh = null;
            }
        }

        private bool TryReadRendererPrefix(ObjectReader reader)
        {
            var position = reader.Position;
            try
            {
                // Unity 2019 serializes the mode pair before the renderer floats.
                m_RenderMode = reader.ReadUInt16();
                m_SortMode = reader.ReadUInt16();
                if (reader.Game.Type.IsZZZ())
                {
                    m_OrderType = reader.ReadUInt16();
                    m_LodLevel = reader.ReadUInt16();
                }
                // ZZZ adds renderer modes beyond Unity's stock 0..5 range.
                if (m_RenderMode > (reader.Game.Type.IsZZZ() ? 6 : 5) || m_SortMode > 4)
                    throw new System.IO.InvalidDataException("Invalid particle renderer mode prefix.");

                m_MinParticleSize = ReadFinite(reader);
                m_MaxParticleSize = ReadFinite(reader);
                if (m_MinParticleSize < 0f || m_MaxParticleSize < 0f || m_MaxParticleSize < m_MinParticleSize)
                    throw new System.IO.InvalidDataException(
                        $"Invalid particle size range [{m_MinParticleSize}, {m_MaxParticleSize}].");

                m_CameraVelocityScale = ReadFinite(reader);
                m_VelocityScale = ReadFinite(reader);
                m_LengthScale = ReadFinite(reader);
                m_SortingFudge = ReadFinite(reader);
                m_NormalDirection = ReadFinite(reader);
                m_ShadowBias = ReadFinite(reader);
                m_RenderAlignment = reader.ReadInt32();
                if (m_RenderAlignment < 0 || m_RenderAlignment > 5)
                    throw new System.IO.InvalidDataException($"Invalid particle render alignment {m_RenderAlignment}.");
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

    }
}
