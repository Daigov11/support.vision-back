using System.Security.Cryptography;
using System.Text;

namespace VisionSupport.Server.Services;

/// <summary>
/// Genera y valida códigos de emparejamiento de un solo uso (ver Models/PairingCode.cs y
/// Controllers/PairingCodesController.cs/DevicesController.Enroll). El código nunca se guarda
/// ni se registra en texto plano: solo su hash SHA-256 (mismo esquema que TokenService con los
/// refresh tokens).
/// </summary>
public static class PairingCodeService
{
    /// <summary>
    /// Alfabeto de 32 símbolos sin caracteres ambiguos (sin 0/O ni 1/I), para que 256 % 32 == 0
    /// y el mapeo byte→símbolo no tenga sesgo de módulo.
    /// </summary>
    private const string Alphabet = "23456789ABCDEFGHJKLMNPQRSTUVWXYZ";

    /// <summary>10 símbolos de 32 = 50 bits de entropía; de un solo uso y expira en 10 minutos.</summary>
    private const int CodeLength = 10;

    public static TimeSpan Lifetime => TimeSpan.FromMinutes(10);

    /// <summary>Código en texto plano, sin formato (10 caracteres, sin separadores).</summary>
    public static string GenerateCode()
    {
        var bytes = RandomNumberGenerator.GetBytes(CodeLength);
        var chars = new char[CodeLength];
        for (var i = 0; i < CodeLength; i++)
        {
            chars[i] = Alphabet[bytes[i] % Alphabet.Length];
        }
        return new string(chars);
    }

    /// <summary>"ABCDE-FGHJK": el guion es solo para legibilidad, se ignora al validar.</summary>
    public static string FormatForDisplay(string code) =>
        code.Length == CodeLength ? $"{code[..5]}-{code[5..]}" : code;

    /// <summary>
    /// Normaliza espacios/guiones/mayúsculas antes de hashear, para que dé igual cómo el usuario
    /// lo copie o lo escriba a mano en Android.
    /// </summary>
    public static string Normalize(string rawCode) =>
        new string(rawCode.Where(c => !char.IsWhiteSpace(c) && c != '-').ToArray()).ToUpperInvariant();

    /// <summary>SHA-256 en hex del código ya normalizado: lo único que se persiste.</summary>
    public static string Hash(string rawCode) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Normalize(rawCode))));
}
