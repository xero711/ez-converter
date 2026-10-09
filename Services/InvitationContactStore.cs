using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace MediaConverter.Services;

/// <summary>A locally saved invitation. ToString intentionally omits the bearer code.</summary>
public sealed record InvitationContact(string DisplayName, string RegistrationCode)
{
    public override string ToString() => DisplayName;
}

/// <summary>
/// Stores invitation contacts in a caller-selected file. The complete document, including
/// display names and bearer codes, is protected with current-user Windows DPAPI.
/// </summary>
public sealed class InvitationContactStore
{
    private const string FilePrefix = "EZCONTACTS1:";
    private const int MaximumContacts = 500;
    private const int MaximumDisplayNameLength = 100;
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = false };
    private readonly object _sync = new();

    public InvitationContactStore(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath))
        {
            throw new ArgumentException("連絡先ファイルの保存先を指定してください。", nameof(filePath));
        }

        FilePath = Path.GetFullPath(filePath);
    }

    public string FilePath { get; }

    public static string DefaultFilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "EZConverter", "invitation-contacts.dat");

    public IReadOnlyList<InvitationContact> Load()
    {
        lock (_sync)
        {
            return LoadCore();
        }
    }

    public void Save(IEnumerable<InvitationContact> contacts)
    {
        ArgumentNullException.ThrowIfNull(contacts);
        lock (_sync)
        {
            SaveCore(NormalizeContacts(contacts));
        }
    }

    /// <summary>Adds or updates a contact by its exact EZC1 code.</summary>
    public void Upsert(string displayName, string registrationCode)
    {
        var contact = NormalizeContact(displayName, registrationCode);
        lock (_sync)
        {
            var contacts = LoadCore().ToList();
            var index = contacts.FindIndex(existing =>
                string.Equals(existing.RegistrationCode, contact.RegistrationCode, StringComparison.Ordinal));
            if (index >= 0)
            {
                contacts[index] = contact;
            }
            else
            {
                contacts.Add(contact);
            }

            SaveCore(NormalizeContacts(contacts));
        }
    }

    /// <summary>Removes the contact matching an exact EZC1 code and reports whether it existed.</summary>
    public bool Remove(string registrationCode)
    {
        if (!InvitationCodeService.TryRestore(registrationCode, out var restoredUrl))
        {
            throw new ArgumentException("有効なEZC1登録コードを指定してください。", nameof(registrationCode));
        }

        var canonicalCode = InvitationCodeService.CreateStructuralCode(restoredUrl);
        lock (_sync)
        {
            var contacts = LoadCore().ToList();
            var removed = contacts.RemoveAll(contact =>
                string.Equals(contact.RegistrationCode, canonicalCode, StringComparison.Ordinal)) > 0;
            if (removed)
            {
                SaveCore(contacts);
            }

            return removed;
        }
    }

    private IReadOnlyList<InvitationContact> LoadCore()
    {
        if (!File.Exists(FilePath))
        {
            return Array.Empty<InvitationContact>();
        }

        try
        {
            var protectedText = File.ReadAllText(FilePath, Encoding.UTF8);
            if (!protectedText.StartsWith(FilePrefix, StringComparison.Ordinal))
            {
                throw new InvalidDataException("連絡先ファイルの形式またはバージョンが正しくありません。");
            }

            var json = DpapiStringProtector.Unprotect(protectedText[FilePrefix.Length..]);
            var document = JsonSerializer.Deserialize<ContactDocument>(json, JsonOptions)
                ?? throw new InvalidDataException("連絡先ファイルが空です。");
            if (document.Version != 1 || document.Contacts is null || document.Contacts.Length > MaximumContacts)
            {
                throw new InvalidDataException("連絡先ファイルのバージョンまたは件数が不正です。");
            }

            return NormalizeContacts(document.Contacts);
        }
        catch (InvalidDataException)
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException or JsonException or FormatException or
                                          Win32Exception or CryptographicException or ArgumentException)
        {
            throw new InvalidDataException("連絡先ファイルを読み取れません。破損しているか、別のWindowsユーザーで保護されています。", exception);
        }
    }

    private void SaveCore(IReadOnlyList<InvitationContact> contacts)
    {
        if (contacts.Count == 0)
        {
            if (File.Exists(FilePath))
            {
                File.Delete(FilePath);
            }

            return;
        }

        var directory = Path.GetDirectoryName(FilePath)
            ?? throw new InvalidOperationException("連絡先ファイルの保存先を決定できません。");
        Directory.CreateDirectory(directory);

        var document = new ContactDocument(1, contacts.ToArray());
        var json = JsonSerializer.Serialize(document, JsonOptions);
        var protectedText = FilePrefix + DpapiStringProtector.Protect(json);
        var temporaryPath = FilePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            using (var writer = new StreamWriter(stream, new UTF8Encoding(false)))
            {
                writer.Write(protectedText);
                writer.Flush();
                stream.Flush(flushToDisk: true);
            }

            File.Move(temporaryPath, FilePath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    private static IReadOnlyList<InvitationContact> NormalizeContacts(IEnumerable<InvitationContact> contacts)
    {
        var normalized = contacts.Select(contact =>
        {
            if (contact is null)
            {
                throw new ArgumentException("空の連絡先は保存できません。", nameof(contacts));
            }

            return NormalizeContact(contact.DisplayName, contact.RegistrationCode);
        }).ToArray();

        if (normalized.Length > MaximumContacts)
        {
            throw new ArgumentException($"連絡先は{MaximumContacts}件まで保存できます。", nameof(contacts));
        }

        if (normalized.Select(contact => contact.RegistrationCode)
            .Distinct(StringComparer.Ordinal).Count() != normalized.Length)
        {
            throw new ArgumentException("同じ登録コードの連絡先が重複しています。", nameof(contacts));
        }

        return normalized;
    }

    private static InvitationContact NormalizeContact(string displayName, string registrationCode)
    {
        var name = displayName?.Trim();
        if (string.IsNullOrWhiteSpace(name) || name.Length > MaximumDisplayNameLength || name.Any(char.IsControl))
        {
            throw new ArgumentException($"表示名は1～{MaximumDisplayNameLength}文字で、制御文字を含めないでください。", nameof(displayName));
        }

        if (!InvitationCodeService.TryRestore(registrationCode, out var invitationUrl))
        {
            throw new ArgumentException("有効なEZC1登録コードを指定してください。", nameof(registrationCode));
        }

        var canonicalCode = InvitationCodeService.CreateStructuralCode(invitationUrl);
        return new InvitationContact(name, canonicalCode);
    }

    private sealed record ContactDocument(int Version, InvitationContact[] Contacts);
}
