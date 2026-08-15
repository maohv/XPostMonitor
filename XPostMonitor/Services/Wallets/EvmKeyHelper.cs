using Nethereum.Signer;

namespace XPostMonitor.Services.Wallets;

// Chỉ tạo/đọc khóa EVM. Không biết DB, GMGN, tạo token hay NFT.
public static class EvmKeyHelper
{
    public static EvmWalletCredentials Create()
    {
        EthECKey key = EthECKey.GenerateKey();
        return ToCredentials(key);
    }

    public static EvmWalletCredentials Read(string privateKey)
    {
        string clean = privateKey.Trim();
        if (clean.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) clean = clean[2..];
        if (clean.Length != 64 || !clean.All(Uri.IsHexDigit))
            throw new ArgumentException("Private key EVM không hợp lệ.", nameof(privateKey));

        try { return ToCredentials(new EthECKey(clean)); }
        catch (Exception exception)
        {
            throw new ArgumentException("Private key EVM không hợp lệ.", nameof(privateKey), exception);
        }
    }

    private static EvmWalletCredentials ToCredentials(EthECKey key)
    {
        string privateKey = key.GetPrivateKey();
        if (!privateKey.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) privateKey = "0x" + privateKey;
        return new EvmWalletCredentials(key.GetPublicAddress(), privateKey);
    }
}
