using System.Security.Cryptography;
using System.Text;
using Qahwa.Testing;
using static Qahwa.Testing.TestEngine;

namespace Qahwa.Core.Tests;

/// <summary>Password-protected backups: the file format and what it refuses.</summary>
public class BackupCryptoTests
{
    private const string Password = "a long enough passphrase";
    private const int Rounds = 1000; // fast for tests; the real default is far higher

    private static byte[] Bytes(int length) => RandomNumberGenerator.GetBytes(length);

    private static async Task<byte[]> SealAsync(byte[] plain, string password = Password)
    {
        using var output = new MemoryStream();
        await BackupCrypto.EncryptAsync(new MemoryStream(plain), output, password, rounds: Rounds);
        return output.ToArray();
    }

    private static async Task<byte[]> OpenAsync(byte[] sealedBytes, string password = Password)
    {
        using var output = new MemoryStream();
        await BackupCrypto.DecryptAsync(new MemoryStream(sealedBytes), output, password);
        return output.ToArray();
    }

    [Theory]
    [InlineData(1)]
    [InlineData(1000)]
    [InlineData(BackupCrypto.PieceSize - 1)]
    [InlineData(BackupCrypto.PieceSize)]
    [InlineData(BackupCrypto.PieceSize + 1)]
    [InlineData(BackupCrypto.PieceSize * 3)]
    [InlineData(BackupCrypto.PieceSize * 3 + 17)]
    public async Task Data_of_any_size_round_trips(int length)
    {
        var plain = Bytes(length);
        var sealedBytes = await SealAsync(plain);

        Assert.Equal(plain, await OpenAsync(sealedBytes));
        Assert.StartsWith("QBK1", Encoding.ASCII.GetString(sealedBytes, 0, 4));
        Assert.DoesNotContain(plain.Take(32).ToArray().ToHex(), sealedBytes.ToHex()); // not stored readable
    }

    [Fact]
    public async Task The_same_data_sealed_twice_looks_different_and_both_open()
    {
        var plain = Bytes(5000);
        var a = await SealAsync(plain);
        var b = await SealAsync(plain);
        Assert.NotEqual(a, b); // fresh salt and nonces
        Assert.Equal(plain, await OpenAsync(a));
        Assert.Equal(plain, await OpenAsync(b));
    }

    [Fact]
    public async Task A_wrong_password_a_changed_byte_a_cut_file_or_removed_piece_are_all_refused()
    {
        var plain = Bytes(BackupCrypto.PieceSize * 3 + 5);
        var sealedBytes = await SealAsync(plain);
        var pieceLength = BackupCrypto.PieceSize + 16;
        var headerLength = sealedBytes.Length - (pieceLength * 3 + 5 + 16);

        await Assert.ThrowsAsync<WrongBackupPasswordException>(() => OpenAsync(sealedBytes, "another passphrase!!"));

        var flipped = (byte[])sealedBytes.Clone();
        flipped[headerLength + 100] ^= 1;
        await Assert.ThrowsAsync<WrongBackupPasswordException>(() => OpenAsync(flipped));

        var header = (byte[])sealedBytes.Clone();
        header[10] ^= 1; // the salt: part of the key and of the authenticated header
        await Assert.ThrowsAsync<WrongBackupPasswordException>(() => OpenAsync(header));

        await Assert.ThrowsAsync<WrongBackupPasswordException>(() => OpenAsync(sealedBytes[..^1])); // cut short by one byte
        await Assert.ThrowsAsync<WrongBackupPasswordException>(() => OpenAsync(sealedBytes[..(headerLength + pieceLength * 2)])); // the last pieces dropped
        var swapped = (byte[])sealedBytes.Clone();
        Array.Copy(sealedBytes, headerLength + pieceLength, swapped, headerLength, pieceLength);
        Array.Copy(sealedBytes, headerLength, swapped, headerLength + pieceLength, pieceLength); // two pieces exchanged
        await Assert.ThrowsAsync<WrongBackupPasswordException>(() => OpenAsync(swapped));
        await Assert.ThrowsAsync<WrongBackupPasswordException>(() => OpenAsync([1, 2, 3])); // not a protected file at all
    }

    [Fact]
    public async Task A_file_cannot_ask_for_an_absurd_number_of_rounds()
    {
        var sealedBytes = await SealAsync(Bytes(10));
        System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(sealedBytes.AsSpan(4 + 16), int.MaxValue);
        await Assert.ThrowsAsync<WrongBackupPasswordException>(() => OpenAsync(sealedBytes));
    }

    [Theory]
    [InlineData("")]
    [InlineData("short")]
    [InlineData("has a\ncontrol character!")]
    public async Task A_weak_password_is_refused_when_sealing(string password) =>
        await Assert.ThrowsAsync<DocumentValidationException>(() => SealAsync(Bytes(10), password));

    [Fact]
    public async Task A_protected_backup_restores_with_the_password_and_never_without_it()
    {
        var source = new TestEngine();
        var target = new TestEngine();
        foreach (var engine in new[] { source, target })
            engine.Registry.Add(DocTypeLoader.Parse("""{ "name": "Memo", "autoname": "M-.####", "fields": [ { "name": "text", "type": "Data" } ], "permissions": [ { "role": "Clerk", "read": true, "create": true } ] }"""));
        await source.Documents.InsertAsync("Memo", Values(("text", "secret plans")), TestUsers.Admin);

        var file = new MemoryStream();
        await source.Backup.WriteProtectedAsync(file, TestUsers.Admin, Password, rounds: Rounds);
        Assert.DoesNotContain("secret plans", Encoding.UTF8.GetString(file.ToArray())); // not readable in the file

        file.Position = 0;
        var noPassword = await Assert.ThrowsAsync<DocumentValidationException>(() => target.Backup.RestoreAsync(file, dryRun: false, TestUsers.Admin));
        Assert.Contains(noPassword.Errors, e => e.Field == "password");
        file.Position = 0;
        await Assert.ThrowsAsync<WrongBackupPasswordException>(() => target.Backup.RestoreAsync(file, dryRun: false, TestUsers.Admin, password: "not the right one!!"));
        Assert.Equal(0, (await target.Documents.ListAsync("Memo", new ListRequest { Limit = 1 }, TestUsers.Admin)).Total);

        file.Position = 0;
        var report = await target.Backup.RestoreAsync(file, dryRun: false, TestUsers.Admin, password: Password);
        Assert.Equal(1, report.Tables.Single(t => t.DocType == "Memo").Inserted);
        Assert.Equal("secret plans", (await target.Documents.ListAsync("Memo", new ListRequest { Fields = ["text"], Limit = 5 }, TestUsers.Admin)).Data.Single()["text"]);
    }

    [Fact]
    public async Task A_scheduled_backup_with_a_password_is_a_qbk_file_that_opens_only_with_it_and_old_ones_of_each_kind_are_pruned_separately()
    {
        var engine = new TestEngine();
        var folder = Path.Combine(Path.GetTempPath(), "qahwa-bk-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(folder);
            await File.WriteAllTextAsync(Path.Combine(folder, "qahwa-backup-20200101.zip"), "an old plain backup");
            await File.WriteAllTextAsync(Path.Combine(folder, "qahwa-backup-20200101.qbk"), "an old protected one");

            var made = await engine.Backup.RunScheduledAsync(folder, keep: 1, TestUsers.Admin, password: Password);

            Assert.EndsWith(".qbk", made);
            Assert.Null(await engine.Backup.RunScheduledAsync(folder, keep: 1, TestUsers.Admin, password: Password)); // one a day
            Assert.False(File.Exists(Path.Combine(folder, "qahwa-backup-20200101.qbk"))); // pruned among the protected ones
            Assert.True(File.Exists(Path.Combine(folder, "qahwa-backup-20200101.zip"))); // the plain ones are not touched by a protected run

            await using var stream = File.OpenRead(made!);
            Assert.True(BackupCrypto.IsProtected(stream));
            Assert.NotNull(await engine.Backup.RestoreAsync(stream, dryRun: true, TestUsers.Admin, password: Password));
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public async Task Only_an_administrator_can_make_a_protected_backup_and_a_plain_one_still_restores_without_a_password()
    {
        var engine = new TestEngine();
        await Assert.ThrowsAsync<PermissionDeniedException>(() => engine.Backup.WriteProtectedAsync(new MemoryStream(), new UserContext("a@example.com", ["Clerk"]), Password, rounds: Rounds));

        var plain = new MemoryStream();
        await engine.Backup.WriteAsync(plain, TestUsers.Admin);
        plain.Position = 0;
        Assert.False(BackupCrypto.IsProtected(plain));
        Assert.NotNull(await engine.Backup.RestoreAsync(plain, dryRun: true, TestUsers.Admin, password: "ignored when the file is not protected"));
    }
}

internal static class HexExtensions
{
    public static string ToHex(this byte[] bytes) => Convert.ToHexString(bytes);
}
