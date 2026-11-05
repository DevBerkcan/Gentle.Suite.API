using System.Security.Cryptography;
using System.Text;
using GentleSuite.Domain.Entities;
using GentleSuite.Domain.Enums;
using GentleSuite.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace GentleSuite.Infrastructure.Services;

/// <summary>
/// Verschlüsselt Kundennotizen vom Typ „Zugangsdaten“ (AES-256-GCM) mit einem Schlüssel aus der Server-Konfiguration
/// (<c>Security:NotesEncryptionKey</c>, 32 Byte Base64). Der Schlüssel liegt bewusst NICHT in der Datenbank, damit ein
/// DB-Backup allein die Zugänge nicht preisgibt.
/// Ohne konfigurierten Schlüssel verhält sich alles wie bisher (Klartext) – es geht nie etwas kaputt, es wird nur gewarnt.
/// Gespeichertes Format: <c>enc:v1:</c> + Base64(nonce[12] | tag[16] | ciphertext).
/// </summary>
public class NoteSecretProtector
{
    public const string Prefix = "enc:v1:";
    public const string ConfigKey = "Security:NotesEncryptionKey";
    private readonly byte[]? _key;

    public NoteSecretProtector(IConfiguration config, ILogger<NoteSecretProtector> log)
    {
        var raw = config[ConfigKey];
        if (string.IsNullOrWhiteSpace(raw)) { log.LogWarning("{Key} ist nicht gesetzt – Zugangsdaten-Notizen werden unverschlüsselt gespeichert.", ConfigKey); return; }
        try
        {
            var key = Convert.FromBase64String(raw.Trim());
            if (key.Length != 32) throw new FormatException("Schlüssel muss 32 Byte lang sein.");
            _key = key;
        }
        catch (FormatException ex) { log.LogError(ex, "{Key} ist ungültig (erwartet: 32 Byte Base64) – Zugangsdaten-Notizen werden unverschlüsselt gespeichert.", ConfigKey); }
    }

    public bool IsEnabled => _key != null;
    public static bool IsProtected(string? value) => value != null && value.StartsWith(Prefix, StringComparison.Ordinal);

    public string Protect(string plain)
    {
        if (_key == null || IsProtected(plain)) return plain;
        var data = Encoding.UTF8.GetBytes(plain);
        var nonce = RandomNumberGenerator.GetBytes(12);
        var tag = new byte[16];
        var cipher = new byte[data.Length];
        using (var aes = new AesGcm(_key, 16)) aes.Encrypt(nonce, data, cipher, tag);
        var blob = new byte[12 + 16 + cipher.Length];
        nonce.CopyTo(blob, 0); tag.CopyTo(blob, 12); cipher.CopyTo(blob, 28);
        return Prefix + Convert.ToBase64String(blob);
    }

    public string Unprotect(string stored)
    {
        if (!IsProtected(stored)) return stored;
        if (_key == null) return "[Verschlüsselt – Schlüssel auf dem Server fehlt]";
        try
        {
            var blob = Convert.FromBase64String(stored[Prefix.Length..]);
            var cipher = blob.AsSpan(28);
            var plain = new byte[cipher.Length];
            using var aes = new AesGcm(_key, 16);
            aes.Decrypt(blob.AsSpan(0, 12), cipher, blob.AsSpan(12, 16), plain);
            return Encoding.UTF8.GetString(plain);
        }
        catch (Exception ex) when (ex is FormatException or CryptographicException)
        {
            return "[Verschlüsselt – Entschlüsselung fehlgeschlagen, falscher Schlüssel?]";
        }
    }

    /// <summary>Speicherwert für eine Notiz: nur Zugangsdaten werden verschlüsselt, alle anderen Typen bleiben Klartext.</summary>
    public string ForStorage(NoteType type, string plain) => type == NoteType.Credential ? Protect(plain) : plain;

    /// <summary>
    /// Einmalige, idempotente Nachverschlüsselung bestehender Klartext-Zugangsdaten. Jede Notiz wird vor dem Schreiben
    /// per Rundlauf (verschlüsseln → entschlüsseln → vergleichen) geprüft; schlägt das fehl, bleibt sie unverändert.
    /// Ändert nur <c>Content</c>, löscht nichts.
    /// </summary>
    public async Task<int> EncryptExistingAsync(AppDbContext db, ILogger log, CancellationToken ct = default)
    {
        if (_key == null) return 0;
        var plainNotes = await db.CustomerNotes.IgnoreQueryFilters()
            .Where(n => n.Type == NoteType.Credential && !n.Content.StartsWith(Prefix))
            .ToListAsync(ct);
        var done = 0;
        foreach (var n in plainNotes)
        {
            var enc = Protect(n.Content);
            if (Unprotect(enc) != n.Content) { log.LogError("Rundlauf-Prüfung für Notiz {Id} fehlgeschlagen – bleibt unverändert.", n.Id); continue; }
            n.Content = enc;
            done++;
        }
        if (done > 0) await db.SaveChangesAsync(ct);
        return done;
    }
}
