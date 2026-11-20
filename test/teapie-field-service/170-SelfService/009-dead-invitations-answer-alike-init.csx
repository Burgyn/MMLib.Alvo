using System.Security.Cryptography;

// A code Alvo never issued, shaped exactly like one it did: the prefix and 16 random bytes in base64url (22 characters).
tp.SetVariable(
    "UnknownInviteCode",
    "alvo_inv_" + Convert.ToBase64String(RandomNumberGenerator.GetBytes(16)).TrimEnd('=').Replace('+', '-').Replace('/', '_'));
