using GentleSuite.Application.DTOs;
using GentleSuite.Application.Interfaces;
using GentleSuite.Infrastructure.Identity;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace GentleSuite.Infrastructure.Services;

public class UserServiceImpl : IUserService
{
    private readonly UserManager<AppUser> _um;
    private readonly IFileStorageService _fs;
    public UserServiceImpl(UserManager<AppUser> um, IFileStorageService fs) { _um = um; _fs = fs; }

    /// <summary>Max. Größe eines Profilbilds.</summary>
    public const long MaxAvatarBytes = 2 * 1024 * 1024;

    /// <summary>Relative API-URL (ohne "/api"-Präfix) für das Profilbild, oder null. Der v-Parameter ist der
    /// Dateiname (GUID) und dient nur dem Cache-Busting nach einem Upload — der Serverpfad wird nicht preisgegeben.</summary>
    public static string? AvatarUrlFor(AppUser u) =>
        string.IsNullOrEmpty(u.AvatarPath) ? null : $"/profile/avatar/{Uri.EscapeDataString(u.Id)}?v={Path.GetFileNameWithoutExtension(u.AvatarPath)}";

    private static UserListDto ToDto(AppUser u, IEnumerable<string> roles) =>
        new(u.Id, u.Email!, u.FirstName, u.LastName, u.IsActive, roles.ToList(), AvatarUrlFor(u));

    public async Task<UserListDto> GetByIdAsync(string id, CancellationToken ct)
    {
        var user = await _um.FindByIdAsync(id) ?? throw new KeyNotFoundException("Benutzer nicht gefunden");
        return ToDto(user, await _um.GetRolesAsync(user));
    }

    public async Task<UserListDto> SetAvatarAsync(string id, Stream stream, long length, CancellationToken ct)
    {
        var user = await _um.FindByIdAsync(id) ?? throw new KeyNotFoundException("Benutzer nicht gefunden");
        if (length <= 0) throw new ArgumentException("Die Datei ist leer.");
        if (length > MaxAvatarBytes) throw new ArgumentException("Das Profilbild darf maximal 2 MB groß sein.");

        // Inhalt puffern (max. 2 MB) und den Typ anhand der Magic Bytes bestimmen — Dateiname und
        // Content-Type des Clients werden bewusst ignoriert.
        using var ms = new MemoryStream();
        await stream.CopyToAsync(ms, ct);
        if (ms.Length > MaxAvatarBytes) throw new ArgumentException("Das Profilbild darf maximal 2 MB groß sein.");
        var (ext, contentType) = DetectImage(ms.GetBuffer().AsSpan(0, (int)ms.Length))
            ?? throw new ArgumentException("Nur JPG-, PNG- oder WebP-Bilder sind erlaubt.");
        ms.Position = 0;

        // Wie beim Firmenlogo (api/settings/logo): Datei über IFileStorageService ablegen, Pfad am Datensatz merken.
        // Die vorherige Datei wird nicht gelöscht (gleiches Verhalten wie beim Logo).
        var path = await _fs.UploadAsync(ms, $"avatar{ext}", contentType, ct);
        user.AvatarPath = path;
        var res = await _um.UpdateAsync(user);
        if (!res.Succeeded) throw new InvalidOperationException(string.Join(", ", res.Errors.Select(e => e.Description)));
        return ToDto(user, await _um.GetRolesAsync(user));
    }

    public async Task<UserListDto> RemoveAvatarAsync(string id, CancellationToken ct)
    {
        var user = await _um.FindByIdAsync(id) ?? throw new KeyNotFoundException("Benutzer nicht gefunden");
        if (user.AvatarPath != null)
        {
            user.AvatarPath = null;
            var res = await _um.UpdateAsync(user);
            if (!res.Succeeded) throw new InvalidOperationException(string.Join(", ", res.Errors.Select(e => e.Description)));
        }
        return ToDto(user, await _um.GetRolesAsync(user));
    }

    public async Task<(Stream Stream, string ContentType)?> GetAvatarAsync(string id, CancellationToken ct)
    {
        var user = await _um.FindByIdAsync(id);
        if (user == null || string.IsNullOrEmpty(user.AvatarPath)) return null;
        var contentType = Path.GetExtension(user.AvatarPath).ToLowerInvariant() switch
        {
            ".png" => "image/png",
            ".webp" => "image/webp",
            _ => "image/jpeg",
        };
        try { return (await _fs.DownloadAsync(user.AvatarPath, ct), contentType); }
        catch (FileNotFoundException) { return null; }
        catch (DirectoryNotFoundException) { return null; }
    }

    private static (string Ext, string ContentType)? DetectImage(ReadOnlySpan<byte> b)
    {
        if (b.Length >= 3 && b[0] == 0xFF && b[1] == 0xD8 && b[2] == 0xFF) return (".jpg", "image/jpeg");
        if (b.Length >= 8 && b[0] == 0x89 && b[1] == 0x50 && b[2] == 0x4E && b[3] == 0x47 && b[4] == 0x0D && b[5] == 0x0A && b[6] == 0x1A && b[7] == 0x0A) return (".png", "image/png");
        if (b.Length >= 12 && b[0] == (byte)'R' && b[1] == (byte)'I' && b[2] == (byte)'F' && b[3] == (byte)'F'
            && b[8] == (byte)'W' && b[9] == (byte)'E' && b[10] == (byte)'B' && b[11] == (byte)'P') return (".webp", "image/webp");
        return null;
    }

    public async Task<List<UserListDto>> GetAllAsync(CancellationToken ct)
    {
        var users = await _um.Users.ToListAsync(ct);
        var result = new List<UserListDto>();
        foreach (var u in users)
        {
            var roles = await _um.GetRolesAsync(u);
            result.Add(ToDto(u, roles));
        }
        return result;
    }

    public async Task<UserListDto> CreateAsync(CreateUserRequest req, CancellationToken ct)
    {
        var user = new AppUser { UserName = req.Email, Email = req.Email, FirstName = req.FirstName, LastName = req.LastName, IsActive = true, EmailConfirmed = true };
        var result = await _um.CreateAsync(user, req.Password);
        if (!result.Succeeded) throw new InvalidOperationException(string.Join(", ", result.Errors.Select(e => e.Description)));
        if (req.Roles?.Any() == true) await _um.AddToRolesAsync(user, req.Roles);
        var roles = await _um.GetRolesAsync(user);
        return ToDto(user, roles);
    }

    public async Task<UserListDto> UpdateAsync(string id, UpdateUserRequest req, CancellationToken ct)
    {
        var user = await _um.FindByIdAsync(id) ?? throw new KeyNotFoundException("Benutzer nicht gefunden");
        user.FirstName = req.FirstName; user.LastName = req.LastName; user.IsActive = req.IsActive;
        await _um.UpdateAsync(user);
        var currentRoles = await _um.GetRolesAsync(user);
        await _um.RemoveFromRolesAsync(user, currentRoles);
        if (req.Roles?.Any() == true) await _um.AddToRolesAsync(user, req.Roles);
        var roles = await _um.GetRolesAsync(user);
        return ToDto(user, roles);
    }

    public async Task DeleteAsync(string id, CancellationToken ct)
    {
        var user = await _um.FindByIdAsync(id) ?? throw new KeyNotFoundException("Benutzer nicht gefunden");
        user.IsActive = false;
        await _um.UpdateAsync(user);
    }

    public async Task ResetPasswordAsync(string id, ResetPasswordRequest req, CancellationToken ct)
    {
        var user = await _um.FindByIdAsync(id) ?? throw new KeyNotFoundException("Benutzer nicht gefunden");
        var token = await _um.GeneratePasswordResetTokenAsync(user);
        var result = await _um.ResetPasswordAsync(user, token, req.NewPassword);
        if (!result.Succeeded) throw new InvalidOperationException(string.Join(", ", result.Errors.Select(e => e.Description)));
    }
}
