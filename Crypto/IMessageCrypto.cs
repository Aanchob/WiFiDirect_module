namespace direct_module.Crypto
{
    public interface IMessageCrypto
    {
        byte[] Encrypt(byte[] plainBytes);

        byte[] Decrypt(byte[] encryptedBytes);
    }
}
