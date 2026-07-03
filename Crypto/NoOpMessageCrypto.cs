namespace direct_module.Crypto
{
    public class NoOpMessageCrypto : IMessageCrypto
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
