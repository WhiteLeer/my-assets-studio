using System;
using System.Runtime.InteropServices;
using AnimeStudio.PInvoke;

namespace ACLLibs
{
    public struct DecompressedClip
    {
        public IntPtr Values;
        public int ValuesCount;
        public IntPtr Times;
        public int TimesCount;
    }
    public static class ACL
    {
        private const string DLL_NAME = "acl";
        static ACL()
        {
            DllLoader.PreloadDll(DLL_NAME);
        }
        public static void DecompressAll(byte[] data, out float[] values, out float[] times)
        {
            var decompressedClip = new DecompressedClip();
            DecompressAll(data, ref decompressedClip);

            values = new float[decompressedClip.ValuesCount];
            Marshal.Copy(decompressedClip.Values, values, 0, decompressedClip.ValuesCount);

            times = new float[decompressedClip.TimesCount];
            Marshal.Copy(decompressedClip.Times, times, 0, decompressedClip.TimesCount);

            Dispose(ref decompressedClip);
        }

        #region importfunctions

        [DllImport(DLL_NAME, CallingConvention = CallingConvention.Cdecl)]
        private static extern void DecompressAll(byte[] data, ref DecompressedClip decompressedClip);

        [DllImport(DLL_NAME, CallingConvention = CallingConvention.Cdecl)]
        private static extern void Dispose(ref DecompressedClip decompressedClip);

        #endregion
    }

    public static class SRACL
    {
        private const string DLL_NAME = "sracl";
        static SRACL()
        {
            DllLoader.PreloadDll(DLL_NAME);
        }
        public static void DecompressAll(byte[] data, out float[] values, out float[] times)
        {
            var decompressedClip = new DecompressedClip();
            DecompressAll(data, ref decompressedClip);

            values = new float[decompressedClip.ValuesCount];
            Marshal.Copy(decompressedClip.Values, values, 0, decompressedClip.ValuesCount);

            times = new float[decompressedClip.TimesCount];
            Marshal.Copy(decompressedClip.Times, times, 0, decompressedClip.TimesCount);

            Dispose(ref decompressedClip);
        }

        #region importfunctions

        [DllImport(DLL_NAME, CallingConvention = CallingConvention.Cdecl)]
        private static extern void DecompressAll(byte[] data, ref DecompressedClip decompressedClip);

        [DllImport(DLL_NAME, CallingConvention = CallingConvention.Cdecl)]
        private static extern void Dispose(ref DecompressedClip decompressedClip);

        #endregion
    }

    public static class DBACL
    {
        private const string DLL_NAME = "acldb";
        private const string DLL_NAME_ZZZ = "acldb_zzz";
        static DBACL()
        {
            DllLoader.PreloadDll(DLL_NAME);
            DllLoader.PreloadDll(DLL_NAME_ZZZ);
        }
        public static void DecompressTracks(byte[] data, byte[] db, out float[] values, out float[] times, bool isZZZ = false)
        {
            var decompressedClip = new DecompressedClip();

            var dataPtr = Marshal.AllocHGlobal(data.Length + 8);
            var dataAligned = new IntPtr(16 * (((long)dataPtr + 15) / 16));
            Marshal.Copy(data, 0, dataPtr, data.Length);

            var dbPtr = Marshal.AllocHGlobal(db.Length + 8);
            var dbAligned = new IntPtr(16 * (((long)dbPtr + 15) / 16));
            Marshal.Copy(db, 0, dbAligned, db.Length);

            // as long as m_ClipData is passed to acl_db.dll without the rest it should be fine
            // m_databaseData doesn't seem to be used. For now
            if (isZZZ)
            {
                var streamer = new IntPtr(0);
                DecompressTracksZZZ(dataAligned, dbAligned, streamer, ref decompressedClip);
            }
            else
            {
                DecompressTracks(dataAligned, dbAligned, ref decompressedClip);
            }

            Marshal.FreeHGlobal(dataPtr);
            Marshal.FreeHGlobal(dbPtr);

            values = new float[decompressedClip.ValuesCount];
            Marshal.Copy(decompressedClip.Values, values, 0, decompressedClip.ValuesCount);

            times = new float[decompressedClip.TimesCount];
            Marshal.Copy(decompressedClip.Times, times, 0, decompressedClip.TimesCount);

            if (isZZZ)
            {
                DisposeZZZ(ref decompressedClip);
            }
            else
            {
                Dispose(ref decompressedClip);
            }
        }

        #region importfunctions

        [DllImport(DLL_NAME, CallingConvention = CallingConvention.Cdecl)]
        private static extern void DecompressTracks(nint data, nint db, ref DecompressedClip decompressedClip);

        [DllImport(DLL_NAME, CallingConvention = CallingConvention.Cdecl)]
        private static extern void Dispose(ref DecompressedClip decompressedClip);

        [DllImport(DLL_NAME_ZZZ, CallingConvention = CallingConvention.Cdecl, EntryPoint = "DecompressTracks")]
        private static extern void DecompressTracksZZZ(nint data, nint db, nint streamer, ref DecompressedClip decompressedClip);

        [DllImport(DLL_NAME_ZZZ, CallingConvention = CallingConvention.Cdecl, EntryPoint = "Dispose")]
        private static extern void DisposeZZZ(ref DecompressedClip decompressedClip);

        #endregion
    }
}
