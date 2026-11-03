using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace Qahwa.Core;

/// <summary>Said (400) when a protected backup is opened with the wrong password, or the file was changed or cut short.</summary>
public sealed class WrongBackupPasswordException()
    : QahwaException("The password is wrong, or the backup file is damaged.", 400, "wrong_password");

/// <summary>
/// Password protection for backup files. The key comes from the password with PBKDF2-SHA256 (600,000 rounds by default, a fresh random salt
/// per file) and the data is sealed with AES-256-GCM in 64 KB pieces, each with its own nonce and the whole header and an "is this the last
/// piece" flag as authenticated data, so a wrong password, a changed byte, a reordered or removed piece, or a file cut short are all refused
/// rather than read back wrong. File layout: <c>QBK1</c>, salt (16), rounds (4), nonce prefix (8), piece size (4), then pieces of
/// sealed data followed by a 16-byte tag.
/// </summary>
public static class BackupCrypto
{
    public static readonly byte[] Magic = "QBK1"u8.ToArray();
    public const int DefaultRounds = 600_000;
    public const int MaxRounds = 10_000_000;
    public const int MinPassword = 12;
    public const int MaxPassword = 200;
    public const int PieceSize = 64 * 1024;
    private const int SaltSize = 16, PrefixSize = 8, TagSize = 16, HeaderSize = 4 + SaltSize + 4 + PrefixSize + 4;

    /// <summary>Why a password cannot protect a backup, or null.</summary>
    public static string? PasswordProblem(string? password) =>
        string.IsNullOrEmpty(password) || password.Length < MinPassword || password.Length > MaxPassword || password.Any(char.IsControl)
            ? $"Use a password of {MinPassword} to {MaxPassword} characters."
            : null;

    /// <summary>True when the stream starts with the protected-backup marker. The position is put back.</summary>
    public static bool IsProtected(Stream stream)
    {
        var start = stream.Position;
        Span<byte> head = stackalloc byte[4];
        var read = stream.Read(head);
        stream.Position = start;
        return read == 4 && head.SequenceEqual(Magic);
    }

    private static byte[] Key(string password, byte[] salt, int rounds) =>
        Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(password), salt, rounds, HashAlgorithmName.SHA256, 32);

    private static byte[] Nonce(byte[] prefix, uint counter)
    {
        var nonce = new byte[12];
        prefix.CopyTo(nonce, 0);
        BinaryPrimitives.WriteUInt32BigEndian(nonce.AsSpan(8), counter);
        return nonce;
    }

    private static byte[] Associated(byte[] header, bool last) => [.. header, last ? (byte)1 : (byte)0];

    private static async Task<int> FillAsync(Stream stream, byte[] buffer, int count, CancellationToken ct)
    {
        var total = 0;
        while (total < count)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(total, count - total), ct);
            if (read == 0)
                break;
            total += read;
        }

        return total;
    }

    public static async Task EncryptAsync(Stream plain, Stream output, string password, CancellationToken ct = default, int rounds = DefaultRounds)
    {
        if (PasswordProblem(password) is { } problem)
            throw new DocumentValidationException([new FieldError("password", problem)]);
        var salt = RandomNumberGenerator.GetBytes(SaltSize);
        var prefix = RandomNumberGenerator.GetBytes(PrefixSize);
        var header = new byte[HeaderSize];
        Magic.CopyTo(header, 0);
        salt.CopyTo(header, 4);
        BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(4 + SaltSize), rounds);
        prefix.CopyTo(header, 4 + SaltSize + 4);
        BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(4 + SaltSize + 4 + PrefixSize), PieceSize);
        await output.WriteAsync(header, ct);

        using var aes = new AesGcm(Key(password, salt, rounds), TagSize);
        var current = new byte[PieceSize];
        var next = new byte[PieceSize];
        var have = await FillAsync(plain, current, PieceSize, ct);
        uint counter = 0;
        while (true)
        {
            var more = have == PieceSize ? await FillAsync(plain, next, PieceSize, ct) : 0;
            var last = more == 0;
            var sealedPiece = new byte[have];
            var tag = new byte[TagSize];
            aes.Encrypt(Nonce(prefix, counter++), current.AsSpan(0, have), sealedPiece, tag, Associated(header, last));
            await output.WriteAsync(sealedPiece, ct);
            await output.WriteAsync(tag, ct);
            if (last)
                break;
            (current, next) = (next, current);
            have = more;
        }
    }

    /// <exception cref="WrongBackupPasswordException">The password is wrong, or the file was changed or cut short.</exception>
    public static async Task DecryptAsync(Stream encrypted, Stream plainOutput, string password, CancellationToken ct = default)
    {
        var header = new byte[HeaderSize];
        if (await FillAsync(encrypted, header, HeaderSize, ct) != HeaderSize || !header.AsSpan(0, 4).SequenceEqual(Magic))
            throw new WrongBackupPasswordException();
        var rounds = BinaryPrimitives.ReadInt32BigEndian(header.AsSpan(4 + SaltSize));
        var pieceSize = BinaryPrimitives.ReadInt32BigEndian(header.AsSpan(4 + SaltSize + 4 + PrefixSize));
        if (rounds is < 1 or > MaxRounds || pieceSize != PieceSize || string.IsNullOrEmpty(password) || password.Length > MaxPassword)
            throw new WrongBackupPasswordException();
        var prefix = header.AsSpan(4 + SaltSize + 4, PrefixSize).ToArray();

        using var aes = new AesGcm(Key(password, header.AsSpan(4, SaltSize).ToArray(), rounds), TagSize);
        var current = new byte[PieceSize + TagSize];
        var next = new byte[PieceSize + TagSize];
        var have = await FillAsync(encrypted, current, current.Length, ct);
        uint counter = 0;
        while (true)
        {
            if (have < TagSize)
                throw new WrongBackupPasswordException();
            var more = have == current.Length ? await FillAsync(encrypted, next, next.Length, ct) : 0;
            var last = more == 0;
            var plain = new byte[have - TagSize];
            try
            {
                aes.Decrypt(Nonce(prefix, counter++), current.AsSpan(0, have - TagSize), current.AsSpan(have - TagSize, TagSize), plain, Associated(header, last));
            }
            catch (CryptographicException)
            {
                throw new WrongBackupPasswordException();
            }

            await plainOutput.WriteAsync(plain, ct);
            if (last)
                break;
            (current, next) = (next, current);
            have = more;
        }
    }
}
