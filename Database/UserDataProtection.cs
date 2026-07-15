using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace direct_module.Database
{
    /// <summary>
    /// Protects user data with Windows DPAPI. The ciphertext is tied to the
    /// current Windows user and cannot be moved to another account.
    /// </summary>
    internal static class UserDataProtection
    {
        private const string ProtectedPrefix = "dpapi:v1:";
        private const uint CryptProtectUiForbidden = 0x1;
        private const int MaxPlaintextBytes = 8 * 1024 * 1024;
        private const int MaxProtectedValueCharacters = 16 * 1024 * 1024;
        private const int MaxDpapiBlobBytes = 12 * 1024 * 1024;
        private static readonly UTF8Encoding StrictUtf8 = new(false, true);
        private static readonly byte[] OptionalEntropy =
            Encoding.UTF8.GetBytes("Aanchob.WiFiDirect_module.user-data.v1");

        public static bool IsProtected(string? value) =>
            value?.StartsWith(ProtectedPrefix, StringComparison.Ordinal) == true;

        public static string ProtectString(string value)
        {
            ArgumentNullException.ThrowIfNull(value);
            if (value.Length == 0)
            {
                return ProtectedPrefix;
            }

            if (StrictUtf8.GetByteCount(value) > MaxPlaintextBytes)
            {
                throw new CryptographicException("The value is too large to protect safely.");
            }

            byte[] plaintext = StrictUtf8.GetBytes(value);
            try
            {
                byte[] protectedBytes = Protect(plaintext);
                try
                {
                    if (protectedBytes.Length == 0)
                    {
                        throw new CryptographicException("DPAPI returned an empty protected value.");
                    }
                    return ProtectedPrefix + Convert.ToBase64String(protectedBytes);
                }
                finally
                {
                    CryptographicOperations.ZeroMemory(protectedBytes);
                }
            }
            finally
            {
                CryptographicOperations.ZeroMemory(plaintext);
            }
        }

        public static string ProtectBytesToString(ReadOnlySpan<byte> value)
        {
            if (value.Length > MaxPlaintextBytes)
            {
                throw new CryptographicException("The binary value is too large to protect safely.");
            }

            if (value.Length == 0)
            {
                return ProtectedPrefix;
            }

            byte[] copy = value.ToArray();
            try
            {
                byte[] protectedBytes = Protect(copy);
                try
                {
                    if (protectedBytes.Length == 0)
                    {
                        throw new CryptographicException("DPAPI returned an empty protected binary value.");
                    }
                    return ProtectedPrefix + Convert.ToBase64String(protectedBytes);
                }
                finally
                {
                    CryptographicOperations.ZeroMemory(protectedBytes);
                }
            }
            finally
            {
                CryptographicOperations.ZeroMemory(copy);
            }
        }

        public static string UnprotectString(string value)
        {
            ArgumentNullException.ThrowIfNull(value);
            if (!IsProtected(value))
            {
                // Compatibility with databases created before encryption was enabled.
                return value;
            }

            string encoded = value[ProtectedPrefix.Length..];
            if (encoded.Length > MaxProtectedValueCharacters)
            {
                throw new CryptographicException("The protected value exceeds its safety limit.");
            }

            if (encoded.Length == 0)
            {
                return "";
            }

            byte[] protectedBytes;
            try
            {
                protectedBytes = Convert.FromBase64String(encoded);
            }
            catch (FormatException ex)
            {
                throw new CryptographicException("The protected value is malformed.", ex);
            }

            try
            {
                byte[] plaintext = Unprotect(protectedBytes);
                try
                {
                    try
                    {
                        return StrictUtf8.GetString(plaintext);
                    }
                    catch (DecoderFallbackException ex)
                    {
                        throw new CryptographicException("The protected value is not valid UTF-8.", ex);
                    }
                }
                finally
                {
                    CryptographicOperations.ZeroMemory(plaintext);
                }
            }
            finally
            {
                CryptographicOperations.ZeroMemory(protectedBytes);
            }
        }

        public static byte[] UnprotectBytesFromString(string value)
        {
            ArgumentNullException.ThrowIfNull(value);
            if (!IsProtected(value))
            {
                throw new CryptographicException("The binary value is not DPAPI protected.");
            }

            string encoded = value[ProtectedPrefix.Length..];
            if (encoded.Length > MaxProtectedValueCharacters)
            {
                throw new CryptographicException("The protected binary value exceeds its safety limit.");
            }

            if (encoded.Length == 0)
            {
                return Array.Empty<byte>();
            }

            byte[] protectedBytes;
            try
            {
                protectedBytes = Convert.FromBase64String(encoded);
            }
            catch (FormatException ex)
            {
                throw new CryptographicException("The protected binary value is malformed.", ex);
            }
            try
            {
                return Unprotect(protectedBytes);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(protectedBytes);
            }
        }

        private static byte[] Protect(byte[] plaintext)
        {
            DataBlob input = CreateBlob(plaintext);
            DataBlob entropy = CreateBlob(OptionalEntropy);
            try
            {
                if (!CryptProtectData(
                        ref input,
                        "WiFiDirect_module user data",
                        ref entropy,
                        IntPtr.Zero,
                        IntPtr.Zero,
                        CryptProtectUiForbidden,
                        out DataBlob output))
                {
                    throw CreateWin32CryptographicException("DPAPI protection failed");
                }

                try
                {
                    return CopyBlob(output);
                }
                finally
                {
                    if (output.Data != IntPtr.Zero)
                    {
                        try
                        {
                            ZeroBlob(output);
                        }
                        finally
                        {
                            LocalFree(output.Data);
                        }
                    }
                }
            }
            finally
            {
                FreeBlob(input);
                FreeBlob(entropy);
            }
        }

        private static byte[] Unprotect(byte[] protectedBytes)
        {
            DataBlob input = CreateBlob(protectedBytes);
            DataBlob entropy = CreateBlob(OptionalEntropy);
            try
            {
                if (!CryptUnprotectData(
                        ref input,
                        IntPtr.Zero,
                        ref entropy,
                        IntPtr.Zero,
                        IntPtr.Zero,
                        CryptProtectUiForbidden,
                        out DataBlob output))
                {
                    throw CreateWin32CryptographicException("DPAPI unprotection failed");
                }

                try
                {
                    return CopyBlob(output);
                }
                finally
                {
                    if (output.Data != IntPtr.Zero)
                    {
                        try
                        {
                            ZeroBlob(output);
                        }
                        finally
                        {
                            LocalFree(output.Data);
                        }
                    }
                }
            }
            finally
            {
                FreeBlob(input);
                FreeBlob(entropy);
            }
        }

        private static DataBlob CreateBlob(byte[] data)
        {
            if (data.Length == 0)
            {
                return default;
            }

            IntPtr pointer = Marshal.AllocHGlobal(data.Length);
            Marshal.Copy(data, 0, pointer, data.Length);
            return new DataBlob { Length = data.Length, Data = pointer };
        }

        private static byte[] CopyBlob(DataBlob blob)
        {
            if (blob.Length < 0 || (blob.Length > 0 && blob.Data == IntPtr.Zero))
            {
                throw new CryptographicException("DPAPI returned an invalid data blob.");
            }
            if (blob.Length == 0)
            {
                return Array.Empty<byte>();
            }

            if (blob.Length > MaxDpapiBlobBytes)
            {
                throw new CryptographicException("DPAPI returned a value that exceeds the safety limit.");
            }

            byte[] value = new byte[blob.Length];
            Marshal.Copy(blob.Data, value, 0, blob.Length);
            return value;
        }

        private static void FreeBlob(DataBlob blob)
        {
            if (blob.Data == IntPtr.Zero)
            {
                return;
            }

            try
            {
                ZeroBlob(blob);
            }
            finally
            {
                Marshal.FreeHGlobal(blob.Data);
            }
        }

        private static void ZeroBlob(DataBlob blob)
        {
            if (blob.Data == IntPtr.Zero || blob.Length <= 0)
            {
                return;
            }

            byte[] zeros = new byte[Math.Min(blob.Length, 16 * 1024)];
            int offset = 0;
            while (offset < blob.Length)
            {
                int count = Math.Min(zeros.Length, blob.Length - offset);
                Marshal.Copy(zeros, 0, IntPtr.Add(blob.Data, offset), count);
                offset += count;
            }
        }

        private static CryptographicException CreateWin32CryptographicException(string operation)
        {
            int error = Marshal.GetLastWin32Error();
            return new CryptographicException($"{operation}: {new Win32Exception(error).Message} (0x{error:X8})");
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct DataBlob
        {
            public int Length;
            public IntPtr Data;
        }

        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [DllImport("Crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CryptProtectData(
            ref DataBlob dataIn,
            string? description,
            ref DataBlob optionalEntropy,
            IntPtr reserved,
            IntPtr promptStruct,
            uint flags,
            out DataBlob dataOut);

        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [DllImport("Crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CryptUnprotectData(
            ref DataBlob dataIn,
            IntPtr description,
            ref DataBlob optionalEntropy,
            IntPtr reserved,
            IntPtr promptStruct,
            uint flags,
            out DataBlob dataOut);

        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [DllImport("Kernel32.dll")]
        private static extern IntPtr LocalFree(IntPtr memory);
    }
}
