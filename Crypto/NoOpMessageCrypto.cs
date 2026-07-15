namespace direct_module.Crypto
{
    internal sealed class NoOpMessageCrypto : IMessageCrypto
    {
        public byte[] Encrypt(byte[] plainBytes)
        {
            return plainBytes;
        }

        public byte[] Decrypt(byte[] encryptedBytes)
        {
            return encryptedBytes;
        }
    }
}
